[CmdletBinding()]
param(
    [string]$WorkerResultsRoot,
    [DateTimeOffset]$WorkerTimestamp = [DateTimeOffset]::UtcNow
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

$FixtureParent = Join-Path $Root '.WorkDir/TestResults/RunIdentity'
$Coordinator = Join-Path $PSScriptRoot 'Invoke-BrowserDomSmoke.ps1'
$Tokens,$ParseErrors = $null,$null
$Ast = [Management.Automation.Language.Parser]::ParseFile($Coordinator, [ref]$Tokens, [ref]$ParseErrors)
if ($ParseErrors.Count) { throw "Coordinator parse errors: $($ParseErrors.Message -join '; ')" }

# Load only the production helpers; never execute the coordinator's publish/server entry point.
foreach ($Name in 'Assert-SmokePath','Assert-SmokeTree','Test-SmokeRunId','New-SmokeRunDirectory','Assert-Report') {
    $Function = $Ast.Find({ param($Node)
        $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq $Name
    }, $false)
    if (-not $Function) { throw "Coordinator helper not found: $Name" }
    . ([scriptblock]::Create($Function.Extent.Text))
}

if ($WorkerResultsRoot) {
    $ResultsRoot = Assert-SmokePath $WorkerResultsRoot $FixtureParent
    New-SmokeRunDirectory -Timestamp $WorkerTimestamp
    exit 0
}

$EvidenceRoot = Assert-SmokePath (Join-Path $FixtureParent (
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))) $FixtureParent
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$Checks = [Collections.Generic.List[object]]::new()
$Workers = [Collections.Generic.List[object]]::new()
$Started = [DateTimeOffset]::UtcNow
$Report = [ordered]@{ startedAt = $Started; status = 'failed'; checks = $Checks; errors = @() }

function Assert-Check([string]$Name, [bool]$Condition) {
    $Status = if ($Condition) { 'passed' } else { 'failed' }
    $Checks.Add([ordered]@{ name = $Name; status = $Status })
    if (-not $Condition) { throw "Assertion failed: $Name" }
}

function Assert-RejectedReport([string]$Name, $ReportValue, $RunValue) {
    $Rejected = $false
    try { Assert-Report $ReportValue $RunValue } catch { $Rejected = $true }
    Assert-Check $Name $Rejected
}

