[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$WorkRoot = Join-Path $RepoRoot '.WorkDir'
$PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$Checks = [Collections.Generic.List[object]]::new()
$OwnedLinks = [Collections.Generic.List[string]]::new()
$Report = [ordered]@{ startedAt = [DateTimeOffset]::UtcNow; status = 'failed'; checks = $Checks; errors = @() }

# Load only production functions and preflight statements; never publish or invoke a native tool in this test.
$Publisher = Join-Path $RepoRoot 'tools/Publish-BrowserFlavors.ps1'
$Tokens,$ParseErrors = $null,$null
$PublisherAst = [Management.Automation.Language.Parser]::ParseFile($Publisher, [ref]$Tokens, [ref]$ParseErrors)
if ($ParseErrors.Count) { throw "Publisher parse errors: $($ParseErrors.Message -join '; ')" }
foreach ($Name in 'ConvertTo-PackagePath','Assert-PackagePath','Assert-PackageTree','Resolve-InRepoPath','Copy-BrowserPackage') {
    $Definition = $PublisherAst.Find({ param($Node)
        $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq $Name
    }, $false)
    if (-not $Definition) { throw "Publisher function not found: $Name" }
    . ([scriptblock]::Create($Definition.Extent.Text))
}
$EvidenceParent = Assert-PackagePath (Join-Path $WorkRoot 'TestResults/Publishing') $RepoRoot
$EvidenceRoot = Join-Path $EvidenceParent ([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssZ') + "-$PID")
if (Test-Path -LiteralPath $EvidenceRoot) { throw "Refusing to reuse test evidence: $EvidenceRoot" }
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null

function Assert-Check([string]$Name, [bool]$Condition) {
    $Checks.Add([ordered]@{ name = $Name; status = $(if ($Condition) { 'passed' } else { 'failed' }) })
    if (-not $Condition) { throw "Assertion failed: $Name" }
}

function Assert-Rejected([string]$Name, [scriptblock]$Action) {
    $Rejected = $false
    try { & $Action | Out-Null } catch { $Rejected = $true }
    Assert-Check $Name $Rejected
}

function New-OwnedDirectory([string]$RelativePath) {
    $Path = Assert-PackagePath (Join-Path $EvidenceRoot $RelativePath) $EvidenceRoot
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $Path
}

function New-OwnedLink([string]$RelativePath, [string]$Target) {
    $Path = Assert-PackagePath (Join-Path $EvidenceRoot $RelativePath) $EvidenceRoot
    $LinkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $LinkType -Path $Path -Target $Target | Out-Null
    $OwnedLinks.Add($Path)
    $Path
}

function Invoke-PublisherPreflight([string]$Output, [string]$Hosted = '', [string]$Version = '',
    [string[]]$SelectedFlavors = @('core','web')) {
    $OutputRoot,$HostedRoot,$HostedVersion,$Flavor = $Output,$Hosted,$Version,$SelectedFlavors
    $Statements = @($PublisherAst.EndBlock.Statements)
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
    $Ordinary = New-OwnedDirectory 'ordinary/child'
    $File = Join-Path $Ordinary 'asset.txt'
    [IO.File]::WriteAllText($File, 'ordinary package asset')
    Assert-Check 'A dedicated directory is a valid boundary' (
        (Assert-PackagePath $EvidenceRoot $EvidenceRoot) -eq $EvidenceRoot)
    Assert-Check 'Existing ordinary package files are accepted' ((Assert-PackagePath $File $EvidenceRoot) -eq $File)
    Assert-PackageTree (Join-Path $EvidenceRoot 'ordinary') $EvidenceRoot
    Assert-Check 'Ordinary package trees are accepted' $true
    $Missing = Join-Path $EvidenceRoot 'new/child/output'
    Assert-Check 'Missing output descendants are accepted without creating them' (
        (Assert-PackagePath $Missing $EvidenceRoot) -eq $Missing -and -not (Test-Path -LiteralPath $Missing))
    Assert-PackageTree $Missing $EvidenceRoot
    Assert-Check 'Missing output trees are accepted without creating them' (-not (Test-Path -LiteralPath $Missing))
    Assert-Rejected 'An empty path is rejected' { ConvertTo-PackagePath '' }
    Assert-Rejected 'A drive root cannot be a deletion boundary' {
        Assert-PackagePath $File ([IO.Path]::GetPathRoot($File))
    }
    Assert-Rejected 'A missing directory cannot be a boundary' { Assert-PackagePath $Missing $Missing }
    Assert-Rejected 'A file cannot be a directory boundary' { Assert-PackagePath $File $File }
    Assert-Rejected 'A file cannot be an output directory ancestor' { Assert-PackagePath (Join-Path $File 'child') $EvidenceRoot }
    Assert-Rejected 'Sibling prefixes cannot bypass containment' {
        Assert-PackagePath ($EvidenceRoot + '-sibling/file') $EvidenceRoot
    }
    Assert-Rejected 'Normalized parent traversal cannot escape containment' {
        Assert-PackagePath (Join-Path $EvidenceRoot '../outside') $EvidenceRoot
    }
    if ($IsWindows) {
        foreach ($Relative in 'file:stream','NUL','CON.txt','LPT1','COM1.txt','child.','child ','file*','file?') {
            Assert-Rejected "Ambiguous Windows path is rejected: $Relative" {
                Assert-PackagePath (Join-Path $EvidenceRoot $Relative) $EvidenceRoot
            }
        }
        Assert-Rejected 'Device-path aliases are rejected' { Assert-PackagePath ('\\?\' + $File) $EvidenceRoot }
        $LongFile = Join-Path $Ordinary 'packagecanonicalfilename.txt'
        [IO.File]::WriteAllText($LongFile, 'canonical filename fixture')
        $ShortFile = Join-Path $Ordinary 'PACKAG~1.TXT'
        if ((Test-Path -LiteralPath $ShortFile -PathType Leaf) -and
            [IO.File]::ReadAllText($ShortFile) -ceq 'canonical filename fixture') {
            $AliasPath,$AliasRejected = $null,$false
            try { $AliasPath = Assert-PackagePath $ShortFile $EvidenceRoot } catch { $AliasRejected = $true }
            Assert-Check 'Short filename aliases are rejected or normalized to the exact canonical path' (
                $AliasRejected -or $AliasPath -ceq $LongFile)
        } else {
            $Checks.Add([ordered]@{ name = 'Short filename aliases are rejected or normalized to the exact canonical path';
                status = 'skipped'; reason = 'The fixture has no matching filesystem short filename.' })
        }
    } else {
        Assert-Rejected 'Case-distinct paths cannot bypass containment' {
            Assert-PackagePath ($EvidenceRoot.ToUpperInvariant() + '/child') $EvidenceRoot
        }
    }
    $BracketFile = Join-Path $Ordinary '[asset].txt'
    [IO.File]::WriteAllText($BracketFile, 'literal brackets')
    Assert-Check 'Literal bracket filenames are inspected without wildcard expansion' (
        (Assert-PackagePath $BracketFile $EvidenceRoot) -eq $BracketFile)

    $CopySource = New-OwnedDirectory 'copy/source'
    $CopyDestination = New-OwnedDirectory 'copy/destination'
    [IO.File]::WriteAllText((Join-Path $CopySource 'asset.txt'), 'copied fixture')
    [IO.File]::WriteAllText((Join-Path $CopyDestination 'old.txt'), 'replace only this owned fixture')
    Copy-BrowserPackage $CopySource $CopyDestination
    Assert-Check 'Copy replaces ordinary output and preserves package content' (
        [IO.File]::ReadAllText((Join-Path $CopyDestination 'asset.txt')) -ceq 'copied fixture' -and
        -not (Test-Path -LiteralPath (Join-Path $CopyDestination 'old.txt')))
    Assert-Rejected 'Copy rejects equal source and destination' { Copy-BrowserPackage $CopySource $CopySource }
    Assert-Rejected 'Copy rejects nested destination' { Copy-BrowserPackage $CopySource (Join-Path $CopySource 'nested') }
    Assert-Rejected 'Copy rejects an ancestor destination' {
        Copy-BrowserPackage $CopySource ([IO.Path]::GetDirectoryName($CopySource))
    }
    if ($IsWindows) {
        $AliasParent = New-OwnedDirectory 'directory-alias'
        $LongDirectory = New-OwnedDirectory 'directory-alias/packagecanonicaldirectory'
        $AliasSentinel = Join-Path $LongDirectory 'preserve.txt'
        [IO.File]::WriteAllText($AliasSentinel, 'directory alias must not hide source overlap')
        $ShortDirectory = Join-Path $AliasParent 'PACKAG~1'
        if ((Test-Path -LiteralPath $ShortDirectory -PathType Container) -and
            [IO.File]::ReadAllText((Join-Path $ShortDirectory 'preserve.txt')) -ceq 'directory alias must not hide source overlap') {
            Assert-Rejected 'Directory alias destinations cannot bypass source overlap checks' {
                Copy-BrowserPackage $LongDirectory $ShortDirectory
            }
            Assert-Rejected 'Directory alias sources cannot bypass destination overlap checks' {
                Copy-BrowserPackage $ShortDirectory $LongDirectory
            }
            Assert-Check 'Directory alias overlap rejection preserves source content' (
                [IO.File]::ReadAllText($AliasSentinel) -ceq 'directory alias must not hide source overlap')
        } else {
            $Checks.Add([ordered]@{ name = 'Directory aliases cannot bypass copy overlap checks';
                status = 'skipped'; reason = 'The fixture has no matching filesystem short directory name.' })
        }
    }
    Assert-Rejected 'Copy rejects missing source before replacing existing output' { Copy-BrowserPackage $Missing $CopyDestination }
    Assert-Check 'Missing source rejection preserves existing output' (
        [IO.File]::ReadAllText((Join-Path $CopyDestination 'asset.txt')) -ceq 'copied fixture')
    Assert-Rejected 'Repository root cannot be a publish destination' { Resolve-InRepoPath $RepoRoot 'Fixture' }
    Assert-Rejected 'Source directories cannot be publish destinations' { Resolve-InRepoPath (Join-Path $RepoRoot 'src') 'Fixture' }
    foreach ($Parent in 'TestResults','build/publish') {
        Assert-Rejected "Output parent itself cannot be a destination: $Parent" {
            Resolve-InRepoPath (Join-Path $WorkRoot $Parent) 'Fixture'
        }
    }
    Assert-Rejected 'Sibling-prefix output destinations are rejected' {
        Resolve-InRepoPath (Join-Path $WorkRoot 'TestResults-other/fixture') 'Fixture'
    }
    Assert-Rejected 'Traversal above dedicated output parents is rejected' {
        Resolve-InRepoPath (Join-Path $WorkRoot 'TestResults/../fixture') 'Fixture'
    }
    if ($IsWindows) {
        Assert-Rejected 'Trailing-dot destination aliases are rejected before access' {
            Resolve-InRepoPath ($CopyDestination + '.') 'Fixture'
        }
    }
    Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher')
    Assert-Check 'Preflight accepts dedicated output without publishing' $true
    Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') (Join-Path $EvidenceRoot 'empty-hosted') 'v-test'
    Assert-Check 'Preflight accepts distinct hosted and versioned output without publishing' $true
    Assert-Rejected 'Preflight rejects equal hosted and flavor output roots' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'overlap') (Join-Path $EvidenceRoot 'overlap')
    }
    Assert-Rejected 'Preflight rejects hosted output nested in a selected flavor' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'overlap') (Join-Path $EvidenceRoot 'overlap/core/hosted')
    }
    Assert-Rejected 'Versioned hosted destinations cannot overlap an unversioned destination' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') (Join-Path $EvidenceRoot 'empty-hosted') 'core'
    }
    Assert-Rejected 'A hosted version requires a hosted root' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') '' 'v-test'
    }

    $SelectedRoot = New-OwnedDirectory 'publisher'
    $FirstFlavor = New-OwnedDirectory 'publisher/core'
    $SecondFlavor = New-OwnedDirectory 'publisher/web'
    $Sentinel = Join-Path $FirstFlavor 'preserve.txt'
    [IO.File]::WriteAllText($Sentinel, 'earlier selected flavor must remain untouched')
    $LinkTarget = New-OwnedDirectory 'link-target'
    $TargetSentinel = Join-Path $LinkTarget 'preserve.txt'
    [IO.File]::WriteAllText($TargetSentinel, 'link target must remain untouched')
    $Link = New-OwnedLink 'publisher/web/redirect' $LinkTarget
    Assert-Rejected 'Links are rejected even when their target is inside the repository' { Assert-PackagePath $Link $RepoRoot }
    Assert-Rejected 'Linked ancestors are rejected before accepting child paths' {
        Assert-PackagePath (Join-Path $Link 'future/child') $RepoRoot
    }
    Assert-Rejected 'A link cannot serve as the operation boundary' { Assert-PackagePath $Link $Link }
    Assert-Rejected 'Linked descendants prevent recursive tree operations' { Assert-PackageTree $SecondFlavor $RepoRoot }
    Assert-Rejected 'All selected destinations are checked before any flavor is removed' { Invoke-PublisherPreflight $SelectedRoot }
    Assert-Rejected 'All hosted destinations are checked before any flavor is removed' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') $SelectedRoot
    }
    Assert-Check 'Earlier selected output survives later-flavor rejection' (
        [IO.File]::ReadAllText($Sentinel) -ceq 'earlier selected flavor must remain untouched')
    Assert-Rejected 'Copy refuses a destination containing a link' { Copy-BrowserPackage $CopySource $SecondFlavor }
    Assert-Rejected 'Copy refuses a source containing a link' { Copy-BrowserPackage $SecondFlavor $CopyDestination }
    Assert-Check 'Rejected source copy preserves existing destination' (
        [IO.File]::ReadAllText((Join-Path $CopyDestination 'asset.txt')) -ceq 'copied fixture')
    Assert-Check 'Rejected operations preserve the link target' (
        [IO.File]::ReadAllText($TargetSentinel) -ceq 'link target must remain untouched')

    $VersionedRoot = New-OwnedDirectory 'versioned'
    New-OwnedDirectory 'versioned/v-test/web' | Out-Null
    New-OwnedLink 'versioned/v-test/web/redirect' $LinkTarget | Out-Null
    Assert-Rejected 'Versioned hosted trees are checked before any flavor is removed' {
        Invoke-PublisherPreflight (Join-Path $EvidenceRoot 'empty-publisher') $VersionedRoot 'v-test'
    }
    $BrokenTarget = New-OwnedDirectory 'broken-target'
    $BrokenLink = New-OwnedLink 'broken-link' $BrokenTarget
    # This is an empty, test-owned directory verified above. Remove only it to leave a real dangling link.
    $BrokenTarget = Assert-PackagePath $BrokenTarget $EvidenceRoot
    [IO.Directory]::Delete($BrokenTarget)
    Assert-Rejected 'Broken links are rejected rather than treated as missing output' { Assert-PackagePath $BrokenLink $RepoRoot }
    Assert-Rejected 'Broken linked ancestors cannot become output directories' {
        Assert-PackagePath (Join-Path $BrokenLink 'future') $RepoRoot
    }
    Assert-Rejected 'Copy refuses a broken destination link before changing its source' { Copy-BrowserPackage $CopySource $BrokenLink }
    Assert-Check 'Broken-link rejection preserves package source content' (
        [IO.File]::ReadAllText((Join-Path $CopySource 'asset.txt')) -ceq 'copied fixture')
    $Report.status = 'passed'
} catch {
    $Report.errors = @($_.Exception.Message)
    $Report.errorDetails = $_.ScriptStackTrace
    Write-Warning $_.Exception.Message
} finally {
    foreach ($Link in $OwnedLinks) {
        # Delete only our link itself. Keep ordinary fixtures and evidence, and never recurse through a link.
        $FullLink = [IO.Path]::GetFullPath($Link)
        if (-not $FullLink.StartsWith($EvidenceRoot + [IO.Path]::DirectorySeparatorChar, $PathComparison)) {
            throw "Refusing to remove a link outside this test's evidence directory: $FullLink"
        }
        [IO.Directory]::Delete($FullLink)
    }
    $Report.finishedAt = [DateTimeOffset]::UtcNow
    $ReportPath = Join-Path $EvidenceRoot 'results.json'
    [IO.File]::WriteAllText($ReportPath, ($Report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $Passed = @($Checks | Where-Object status -eq 'passed').Count
    $Skipped = @($Checks | Where-Object status -eq 'skipped').Count
    Write-Host "Publishing: $($Report.status); assertions passed: $Passed; skipped: $Skipped; evidence: $ReportPath"
}
if ($Report.status -ne 'passed') { exit 1 }
