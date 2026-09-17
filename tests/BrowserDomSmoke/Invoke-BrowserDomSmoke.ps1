[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run')][string]$Configuration = 'Release',
    [Parameter(ParameterSetName = 'Run')][ValidateRange(1,65535)][int]$Port = 5010,
    [Parameter(ParameterSetName = 'Run')][ValidateRange(5,1800)][int]$TimeoutSeconds = 180,
    [Parameter(ParameterSetName = 'Run')][switch]$SkipPublish,
    [Parameter(Mandatory, ParameterSetName = 'ReportJson')][string]$ResultJson,
    [Parameter(Mandatory, ParameterSetName = 'ReportFile')][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ResultsRoot = Join-Path $Root '.WorkDir/TestResults/BrowserDomSmoke'
$WorkspaceCommand = Join-Path $Root 'tools/Invoke-WorkspaceCommand.ps1'
. (Join-Path $Root 'tools/WorkspacePathSafety.ps1')

function Assert-WorkspacePath([string]$Path, [string]$Parent) {
    $FullPath,$Parent = (ConvertTo-WorkspaceFullPath $Path), (ConvertTo-WorkspaceFullPath $Parent)
    $Comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $FullPath.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $Comparison)) {
        throw "Path must be a dedicated child of ${Parent}: $FullPath"
    }
    return Assert-WorkspacePathSafe $FullPath $Root
}

function Test-SmokeRunId($Id) {
    if ($Id -isnot [string]) { return $false }
    $LegacyId = [guid]::Empty
    if ($Id.Length -eq 36 -and [guid]::TryParseExact($Id, 'D', [ref]$LegacyId)) { return $true }
    if ($Id -cnotmatch '\A[0-9]{8}-[0-9]{6}Z(?:-(?:0[2-9]|[1-9][0-9]{1,2}))?\z') { return $false }
    $Timestamp = [datetime]::MinValue
    return [datetime]::TryParseExact($Id.Substring(0, 16), "yyyyMMdd-HHmmss'Z'",
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$Timestamp)
}

