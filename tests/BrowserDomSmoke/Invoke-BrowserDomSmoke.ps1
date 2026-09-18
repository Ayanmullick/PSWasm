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
$PSNativeCommandUseErrorActionPreference = $false
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ResultsRoot = Join-Path $Root '.WorkDir/TestResults/BrowserDomSmoke'
function Assert-SmokePath([string]$Path, [string]$Parent) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A test path must not be empty.' }
    if ($IsWindows) {
        if ($Path -match '[*?<>|"\x00-\x1F]' -or $Path.StartsWith('\\') -or
            ($Path -replace '^[A-Za-z]:', '') -match ':') { throw "Expected a regular filesystem path: $Path" }
        foreach ($Part in ($Path -split '[\\/]')) {
            if ($Part -in @('','.', '..') -or $Part -match '^[A-Za-z]:$') { continue }
            if ($Part -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
                throw "Ambiguous or reserved test path component: $Part"
            }
        }
    }
    $FullPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, (Get-Location).Path))
    $Parent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent, (Get-Location).Path))
    $Comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $FullPath.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $Comparison) -or
        -not $FullPath.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, $Comparison)) {
        throw "Path must be a dedicated child of ${Parent}: $FullPath"
    }
    # Inspect each existing ancestor before a report read, write, or fixture copy can follow a link.
    $Current = $Root
    foreach ($Part in @('') + [IO.Path]::GetRelativePath($Root, $FullPath).Split([IO.Path]::DirectorySeparatorChar)) {
        if ($Part) { $Current = Join-Path $Current $Part }
        try { $Item = Get-Item -LiteralPath $Current -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] {
            if ($Current -eq $Root) { throw }
            break
        }
        if ($Item.LinkTarget) { throw "Test paths cannot traverse links: $Current" }
        if ($IsWindows -and $Current -ne $Root) {
            $Name = [IO.Path]::GetFileName($Current)
            $Canonical = Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Current)) -Force -ErrorAction Stop |
                Where-Object { $_.Name.Equals($Name, $Comparison) } | Select-Object -First 1
            if (-not $Canonical) { throw "Use the canonical test path instead of an alias: $Current" }
        }
        if ($Current -ne $FullPath -and -not $Item.PSIsContainer) { throw "Test ancestor is not a directory: $Current" }
    }
    return $FullPath
}

function Assert-SmokeTree([string]$Path) {
    $Path = Assert-SmokePath $Path $Root
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $Pending = [Collections.Generic.Queue[string]]::new()
    $Pending.Enqueue($Path)
    while ($Pending.Count) {
        $Item = Get-Item -LiteralPath $Pending.Dequeue() -Force -ErrorAction Stop
        if ($Item.LinkTarget) { throw "Test fixtures cannot contain links: $($Item.FullName)" }
        if ($Item.PSIsContainer) {
            foreach ($Child in Get-ChildItem -LiteralPath $Item.FullName -Force -ErrorAction Stop) {
                $Pending.Enqueue($Child.FullName)
            }
        }
    }
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
    $null = Assert-SmokePath $ResultsRoot $Root
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    $LockPath = Assert-SmokePath (Join-Path $ResultsRoot '.run-id.lock') $ResultsRoot
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
            $Directory = Assert-SmokePath (Join-Path $ResultsRoot $Id) $ResultsRoot
            if (Test-Path -LiteralPath $Directory) { continue }
            [IO.Directory]::CreateDirectory($Directory) | Out-Null
            return $Directory
        }
        throw "No unused browser run name remains for $BaseId. Retry in a later second."
    } finally { if ($null -ne $Lock) { $Lock.Dispose() } }
}

function Write-RunJson([string]$Path, $Value, [switch]$New) {
    $Path = Assert-SmokePath $Path $ResultsRoot
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
        $ResultPath = Assert-SmokePath $ResultPath $ResultsRoot
        $ResultJson = Get-Content -LiteralPath $ResultPath -Raw
    }
    $Report = $ResultJson | ConvertFrom-Json -AsHashtable
    if (-not (Test-SmokeRunId $Report.runId)) { throw 'Report runId must be a UTC run name or a legacy GUID.' }
    $RunRoot = Assert-SmokePath (Join-Path $ResultsRoot $Report.runId) $ResultsRoot
    $RunPath = Assert-SmokePath (Join-Path $RunRoot 'run.json') $RunRoot
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

