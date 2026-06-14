[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$Port = 5010,
    [int]$DebugPort = 9222,
    [string]$BrowserPath,
    [switch]$Manual,
    [switch]$SkipPublish
)

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$PublishRoot = Join-Path $Root 'artifacts\BrowserHost'
$WwwRoot = Join-Path $PublishRoot 'wwwroot'

if (-not $SkipPublish) {
    dotnet publish (Join-Path $Root 'samples\BrowserHost\PSWasm.BrowserHost.csproj') `
        -c $Configuration -r browser-wasm -o $PublishRoot /p:UseAppHost=false --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$NodeArgs = @(
    (Join-Path $PSScriptRoot 'browser-dom-smoke.mjs'),
    '--root', $WwwRoot,
    '--fixture', (Join-Path $PSScriptRoot 'dom-smoke.html'),
    '--port', $Port,
    '--debug-port', $DebugPort
)

if (-not [string]::IsNullOrWhiteSpace($BrowserPath)) {
    $NodeArgs += @('--browser', $BrowserPath)
}

if ($Manual) {
    $NodeArgs += '--manual'
}

node @NodeArgs
exit $LASTEXITCODE
