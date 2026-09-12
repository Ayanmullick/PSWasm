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

function Assert-WorkspacePath([string]$Path, [string]$Parent) {
    $FullPath = [IO.Path]::GetFullPath($Path)
    if (-not $FullPath.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be a dedicated child of ${Parent}: $FullPath"
    }
    $Ancestor = $FullPath
    while ($Ancestor -and $Ancestor.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $Ancestor) -and
            ((Get-Item -LiteralPath $Ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Test paths must not traverse a symbolic link or junction: $Ancestor"
        }
        $Ancestor = [IO.Path]::GetDirectoryName($Ancestor)
    }
    return $FullPath
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
    $Id = [guid]::Empty
    if (-not [guid]::TryParseExact($Report.runId, 'D', [ref]$Id)) { throw 'Report runId must be a GUID.' }
    $RunRoot = Assert-WorkspacePath (Join-Path $ResultsRoot $Id.ToString('D')) $ResultsRoot
    $Run = Get-Content -LiteralPath (Join-Path $RunRoot 'run.json') -Raw | ConvertFrom-Json -AsHashtable
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
if (Test-Path -LiteralPath $PublishRoot) {
    foreach ($Item in Get-ChildItem -LiteralPath $PublishRoot -Recurse -Force) {
        if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Publish output must not traverse a symbolic link or junction: $($Item.FullName)"
        }
    }
}
if (-not $SkipPublish) {
    & $WorkspaceCommand dotnet publish (Join-Path $Root 'samples/BrowserHost/PSWasm.BrowserHost.csproj') `
        -c $Configuration -r browser-wasm -o $PublishRoot /p:UseAppHost=false --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishedSite 'app.js') -PathType Leaf)) {
    throw 'Publish BrowserHost before using -SkipPublish.'
}
$ExpectedChecks = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'checks.json') -Raw | ConvertFrom-Json)
if (-not $ExpectedChecks.Count) { throw 'The browser assertion manifest is empty.' }

# Check the requested port without taking over a server that belongs to someone else.
$Probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $Probe.Start() } catch { throw "Port $Port is unavailable. Choose a free port with -Port." } finally { $Probe.Stop() }

$RunId = [guid]::NewGuid().ToString('D')
$RunRoot = Assert-WorkspacePath (Join-Path $ResultsRoot $RunId) $ResultsRoot
$SiteRoot = Join-Path $RunRoot 'site'
New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
foreach ($Item in Get-ChildItem -LiteralPath $PublishedSite -Recurse -Force) {
    if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Published test assets contain a link: $($Item.FullName)" }
}
Get-ChildItem -LiteralPath $PublishedSite -Force | Copy-Item -Destination $SiteRoot -Recurse
foreach ($Name in @('dom-smoke.html','browser-dom-smoke.mjs','checks.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $Name) -Destination (Join-Path $SiteRoot $Name)
}

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
            if ($Log[1] -and $Log[1].Wait(5000)) { [IO.File]::WriteAllText((Join-Path $RunRoot $Log[0]), $Log[1].Result) }
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
