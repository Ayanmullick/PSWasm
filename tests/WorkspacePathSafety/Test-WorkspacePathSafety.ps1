[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $Root 'tools/WorkspacePathSafety.ps1')
$EvidenceParent = Assert-WorkspacePathSafe (Join-Path $Root '.WorkDir/TestResults/PathSafety') $Root
$EvidenceRoot = Join-Path $EvidenceParent ([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssZ') + "-$PID")
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Refusing to reuse test evidence: $EvidenceRoot" }
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$Checks = [Collections.Generic.List[object]]::new()
$Report = [ordered]@{ startedAt = [DateTimeOffset]::UtcNow; status = 'failed'; checks = $Checks; errors = @();
    cloudValidation = 'Simulated cloud-tag metadata; local file availability is a user-managed prerequisite.' }
$script:ProductionMetadata = ${function:Get-WorkspacePathMetadata}
$script:MetadataQueries = [Collections.Generic.List[string]]::new()
$PathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$script:MetadataOverrides = [Collections.Generic.Dictionary[string,object]]::new($PathComparer)
$OwnedLinks = [Collections.Generic.List[string]]::new()

function Assert-Check([string]$Name, [bool]$Condition, [string]$Kind = 'real-files') {
    $Checks.Add([ordered]@{ name = $Name; status = $(if ($Condition) { 'passed' } else { 'failed' }); kind = $Kind })
    if (-not $Condition) { throw "Assertion failed: $Name" }
}

function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Kind = 'real-files') {
    $Rejected = $false
    try { & $Action | Out-Null } catch { $Rejected = $true }
    Assert-Check $Name $Rejected $Kind
}

function New-Metadata([uint32]$Attributes = 0, [uint32]$ReparseTag = 0) {
    [pscustomobject]@{ Attributes = $Attributes; ReparseTag = $ReparseTag }
}

function Invoke-MetadataScenario([scriptblock]$Action) {
    $script:MetadataQueries.Clear()
    Set-Item -LiteralPath Function:script:Get-WorkspacePathMetadata -Value {
        param([string]$LiteralPath)
        $FullPath = [IO.Path]::GetFullPath($LiteralPath)
        $script:MetadataQueries.Add($FullPath)
        if ($script:MetadataOverrides.ContainsKey($FullPath)) {
            $Value = $script:MetadataOverrides[$FullPath]
            if ($Value -is [Exception]) { throw $Value }
            return $Value
        }
        & $script:ProductionMetadata -LiteralPath $LiteralPath
    }
    try { & $Action } finally {
        Set-Item -LiteralPath Function:script:Get-WorkspacePathMetadata -Value $script:ProductionMetadata
        $script:MetadataOverrides.Clear()
    }
}

function New-OwnedDirectory([string]$RelativePath) {
    $Path = Assert-WorkspacePathSafe (Join-Path $EvidenceRoot $RelativePath) $EvidenceRoot
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $Path
}

function Import-PublisherFunctions {
    $Publisher = Join-Path $Root 'tools/Publish-BrowserFlavors.ps1'
    $Tokens,$ParseErrors = $null,$null
    $script:PublisherAst = [Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$Tokens, [ref]$ParseErrors)
    if ($ParseErrors.Count) { throw "Publisher parse errors: $($ParseErrors.Message -join '; ')" }
    foreach ($Name in 'Resolve-InRepoPath','Copy-BrowserPackage') {
        $Definition = $script:PublisherAst.Find({ param($Node)
            $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq $Name
        }, $false)
        if (-not $Definition) { throw "Publisher helper not found: $Name" }
        Set-Item -LiteralPath "Function:script:$Name" -Value ([scriptblock]::Create($Definition.Body.Extent.Text.Trim('{}')))
    }
}

function Invoke-PublisherPreflight([string]$Output, [string]$Hosted = '', [string[]]$SelectedFlavors = @('core','web')) {
    # Execute the actual preflight statements, stopping before the publish/tool section even if a regression lets them pass.
    $OutputRoot,$HostedRoot,$HostedVersion,$Flavor = $Output,$Hosted,'',$SelectedFlavors
    $Statements = @($script:PublisherAst.EndBlock.Statements)
    $Start,$Finish = -1,-1
    for ($Index = 0; $Index -lt $Statements.Count; $Index++) {
        $Statement = $Statements[$Index]
        if ($Statement -isnot [Management.Automation.Language.AssignmentStatementAst]) { continue }
        if ($Statement.Left.Extent.Text -eq '$OutputRoot' -and $Start -lt 0) { $Start = $Index }
        if ($Statement.Left.Extent.Text -eq '$Project') { $Finish = $Index; break }
    }
    if ($Start -lt 0 -or $Finish -le $Start) { throw 'Cannot identify publisher preflight; test must be updated.' }
    $Text = ($Statements[$Start..($Finish - 1)] | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
    . ([scriptblock]::Create($Text))
}

try {
    # Exact values are documented by Microsoft; these synthetic cases do not create or alter any real cloud tags.
    # https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4
    $CloudBase = [Convert]::ToUInt32('9000001A',16)
    foreach ($Number in 0..15) {
        $Tag = [uint32]($CloudBase + $Number * 0x1000)
        Assert-Check ('Accept documented CLOUD tag 0x{0:X8}' -f $Tag) (Test-WorkspaceCloudTag $Tag) 'simulated-metadata'
        if ($IsWindows) {
            Assert-WorkspaceEntrySafe $EvidenceRoot (New-Metadata 0x410 $Tag)
            Assert-Check ('Allow CLOUD entry 0x{0:X8}' -f $Tag) $true 'simulated-metadata'
        }
    }
    foreach ($Hex in '00000000','9000001B','9000081A','9001001A','80000021','A0000003','A000000C','A000001D','B000001A') {
        $Tag = [Convert]::ToUInt32($Hex,16)
        Assert-Check "Reject non-allowlisted tag 0x$Hex" (-not (Test-WorkspaceCloudTag $Tag)) 'simulated-metadata'
        Assert-Rejected "Reject reparse entry with tag 0x$Hex" {
            Assert-WorkspaceEntrySafe $EvidenceRoot (New-Metadata 0x410 $Tag)
        } 'simulated-metadata'
    }
    Assert-Rejected 'Missing metadata is not interpreted as a safe entry' {
        Assert-WorkspaceEntrySafe $EvidenceRoot $null
    } 'simulated-metadata'
    if ($IsWindows) {
        $AliasMetadata = [pscustomobject]@{ Attributes = [uint32]0; ReparseTag = [uint32]0; Name = 'canonical-name.txt' }
        Assert-Rejected 'Short-name alias cannot bypass canonical path containment' {
            Assert-WorkspaceEntrySafe (Join-Path $EvidenceRoot 'CANONI~1.TXT') $AliasMetadata
        } 'simulated-metadata'
        Assert-WorkspaceEntrySafe (Join-Path $EvidenceRoot 'CANONICAL-NAME.TXT') $AliasMetadata
        Assert-Check 'Canonical metadata name comparison is case-insensitive on Windows' $true 'simulated-metadata'
    }

    $Ordinary = New-OwnedDirectory 'ordinary/child'
    $File = Join-Path $Ordinary 'asset.txt'
    [IO.File]::WriteAllText($File, 'workspace path safety fixture')
    $Metadata = Get-WorkspacePathMetadata $File
    Assert-Check 'Native metadata reads an ordinary file' ($null -ne $Metadata -and -not ($Metadata.Attributes -band 0x400))
    Assert-Check 'Workspace root itself is accepted' ((Assert-WorkspacePathSafe $EvidenceRoot $EvidenceRoot) -eq $EvidenceRoot)
    Assert-Check 'Existing ordinary file is accepted' ((Assert-WorkspacePathSafe $File $EvidenceRoot) -eq $File)
    Assert-Rejected 'An existing file cannot serve as the workspace root' { Assert-WorkspacePathSafe $File $File }
    Assert-WorkspaceTreeSafe (Join-Path $EvidenceRoot 'ordinary') $EvidenceRoot
    Assert-Check 'Ordinary directory tree is accepted' $true
    $Missing = Join-Path $EvidenceRoot 'new/child/output'
    Assert-Check 'Missing output descendants are accepted without creating them' (
        (Assert-WorkspacePathSafe $Missing $EvidenceRoot) -eq $Missing -and -not (Test-Path -LiteralPath $Missing))
    Assert-WorkspaceTreeSafe $Missing $EvidenceRoot
    Assert-Check 'Missing output tree is accepted without creating it' (-not (Test-Path -LiteralPath $Missing))
    Assert-Rejected 'Nonexistent workspace root is rejected' { Assert-WorkspacePathSafe $Missing $Missing }
    Assert-Rejected 'Sibling prefix cannot escape workspace containment' {
        Assert-WorkspacePathSafe ($EvidenceRoot + '-sibling/file') $EvidenceRoot
    }
    Assert-Rejected 'Parent traversal cannot escape workspace containment' {
        Assert-WorkspacePathSafe (Join-Path $EvidenceRoot '../outside') $EvidenceRoot
    }
    Assert-Rejected 'A file cannot act as an output directory ancestor' {
        Assert-WorkspacePathSafe (Join-Path $File 'child') $EvidenceRoot
    }
    if ($IsWindows) {
        foreach ($Relative in 'file:stream','NUL','CON.txt','LPT1','COM1.txt','child.','child ','file*','file?') {
            Assert-Rejected "Reject ambiguous Windows path $Relative" {
                Assert-WorkspacePathSafe (Join-Path $EvidenceRoot $Relative) $EvidenceRoot
            }
        }
        $BracketFile = Join-Path $Ordinary '[asset].txt'
        [IO.File]::WriteAllText($BracketFile, 'literal brackets are ordinary filename characters')
        Assert-Check 'Literal bracket filename is accepted without wildcard expansion' (
            (Assert-WorkspacePathSafe $BracketFile $EvidenceRoot) -eq $BracketFile)
        $LongNamedFile = Join-Path $Ordinary 'ordinary-long-file-name.txt'
        [IO.File]::WriteAllText($LongNamedFile, 'long canonical filename')
        Assert-Check 'Ordinary long canonical filename is accepted' (
            (Assert-WorkspacePathSafe $LongNamedFile $EvidenceRoot) -eq $LongNamedFile)
        Assert-Rejected 'Device-path prefix cannot bypass workspace containment' {
            Assert-WorkspacePathSafe ('\\?\' + $File) $EvidenceRoot
        }
    }
    if (-not $IsWindows) {
        Assert-Rejected 'Case-distinct non-Windows path cannot bypass containment' {
            Assert-WorkspacePathSafe ($EvidenceRoot.ToUpperInvariant() + '/child') $EvidenceRoot
        }
        Assert-Rejected 'Cloud-looking tag is not accepted off Windows' {
            Assert-WorkspaceEntrySafe $EvidenceRoot (New-Metadata 0x410 $CloudBase)
        } 'simulated-metadata'
    }

    $Ancestor = New-OwnedDirectory 'simulated/ancestor'
    $Descendant = New-OwnedDirectory 'simulated/ancestor/descendant'
    $Leaf = Join-Path $Descendant 'future.txt'
    [IO.File]::WriteAllText($Leaf, 'descendant fixture')
    if ($IsWindows) {
        $script:MetadataOverrides[$EvidenceRoot] = New-Metadata 0x410 ([uint32]($CloudBase + 14 * 0x1000))
        $script:MetadataOverrides[$Ancestor] = New-Metadata 0x410 $CloudBase
        $script:MetadataOverrides[$Descendant] = New-Metadata 0x410 ([uint32]($CloudBase + 15 * 0x1000))
        Invoke-MetadataScenario {
            Assert-WorkspacePathSafe $Leaf $EvidenceRoot | Out-Null
            Assert-WorkspaceTreeSafe $Ancestor $EvidenceRoot
            Assert-Check 'Cloud root, ancestor and descendant pass the real traversal' $true 'simulated-metadata'
            Assert-Check 'Cloud descendant metadata was inspected' ($script:MetadataQueries.Contains($Descendant)) 'simulated-metadata'
        }
    }
    $script:MetadataOverrides[$EvidenceRoot] = New-Metadata 0x410 ([Convert]::ToUInt32('A0000003',16))
    Invoke-MetadataScenario {
        Assert-Rejected 'Redirecting workspace root is rejected before descendant lookup' {
            Assert-WorkspacePathSafe $Leaf $EvidenceRoot
        } 'simulated-metadata'
        Assert-Check 'Root rejection did not query anything below it' (
            $script:MetadataQueries.Count -eq 1 -and $script:MetadataQueries[0] -eq $EvidenceRoot) 'simulated-metadata'
    }
    $script:MetadataOverrides[$Ancestor] = New-Metadata 0x410 ([Convert]::ToUInt32('A000000C',16))
    Invoke-MetadataScenario {
        Assert-Rejected 'Redirecting ancestor is rejected before descendant lookup' {
            Assert-WorkspacePathSafe $Leaf $EvidenceRoot
        } 'simulated-metadata'
        Assert-Check 'Ancestor rejection did not query its descendants' (
            -not $script:MetadataQueries.Contains($Descendant) -and -not $script:MetadataQueries.Contains($Leaf)) 'simulated-metadata'
    }
    $script:MetadataOverrides[$Descendant] = New-Metadata 0x410 ([Convert]::ToUInt32('9001001A',16))
    Invoke-MetadataScenario {
        Assert-Rejected 'Unknown descendant tag rejects the affected tree' {
            Assert-WorkspaceTreeSafe $Ancestor $EvidenceRoot
        } 'simulated-metadata'
    }
    $script:MetadataOverrides[$Descendant] = [IO.IOException]::new('Simulated metadata query failure')
    Invoke-MetadataScenario {
        Assert-Rejected 'Unreadable descendant metadata fails closed' {
            Assert-WorkspaceTreeSafe $Ancestor $EvidenceRoot
        } 'simulated-metadata'
    }
    $script:MetadataOverrides[$EvidenceRoot] = [UnauthorizedAccessException]::new('Simulated metadata access denied')
    Invoke-MetadataScenario {
        Assert-Rejected 'Metadata access denial is not treated as a missing output' {
            Assert-WorkspacePathSafe $Leaf $EvidenceRoot
        } 'simulated-metadata'
        Assert-Check 'Metadata access denial stops at the root' ($script:MetadataQueries.Count -eq 1) 'simulated-metadata'
    }
    Import-PublisherFunctions
    $RepoRoot,$WorkRoot = $Root,(Join-Path $Root '.WorkDir')
    $PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $CopySource = New-OwnedDirectory 'copy/source'
    $CopyDestination = New-OwnedDirectory 'copy/destination'
    [IO.File]::WriteAllText((Join-Path $CopySource 'asset.txt'), 'copied fixture')
    [IO.File]::WriteAllText((Join-Path $CopyDestination 'old.txt'), 'replace only this owned fixture')
    Copy-BrowserPackage $CopySource $CopyDestination
    Assert-Check 'Publisher safely replaces ordinary test-owned output' (
        [IO.File]::ReadAllText((Join-Path $CopyDestination 'asset.txt')) -ceq 'copied fixture' -and
        -not (Test-Path -LiteralPath (Join-Path $CopyDestination 'old.txt')))
    Assert-Rejected 'Publisher copy rejects equal source and destination' { Copy-BrowserPackage $CopySource $CopySource }
    Assert-Rejected 'Publisher copy rejects nested destination' { Copy-BrowserPackage $CopySource (Join-Path $CopySource 'nested') }
    Assert-Rejected 'Publisher copy rejects an ancestor destination' {
        Copy-BrowserPackage $CopySource ([IO.Path]::GetDirectoryName($CopySource))
    }
    Assert-Rejected 'Publisher rejects an output-root parent as a dedicated destination' {
        Resolve-InRepoPath (Join-Path $WorkRoot 'TestResults') 'Fixture'
    }
    Assert-Rejected 'Publisher rejects a sibling-prefix output destination' {
        Resolve-InRepoPath (Join-Path $WorkRoot 'TestResults-other/fixture') 'Fixture'
    }
    Assert-Rejected 'Publisher rejects normalized traversal above its dedicated output roots' {
        Resolve-InRepoPath (Join-Path $WorkRoot 'TestResults/../fixture') 'Fixture'
    }
    if ($IsWindows) {
        Assert-Rejected 'Publisher rejects trailing-dot aliases before destination access' {
            Resolve-InRepoPath ($CopyDestination + '.') 'Fixture'
        }
    }
    Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher')
    Assert-Check 'Publisher preflight accepts ordinary dedicated output paths without publishing' $true
    Assert-Rejected 'Publisher top-level preflight rejects hosted/output overlap' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'overlap') (Join-Path $EvidenceRoot 'overlap')
    }
    Assert-Rejected 'Publisher top-level preflight rejects nested hosted/flavor overlap' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'overlap') (Join-Path $EvidenceRoot 'overlap/core/hosted')
    }

    $SelectedRoot = New-OwnedDirectory 'publisher'
    $FirstFlavor = New-OwnedDirectory 'publisher/core'
    $SecondFlavor = New-OwnedDirectory 'publisher/web'
    $Sentinel = Join-Path $FirstFlavor 'preserve.txt'
    [IO.File]::WriteAllText($Sentinel, 'earlier selected flavor must remain untouched')
    $JunctionTarget = New-OwnedDirectory 'junction-target'
    $TargetSentinel = Join-Path $JunctionTarget 'preserve.txt'
    [IO.File]::WriteAllText($TargetSentinel, 'junction target must remain untouched')
    $Junction = Join-Path $SecondFlavor 'redirect'
    $LinkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $LinkType -Path $Junction -Target $JunctionTarget | Out-Null
    $OwnedLinks.Add($Junction)
    $LinkMetadata = Get-WorkspacePathMetadata $Junction
    Assert-Check 'Real link metadata is read without following its target' (
        [bool]($LinkMetadata.Attributes -band 0x400) -and
        (-not $IsWindows -or $LinkMetadata.ReparseTag -eq [Convert]::ToUInt32('A0000003',16)))
    Assert-Rejected 'Real link is rejected even when its target stays within the workspace' {
        Assert-WorkspacePathSafe $Junction $Root
    }
    Assert-Rejected 'Real link descendant prevents recursive tree operations' { Assert-WorkspaceTreeSafe $SecondFlavor $Root }
    Assert-Rejected 'All selected publisher destinations are checked before any flavor is removed' {
        Invoke-PublisherPreflight $SelectedRoot
    }
    Assert-Rejected 'All hosted destinations are checked before any selected flavor is removed' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') $SelectedRoot
    }
    Assert-Check 'Earlier selected flavor survives later-flavor preflight rejection' (
        [IO.File]::ReadAllText($Sentinel) -ceq 'earlier selected flavor must remain untouched')
    Assert-Rejected 'Publisher refuses to replace a destination containing a link' { Copy-BrowserPackage $CopySource $SecondFlavor }
    Assert-Rejected 'Publisher refuses to copy a source containing a link' { Copy-BrowserPackage $SecondFlavor $CopyDestination }
    Assert-Check 'Rejected copy preserved existing destination' (
        [IO.File]::ReadAllText((Join-Path $CopyDestination 'asset.txt')) -ceq 'copied fixture')
    Assert-Check 'Real link target sentinel is preserved' (
        [IO.File]::ReadAllText($TargetSentinel) -ceq 'junction target must remain untouched')
    $Report.status = 'passed'
} catch {
    $Report.errors = @($_.Exception.Message)
    $Report.errorDetails = $_.ScriptStackTrace
    Write-Warning $_.Exception.Message
} finally {
    Set-Item -LiteralPath Function:script:Get-WorkspacePathMetadata -Value $script:ProductionMetadata
    foreach ($Link in $OwnedLinks) {
        # Remove only our link itself, never recurse into it; retain ordinary fixtures, sentinels and JSON evidence.
        $FullLink = [IO.Path]::GetFullPath($Link)
        if (-not $FullLink.StartsWith($EvidenceRoot + [IO.Path]::DirectorySeparatorChar,
            $(if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }))) {
            throw "Refusing to remove a link outside this test's evidence directory: $FullLink"
        }
        [IO.Directory]::Delete($FullLink)
    }
    $Report.finishedAt = [DateTimeOffset]::UtcNow
    $ReportPath = Join-Path $EvidenceRoot 'results.json'
    [IO.File]::WriteAllText($ReportPath, ($Report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "Workspace path safety: $($Report.status); assertions: $($Checks.Count); evidence: $ReportPath"
}
if ($Report.status -ne 'passed') { exit 1 }