$PublishRoot = Assert-SmokePath (Join-Path $Root '.WorkDir/build/publish/BrowserHost') $Root
$PublishedSite = Assert-SmokePath (Join-Path $PublishRoot 'wwwroot') $PublishRoot
Assert-SmokeTree $PublishRoot
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $Root 'samples/BrowserHost/PSWasm.BrowserHost.csproj') `
        -c $Configuration -r browser-wasm -o $PublishRoot /p:UseAppHost=false --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishedSite 'app.js') -PathType Leaf)) {
    throw 'Publish BrowserHost before using -SkipPublish.'
}
$ChecksPath = Assert-SmokePath (Join-Path $PSScriptRoot 'checks.json') $Root
$ExpectedChecks = @(Get-Content -LiteralPath $ChecksPath -Raw | ConvertFrom-Json)
if (-not $ExpectedChecks.Count) { throw 'The browser assertion manifest is empty.' }

# Check the requested port without taking over a server that belongs to someone else.
$Probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $Probe.Start() } catch { throw "Port $Port is unavailable. Choose a free port with -Port." } finally { $Probe.Stop() }

$RunRoot = New-SmokeRunDirectory
$RunId = [IO.Path]::GetFileName($RunRoot)
$SiteRoot = Assert-SmokePath (Join-Path $RunRoot 'site') $RunRoot
Assert-SmokeTree $PublishedSite
New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
Get-ChildItem -LiteralPath $PublishedSite -Force | Copy-Item -Destination $SiteRoot -Recurse
foreach ($Name in @('dom-smoke.html','browser-dom-smoke.mjs','checks.json')) {
    $FixturePath = Assert-SmokePath (Join-Path $PSScriptRoot $Name) $Root
    Copy-Item -LiteralPath $FixturePath -Destination (Join-Path $SiteRoot $Name)
}
Assert-SmokeTree $SiteRoot

$Url = "http://127.0.0.1:$Port/dom-smoke.html?runId=$RunId"
$Run = [ordered]@{
    schemaVersion=1; runId=$RunId; state='starting'; startedAt=[DateTimeOffset]::UtcNow.ToString('o')
    deadline=$null; url=$Url; expectedChecks=$ExpectedChecks
}
$RunPath,$ReportPath = (Join-Path $RunRoot 'run.json'), (Join-Path $RunRoot 'result.json')
Write-RunJson $RunPath $Run -New
Write-RunJson (Join-Path $SiteRoot 'smoke-run.json') @{runId=$RunId} -New
$Server,$Stdout,$Stderr = [Diagnostics.Process]::new(), $null, $null
$Server.StartInfo.FileName = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$Server.StartInfo.WorkingDirectory = $Root
$Server.StartInfo.UseShellExecute = $false
$Server.StartInfo.CreateNoWindow = $true
$Server.StartInfo.RedirectStandardOutput = $true
$Server.StartInfo.RedirectStandardError = $true
foreach ($Argument in @('serve','--directory',$SiteRoot,
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
    Write-Host "Browser test URL: $Url"
    Write-Host 'Open the URL in a browser, then evaluate in its developer console: await globalThis.runPSWasmBrowserSmoke()'
    Write-Host "Submit its JSON in another shell with: Invoke-BrowserDomSmoke.ps1 -ResultJson '<JSON>'"
    Write-Host "Or save it to $RunRoot/submission.json and use -ResultPath with that path."
    Write-Host "Waiting up to $TimeoutSeconds seconds for the browser report."
    while (-not (Test-Path -LiteralPath $ReportPath)) {
        if ($Server.HasExited) { throw "dotnet serve exited during testing (code $($Server.ExitCode))." }
        if ([DateTimeOffset]::UtcNow -gt [DateTimeOffset]$Run.deadline) {
            throw 'Timed out waiting for the browser report. No browser test pass was recorded.'
        }
        Start-Sleep -Milliseconds 200
    }
    $null = Assert-SmokePath $ReportPath $RunRoot
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
                $LogPath = Assert-SmokePath (Join-Path $RunRoot $Log[0]) $RunRoot
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
Write-Host "$($Run.state.ToUpperInvariant()) browser DOM smoke: $ReportPath"
exit $ExitCode