try {
    $Valid = @('20260917-143025Z','20240229-235959Z','20000229-000000Z','20260917-143025Z-02',
        '20260917-143025Z-99','20260917-143025Z-100','20260917-143025Z-999',
        '97d42ce7-7afc-4628-95df-81925c6fd97e','97D42CE7-7AFC-4628-95DF-81925C6FD97E')
    foreach ($Id in $Valid) { Assert-Check "Accept ID $Id" (Test-SmokeRunId $Id) }
    $Invalid = @($null,'','20260229-143025Z','19000229-000000Z','00000101-000000Z','20261301-000000Z',
        '20260431-000000Z','20260917-240000Z','20260917-146000Z','20260917-143060Z','20260917-143025z',
        '20260917-143025Z-00','20260917-143025Z-01','20260917-143025Z-2','20260917-143025Z-002',
        '20260917-143025Z-1000','../20260917-143025Z','..\20260917-143025Z','20260917-143025Z/site',
        '20260917-143025Z\site','20260917-143025Z ',"20260917-143025Z`n",'97d42ce77afc462895df81925c6fd97e',
        '{97d42ce7-7afc-4628-95df-81925c6fd97e}',' 97d42ce7-7afc-4628-95df-81925c6fd97e',
        '97d42ce7-7afc-4628-95df-81925c6fd97e ',"97d42ce7-7afc-4628-95df-81925c6fd97e`n")
    for ($Index = 0; $Index -lt $Invalid.Count; $Index++) {
        Assert-Check "Reject malformed ID $Index" (-not (Test-SmokeRunId $Invalid[$Index]))
    }

    $Now = [DateTimeOffset]::UtcNow
    $SchemaRun = @{ runId = '20260917-143025Z'; expectedChecks = @('fixture');
        startedAt = $Now.AddSeconds(-4).ToString('o'); deadline = $Now.AddMinutes(1).ToString('o') }
    $SchemaReport = @{ schemaVersion = [long]1; runId = $SchemaRun.runId; status = 'passed'; errors = @();
        checks = @(@{ name = 'fixture'; status = 'passed' }); startedAt = $Now.AddSeconds(-3).ToString('o');
        finishedAt = $Now.AddSeconds(-1).ToString('o') }
    Assert-Report $SchemaReport $SchemaRun
    Assert-Check 'Matching timestamp report is accepted' $true
    $Mismatched = $SchemaReport.Clone()
    $Mismatched.runId = '20260917-143025Z-02'
    Assert-RejectedReport 'Report for another run is rejected' $Mismatched $SchemaRun
    $BadSchema = $SchemaReport.Clone()
    $BadSchema.schemaVersion = [long]2
    Assert-RejectedReport 'Unsupported report schema is rejected' $BadSchema $SchemaRun
    $Premature = $SchemaReport.Clone()
    $Premature.startedAt = $Now.AddSeconds(-5).ToString('o')
    Assert-RejectedReport 'Report started before its run is rejected' $Premature $SchemaRun
    $ExpiredRun = $SchemaRun.Clone()
    $ExpiredRun.deadline = $Now.AddSeconds(-2).ToString('o')
    Assert-RejectedReport 'Report finished after the deadline is rejected' $SchemaReport $ExpiredRun
    $LegacyRun,$LegacyReport = $SchemaRun.Clone(),$SchemaReport.Clone()
    $LegacyRun.runId = $LegacyReport.runId = '97d42ce7-7afc-4628-95df-81925c6fd97e'
    Assert-Report $LegacyReport $LegacyRun
    Assert-Check 'Matching legacy GUID report is accepted' $true

    $ResultsRoot = Assert-SmokePath (Join-Path $EvidenceRoot 'collisions') $EvidenceRoot
    $FixedTime = [DateTimeOffset]::Parse('2026-09-17T14:30:25Z', [Globalization.CultureInfo]::InvariantCulture)
    $First = New-SmokeRunDirectory -Timestamp $FixedTime
    Assert-Check 'Readable UTC run directory' ([IO.Path]::GetFileName($First) -ceq '20260917-143025Z')
    $Marker = Join-Path $First 'preserve.txt'
    [IO.File]::WriteAllText($Marker, 'existing run evidence')
    $BlockedCandidate = Join-Path $ResultsRoot '20260917-143025Z-02'
    [IO.File]::WriteAllText($BlockedCandidate, 'existing file')
    $Next = New-SmokeRunDirectory -Timestamp $FixedTime
    Assert-Check 'Directory and file collisions select suffix 03' ([IO.Path]::GetFileName($Next) -ceq '20260917-143025Z-03')
    Assert-Check 'Existing directory content is preserved' ([IO.File]::ReadAllText($Marker) -ceq 'existing run evidence')
    Assert-Check 'Existing colliding file is preserved' ([IO.File]::ReadAllText($BlockedCandidate) -ceq 'existing file')
    $Third = New-SmokeRunDirectory -Timestamp $FixedTime
    Assert-Check 'Repeated allocation remains unique' ([IO.Path]::GetFileName($Third) -ceq '20260917-143025Z-04')
    $OffsetTime = [DateTimeOffset]::Parse('2026-09-18T09:30:25-05:00', [Globalization.CultureInfo]::InvariantCulture)
    $OffsetRun = New-SmokeRunDirectory -Timestamp $OffsetTime
    Assert-Check 'Timestamp is normalized to UTC' ([IO.Path]::GetFileName($OffsetRun) -ceq '20260918-143025Z')

    $RejectedEscape = $false
    try { Assert-SmokePath (Join-Path $ResultsRoot '../outside') $ResultsRoot | Out-Null }
    catch { $RejectedEscape = $true }
    Assert-Check 'Path traversal outside the results root is rejected' $RejectedEscape
    Assert-Check 'Rejected path was not created' (-not (Test-Path -LiteralPath (Join-Path $EvidenceRoot 'outside')))

    foreach ($InvalidPath in @($ResultsRoot, $ResultsRoot + '-other/report.json', '',
        (Join-Path $ResultsRoot '../report.json'))) {
        $Rejected = $false
        try { Assert-SmokePath $InvalidPath $ResultsRoot | Out-Null } catch { $Rejected = $true }
        Assert-Check 'Report path must be a dedicated child of the run results directory' $Rejected
    }
    if ($IsWindows) {
        foreach ($Suffix in @('report.json.', 'report.json ', 'report.json:stream', 'NUL')) {
            $Rejected = $false
            try { Assert-SmokePath (Join-Path $ResultsRoot $Suffix) $ResultsRoot | Out-Null } catch { $Rejected = $true }
            Assert-Check "Ambiguous report path is rejected: $Suffix" $Rejected
        }
    }
    $LinkTarget,$LinkRoot = (Join-Path $EvidenceRoot 'link-target'), (Join-Path $EvidenceRoot 'linked-fixture')
    [IO.Directory]::CreateDirectory($LinkTarget) | Out-Null
    [IO.Directory]::CreateDirectory($LinkRoot) | Out-Null
    $LinkPath = Join-Path $LinkRoot 'redirect'
    $Marker = Join-Path $LinkTarget 'preserve.txt'
    [IO.File]::WriteAllText($Marker, 'preserve linked target')
    $LinkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    try {
        New-Item -ItemType $LinkType -Path $LinkPath -Target $LinkTarget | Out-Null
        $Rejected = $false
        try { Assert-SmokePath (Join-Path $LinkPath 'result.json') $EvidenceRoot | Out-Null } catch { $Rejected = $true }
        Assert-Check 'Report path cannot traverse a linked ancestor' $Rejected
        $Rejected = $false
        try { Assert-SmokeTree $LinkRoot } catch { $Rejected = $true }
        Assert-Check 'Fixture copying rejects descendant links before traversal' $Rejected
        Assert-Check 'Rejected fixture preserves linked target contents' (
            [IO.File]::ReadAllText($Marker) -ceq 'preserve linked target')
    } finally {
        if (Get-Item -LiteralPath $LinkPath -Force -ErrorAction SilentlyContinue) { [IO.Directory]::Delete($LinkPath) }
    }

    $ResultsRoot = Assert-SmokePath (Join-Path $EvidenceRoot 'concurrent') $EvidenceRoot
    for ($Index = 0; $Index -lt 8; $Index++) {
        $StartInfo = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
        $StartInfo.UseShellExecute = $false
        $StartInfo.CreateNoWindow = $true
        $StartInfo.RedirectStandardOutput = $true
        $StartInfo.RedirectStandardError = $true
        foreach ($Argument in @('-NoProfile','-File',$PSCommandPath,'-WorkerResultsRoot',$ResultsRoot,
            '-WorkerTimestamp',$FixedTime.ToString('o'))) { $StartInfo.ArgumentList.Add($Argument) }
        $Process = [Diagnostics.Process]::Start($StartInfo)
        $Workers.Add(@{ process = $Process; output = $Process.StandardOutput.ReadToEndAsync();
            errors = $Process.StandardError.ReadToEndAsync() })
    }
    $ConcurrentPaths = foreach ($Worker in $Workers) {
        if (-not $Worker.process.WaitForExit(30000)) { throw 'Concurrent allocation worker timed out.' }
        $Output,$Errors = $Worker.output.GetAwaiter().GetResult(),$Worker.errors.GetAwaiter().GetResult()
        if ($Worker.process.ExitCode -ne 0) { throw "Concurrent allocation worker failed: $Errors" }
        $Output.Trim()
    }
    Assert-Check 'Eight concurrent allocations produce eight unique directories' (
        @($ConcurrentPaths | Select-Object -Unique).Count -eq 8)
    foreach ($Path in $ConcurrentPaths) {
        Assert-Check "Concurrent directory exists: $([IO.Path]::GetFileName($Path))" (
            (Test-Path -LiteralPath $Path -PathType Container) -and (Test-SmokeRunId ([IO.Path]::GetFileName($Path))))
    }
    $ActualNames = @($ConcurrentPaths | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $ExpectedNames = @('20260917-143025Z') + @(2..8 | ForEach-Object { '20260917-143025Z-' + $_.ToString('00') })
    Assert-Check 'Concurrent allocation uses the first eight collision slots' (
        ($ActualNames -join '|') -ceq ($ExpectedNames -join '|'))

    # Project relative paths against a synthetic 50-character checkout root; no filesystem access.
    $CheckoutRootLength = 50
    $Asset = 'site/_content/Microsoft.DotNet.HotReload.WebAssembly.Browser/' +
        'Microsoft.DotNet.HotReload.WebAssembly.Browser.99zm1jdh75.lib.module.js.gz'
    $Lengths = foreach ($Id in '97d42ce7-7afc-4628-95df-81925c6fd97e','20260917-143025Z','20260917-143025Z-999') {
        $RelativePath = ".WorkDir/TestResults/BrowserDomSmoke/$Id/$Asset"
        [ordered]@{ runId = $Id; relativePath = $RelativePath; characters = $CheckoutRootLength + 1 + $RelativePath.Length }
    }
    Assert-Check 'Legacy GUID asset reaches the long-path threshold' ($Lengths[0].characters -ge 260)
    Assert-Check 'Readable ID saves twenty characters' ($Lengths[0].characters - $Lengths[1].characters -eq 20)
    Assert-Check 'Largest supported collision suffix remains below 248 characters' ($Lengths[2].characters -le 247)
    $Report.pathProjection = $Lengths
    $Report.checkoutRootCharacters = $CheckoutRootLength
    $Report.concurrentRunIds = $ActualNames
    $Report.status = 'passed'
} catch {
    $Report.errors = @($_.Exception.Message)
    Write-Warning $_.Exception.Message
} finally {
    foreach ($Worker in $Workers) {
        if (-not $Worker.process.HasExited) { $Worker.process.Kill($true); $Worker.process.WaitForExit() }
        $Worker.process.Dispose()
    }
    $Report.finishedAt = [DateTimeOffset]::UtcNow
    $ReportPath = Join-Path $EvidenceRoot 'results.json'
    [IO.File]::WriteAllText($ReportPath, ($Report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "Run identity checks: $($Report.status); assertions: $($Checks.Count); evidence: $ReportPath"
}
if ($Report.status -ne 'passed') { exit 1 }
