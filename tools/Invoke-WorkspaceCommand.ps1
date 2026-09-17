# Run a tool with workspace-local temporary storage; do not dot-source this script.
param()

# Deliberately avoid named parameters: native switches such as -c must pass through untouched.
if ($args.Count -eq 0) { throw 'Usage: Invoke-WorkspaceCommand.ps1 <tool> [arguments...]' }
$Command,$CommandArgs = $args[0], @($args | Select-Object -Skip 1)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Command = (Get-Command -Name $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'WorkspacePathSafety.ps1')
$TempRoot = Join-Path $Root '.WorkDir/temp'
Assert-WorkspaceTreeSafe $TempRoot $Root
New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null

# Child processes inherit these values; the caller's environment is restored afterward.
$Environment = @{
    TEMP = $TempRoot; TMP = $TempRoot; TMPDIR = $TempRoot
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'; MSBUILDDISABLENODEREUSE = '1'
}
$Original = @{}
foreach ($Name in $Environment.Keys) {
    $Original[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
}

$ExitCode = 0
try {
    foreach ($Name in $Environment.Keys) {
        [Environment]::SetEnvironmentVariable($Name, $Environment[$Name], 'Process')
    }
    & $Command @CommandArgs
    $ExitCode = $LASTEXITCODE
} finally {
    foreach ($Name in $Original.Keys) {
        if ($null -eq $Original[$Name]) {
            Remove-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($Name, $Original[$Name], 'Process')
        }
    }
}
exit $ExitCode
