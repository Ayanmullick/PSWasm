param(
    [ValidateSet('core','web','AzAuth','full')]
    [string[]]$Flavor = @('core','web','AzAuth','full'),

    [string]$OutputRoot = '.\artifacts\BrowserFlavors',

    [string]$HostedRoot = '',

    [ValidatePattern('^$|^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$HostedVersion = '',

    [switch]$IncludeSampleHost,

    [switch]$NoRestore
)

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-InRepoPath {
    param([string]$Path, [string]$Name)

    $FullPath = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    } else {
        [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }

    if (-not $FullPath.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must stay inside the repository: $FullPath"
    }

    $FullPath
}

function Copy-BrowserPackage {
    param([string]$SourceRoot, [string]$DestinationRoot)

    $SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
    $DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)

    if (-not $DestinationRoot.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Hosted destination must stay inside the repository: $DestinationRoot"
    }
    if ($SourceRoot.Equals($DestinationRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $SourceRoot.StartsWith($DestinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $DestinationRoot.StartsWith($SourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Hosted destination must not overlap the source package: $DestinationRoot"
    }

    if (Test-Path -LiteralPath $DestinationRoot) {
        Remove-Item -LiteralPath $DestinationRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceRoot -Force | Copy-Item -Destination $DestinationRoot -Recurse -Force
}

$OutputRoot = Resolve-InRepoPath $OutputRoot 'OutputRoot'
$HostedRoot = if ($HostedRoot -ne '') { Resolve-InRepoPath $HostedRoot 'HostedRoot' } else { '' }
if ($HostedRoot -eq '' -and $HostedVersion -ne '') {
    throw 'HostedVersion requires HostedRoot.'
}

$Project = [IO.Path]::Combine($RepoRoot, 'samples', 'BrowserHost', 'PSWasm.BrowserHost.csproj')
$Measure = [IO.Path]::Combine($PSScriptRoot, 'Measure-BrowserPayload.ps1')

foreach ($Name in $Flavor) {
    $Dom,$Crypto,$Web,$AzureAuth = switch ($Name) {
        'core'   { 'false','false','false','false'; break }
        'web'    { 'true','false','true','false'; break }
        'AzAuth' { 'true','true','true','true'; break }
        'full'   { 'true','true','true','true'; break }
    }

    $Out = Join-Path $OutputRoot $Name
    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
    }

    $Args = @('publish', $Project, '-c', 'Release', '-r', 'browser-wasm', '-o', $Out,
        '/p:UseAppHost=false', "/p:PSWasmEnableDom=$Dom", "/p:PSWasmEnableCrypto=$Crypto", "/p:PSWasmEnableWeb=$Web", "/p:PSWasmEnableAzureAuth=$AzureAuth")
    if ($NoRestore) {
        $Args += '--no-restore'
    }

    Write-Host "Publishing PSWasm browser flavor '$Name' (DOM=$Dom, Web=$Web, Crypto=$Crypto, AzureAuth=$AzureAuth)."
    & dotnet @Args
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if (-not $IncludeSampleHost) {
        $WwwRoot = Join-Path $Out 'wwwroot'
        foreach ($Pattern in @('index.html','index.html.br','index.html.gz','*.ps1','*.ps1.br','*.ps1.gz')) {
            Get-ChildItem -LiteralPath $WwwRoot -Filter $Pattern -File -ErrorAction SilentlyContinue |
                Remove-Item -Force
        }
    }

    $PackageRoot = Join-Path $Out 'wwwroot'
    if ($HostedRoot -ne '') {
        $HostedFlavorRoot = Join-Path $HostedRoot $Name
        Copy-BrowserPackage -SourceRoot $PackageRoot -DestinationRoot $HostedFlavorRoot
        Write-Host "Hosted PSWasm browser flavor '$Name' at $HostedFlavorRoot."

        if ($HostedVersion -ne '') {
            $HostedVersionRoot = Join-Path (Join-Path $HostedRoot $HostedVersion) $Name
            Copy-BrowserPackage -SourceRoot $PackageRoot -DestinationRoot $HostedVersionRoot
            Write-Host "Hosted versioned PSWasm browser flavor '$Name' at $HostedVersionRoot."
        }
    }

    & $Measure -Path $PackageRoot -SummaryOnly
}