function New-SmokeRunDirectory([DateTimeOffset]$Timestamp = [DateTimeOffset]::UtcNow) {
    $null = Assert-WorkspacePath $ResultsRoot $Root
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    $LockPath = Assert-WorkspacePath (Join-Path $ResultsRoot '.run-id.lock') $ResultsRoot
    $Lock,$LockDeadline = $null, [DateTimeOffset]::UtcNow.AddSeconds(5)
    try {
        # Serialize allocation across processes. Keep the empty lock file: deleting it introduces an unlink race.
        while ($null -eq $Lock) {
            try { $Lock = [IO.File]::Open($LockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
            catch [IO.IOException] {
                if ([DateTimeOffset]::UtcNow -ge $LockDeadline) { throw }
                Start-Sleep -Milliseconds 50
            }
        }
        $BaseId = $Timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture)
        for ($Sequence = 1; $Sequence -le 999; $Sequence++) {
            $Id = if ($Sequence -eq 1) { $BaseId } else { '{0}-{1:00}' -f $BaseId, $Sequence }
            $Directory = Assert-WorkspacePath (Join-Path $ResultsRoot $Id) $ResultsRoot
            if (Test-Path -LiteralPath $Directory) { continue }
            [IO.Directory]::CreateDirectory($Directory) | Out-Null
            return $Directory
        }
        throw "No unused browser run name remains for $BaseId. Retry in a later second."
    } finally { if ($null -ne $Lock) { $Lock.Dispose() } }
}

function Write-RunJson([string]$Path, $Value, [switch]$New) {
    $Path = Assert-WorkspacePath $Path $ResultsRoot
    $Temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($Temporary, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($Temporary, $Path, -not $New)
    } finally {
        if (Test-Path -LiteralPath $Temporary) { Remove-Item -LiteralPath $Temporary }
    }
}

function Assert-Report($Report, $Run) {
    if ($Report -isnot [Collections.IDictionary] -or $Report.schemaVersion -isnot [long] -or
        $Report.schemaVersion -ne 1 -or $Report.runId -isnot [string] -or $Report.runId -cne $Run.runId -or
        $Report.status -isnot [string] -or $Report.status -cnotin @('passed','failed')) {
        throw 'The browser report has an invalid schema, run ID, or status.'
    }
    if ($Report.checks -isnot [array] -or $Report.errors -isnot [array]) {
        throw 'The browser report must contain checks and errors arrays.'
    }
    foreach ($Check in $Report.checks) {
        if ($Check -isnot [Collections.IDictionary] -or $Check.name -isnot [string] -or $Check.status -isnot [string]) {
            throw 'Each assertion must be an object with scalar string name and status fields.'
        }
    }
    if (@($Report.errors | Where-Object { $_ -isnot [string] }).Count) { throw 'Report errors must be strings.' }
    $Names = @($Report.checks | ForEach-Object { $_.name })
    if (@($Names | Select-Object -Unique).Count -ne $Names.Count -or
        @($Names | Where-Object { $_ -notin $Run.expectedChecks }).Count -or
        @($Report.checks | Where-Object { $_.status -notin @('passed','failed') }).Count) {
        throw 'The browser report contains duplicate, unknown, or invalid checks.'
    }
    if ($Report.status -eq 'passed' -and ($Report.checks.Count -ne $Run.expectedChecks.Count -or $Report.errors.Count -or
        @($Report.checks | Where-Object status -ne 'passed').Count -or
        ($Names -join '|') -cne ($Run.expectedChecks -join '|'))) {
        throw 'A passing report must contain every expected assertion in order, with no failures or errors.'
    }
    if ($Report.status -eq 'failed' -and -not $Report.errors.Count -and
        -not @($Report.checks | Where-Object status -eq 'failed').Count) {
        throw 'A failing report must identify a failure or error.'
    }
    # ConvertFrom-Json can deserialize ISO timestamps as DateTime; a cast preserves its UTC kind.
    $Started,$Finished = [DateTimeOffset]$Report.startedAt, [DateTimeOffset]$Report.finishedAt
    if ($Started -lt [DateTimeOffset]$Run.startedAt -or $Finished -lt $Started -or
        $Finished -gt [DateTimeOffset]$Run.deadline -or $Finished -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) {
        throw 'Invalid browser report timestamps.'
    }
}

if ($PSCmdlet.ParameterSetName -ne 'Run') {
    if ($PSCmdlet.ParameterSetName -eq 'ReportFile') {
        if (-not [IO.Path]::IsPathRooted($ResultPath)) { $ResultPath = Join-Path (Get-Location).Path $ResultPath }
        $ResultPath = Assert-WorkspacePath $ResultPath $ResultsRoot
        $ResultJson = Get-Content -LiteralPath $ResultPath -Raw
    }
    $Report = $ResultJson | ConvertFrom-Json -AsHashtable
    if (-not (Test-SmokeRunId $Report.runId)) { throw 'Report runId must be a UTC run name or a legacy GUID.' }
    $RunRoot = Assert-WorkspacePath (Join-Path $ResultsRoot $Report.runId) $ResultsRoot
    $RunPath = Assert-WorkspacePath (Join-Path $RunRoot 'run.json') $RunRoot
    $Run = Get-Content -LiteralPath $RunPath -Raw | ConvertFrom-Json -AsHashtable
    if ($Run.state -ne 'waiting' -or [DateTimeOffset]::UtcNow -gt [DateTimeOffset]$Run.deadline) {
        throw 'This browser test run is no longer waiting for a report.'
    }
    Assert-Report $Report $Run
    if ([DateTimeOffset]::UtcNow -gt [DateTimeOffset]$Run.deadline) { throw 'The report deadline has passed.' }
    Write-RunJson (Join-Path $RunRoot 'result.json') $Report -New
    Write-Host "Accepted $($Report.status) browser report for $($Run.runId)."
    if ($Report.status -eq 'passed') { exit 0 } else { exit 1 }
}

$PublishRoot = Assert-WorkspacePath (Join-Path $Root '.WorkDir/build/publish/BrowserHost') $Root
$PublishedSite = Assert-WorkspacePath (Join-Path $PublishRoot 'wwwroot') $PublishRoot
Assert-WorkspaceTreeSafe $PublishRoot $Root
if (-not $SkipPublish) {
    & $WorkspaceCommand dotnet publish (Join-Path $Root 'samples/BrowserHost/PSWasm.BrowserHost.csproj') `
        -c $Configuration -r browser-wasm -o $PublishRoot /p:UseAppHost=false --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishedSite 'app.js') -PathType Leaf)) {
    throw 'Publish BrowserHost before using -SkipPublish.'
}
$ChecksPath = Assert-WorkspacePath (Join-Path $PSScriptRoot 'checks.json') $Root
$ExpectedChecks = @(Get-Content -LiteralPath $ChecksPath -Raw | ConvertFrom-Json)
if (-not $ExpectedChecks.Count) { throw 'The browser assertion manifest is empty.' }

# Check the requested port without taking over a server that belongs to someone else.
$Probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $Probe.Start() } catch { throw "Port $Port is unavailable. Choose a free port with -Port." } finally { $Probe.Stop() }

$RunRoot = New-SmokeRunDirectory
$RunId = [IO.Path]::GetFileName($RunRoot)
$SiteRoot = Assert-WorkspacePath (Join-Path $RunRoot 'site') $RunRoot
Assert-WorkspaceTreeSafe $PublishedSite $Root
New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
Get-ChildItem -LiteralPath $PublishedSite -Force | Copy-Item -Destination $SiteRoot -Recurse
foreach ($Name in @('dom-smoke.html','browser-dom-smoke.mjs','checks.json')) {
    $FixturePath = Assert-WorkspacePath (Join-Path $PSScriptRoot $Name) $Root
    Copy-Item -LiteralPath $FixturePath -Destination (Join-Path $SiteRoot $Name)
}
Assert-WorkspaceTreeSafe $SiteRoot $Root

$Url = "http://127.0.0.1:$Port/dom-smoke.html?runId=$RunId"
$Run = [ordered]@{
    schemaVersion=1; runId=$RunId; state='starting'; startedAt=[DateTimeOffset]::UtcNow.ToString('o')
    deadline=$null; url=$Url; expectedChecks=$ExpectedChecks
}
$RunPath,$ReportPath = (Join-Path $RunRoot 'run.json'), (Join-Path $RunRoot 'result.json')
Write-RunJson $RunPath $Run -New
Write-RunJson (Join-Path $SiteRoot 'smoke-run.json') @{runId=$RunId} -New
$Server,$Stdout,$Stderr = [Diagnostics.Process]::new(), $null, $null
$Server.StartInfo.FileName = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
$Server.StartInfo.WorkingDirectory = $Root
$Server.StartInfo.UseShellExecute = $false
$Server.StartInfo.CreateNoWindow = $true
$Server.StartInfo.RedirectStandardOutput = $true
$Server.StartInfo.RedirectStandardError = $true
foreach ($Argument in @('-NoProfile','-File',$WorkspaceCommand,'dotnet','serve','--directory',$SiteRoot,
    '--port',"$Port",'--address','127.0.0.1','--mime','.mjs=text/javascript','--mime','.ts=text/plain',
    '--headers','Cache-Control: no-store')) {
    $Server.StartInfo.ArgumentList.Add($Argument)
}
$ExitCode = 1
try {
    if (-not $Server.Start()) { throw 'Could not start dotnet serve.' }
    $Stdout,$Stderr = $Server.StandardOutput.ReadToEndAsync(), $Server.StandardError.ReadToEndAsync()
    $StartupDeadline,$Ready = [DateTimeOffset]::UtcNow.AddSeconds(20), $false
    $Handler = [Net.Http.HttpClientHandler]::new()
    $Handler.AllowAutoRedirect = $false
    $Client = [Net.Http.HttpClient]::new($Handler)
    $Client.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        while (-not $Ready -and [DateTimeOffset]::UtcNow -lt $StartupDeadline) {
            if ($Server.HasExited) { throw "dotnet serve exited before becoming ready (code $($Server.ExitCode))." }
            try {
                $Marker = $Client.GetStringAsync("http://127.0.0.1:$Port/smoke-run.json").GetAwaiter().GetResult() |
                    ConvertFrom-Json -AsHashtable
                $Ready = $Marker.runId -ceq $RunId
            } catch { Start-Sleep -Milliseconds 100 }
        }
    } finally { $Client.Dispose() }
    if (-not $Ready) { throw 'dotnet serve did not become ready within 20 seconds.' }
    $Run.state,$Run.deadline = 'waiting', [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds).ToString('o')
    Write-RunJson $RunPath $Run
    Write-Host "Run ID: $RunId"
    Write-Host "integrated_browser URL: $Url"
    Write-Host 'Open a dedicated MCP tab, then evaluate: await globalThis.runPSWasmBrowserSmoke()'
    Write-Host "Submit its JSON in another shell with: Invoke-BrowserDomSmoke.ps1 -ResultJson '<JSON>'"
    Write-Host "Or save it to $RunRoot/submission.json and use -ResultPath with that path."
    Write-Host "Waiting up to $TimeoutSeconds seconds; an unavailable MCP is a blocker, never a browser fallback."
    while (-not (Test-Path -LiteralPath $ReportPath)) {
        if ($Server.HasExited) { throw "dotnet serve exited during testing (code $($Server.ExitCode))." }
        if ([DateTimeOffset]::UtcNow -gt [DateTimeOffset]$Run.deadline) {
            throw 'Timed out waiting for the integrated_browser report. No browser test pass was recorded.'
        }
        Start-Sleep -Milliseconds 200
    }
    $null = Assert-WorkspacePath $ReportPath $RunRoot
    $Report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -AsHashtable
    Assert-Report $Report $Run
    if ($Report.status -eq 'passed') { $ExitCode = 0 }
} catch {
    $Report = [ordered]@{
        schemaVersion=1; runId=$RunId; status='failed'; startedAt=$Run.startedAt
        finishedAt=[DateTimeOffset]::UtcNow.ToString('o'); checks=@(); errors=@($_.Exception.Message)
    }
    Write-RunJson $ReportPath $Report
    Write-Warning $_.Exception.Message
} finally {
    try {
        if ($Stdout -and -not $Server.HasExited) { $Server.Kill($true) }
        if ($Stdout -and -not $Server.WaitForExit(5000)) { throw 'The owned test server did not stop.' }
        foreach ($Log in @(@('server.out.log',$Stdout), @('server.err.log',$Stderr))) {
            if ($Log[1] -and $Log[1].Wait(5000)) {
                $LogPath = Assert-WorkspacePath (Join-Path $RunRoot $Log[0]) $RunRoot
                [IO.File]::WriteAllText($LogPath, $Log[1].Result)
            }
        }
    } catch {
        $ExitCode = 1
        $Message = "Test server cleanup failed: $($_.Exception.Message)"
        Write-Warning $Message
        $Report.status = 'failed'
        $Report.errors = @($Report.errors) + $Message
        Write-RunJson $ReportPath $Report
    }
    $Server.Dispose()
    $Run.state = if ($ExitCode -eq 0) { 'passed' } else { 'failed' }
    $Run.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    Write-RunJson $RunPath $Run
}
Write-Host "$($Run.state.ToUpperInvariant()) integrated_browser smoke: $ReportPath"
exit $ExitCode
