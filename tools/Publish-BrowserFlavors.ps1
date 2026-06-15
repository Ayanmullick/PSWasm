param(
    [ValidateSet('core','dom','crypto','web','dom-crypto','dom-web','web-crypto','dom-web-crypto','full')]
    [string[]]$Flavor = @('core','dom','crypto','web','dom-web-crypto','full'),

    [string]$OutputRoot = '.\artifacts\BrowserFlavors',

    [switch]$IncludeSampleHost,

    [switch]$NoRestore
)

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputRoot))
}
if (-not $OutputRoot.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay inside the repository: $OutputRoot"
}

$Project = Join-Path $RepoRoot 'samples\BrowserHost\PSWasm.BrowserHost.csproj'
$Measure = Join-Path $PSScriptRoot 'Measure-BrowserPayload.ps1'

foreach ($Name in $Flavor) {
    $Dom,$Crypto,$Web = switch ($Name) {
        'core'           { 'false','false','false'; break }
        'dom'            { 'true','false','false'; break }
        'crypto'         { 'false','true','false'; break }
        'web'            { 'false','false','true'; break }
        'dom-crypto'     { 'true','true','false'; break }
        'dom-web'        { 'true','false','true'; break }
        'web-crypto'     { 'false','true','true'; break }
        'dom-web-crypto' { 'true','true','true'; break }
        'full'           { 'true','true','true'; break }
    }

    $Out = Join-Path $OutputRoot $Name
    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
    }

    $Args = @('publish', $Project, '-c', 'Release', '-r', 'browser-wasm', '-o', $Out,
        '/p:UseAppHost=false', "/p:PSWasmEnableDom=$Dom", "/p:PSWasmEnableCrypto=$Crypto", "/p:PSWasmEnableWeb=$Web")
    if ($NoRestore) {
        $Args += '--no-restore'
    }

    Write-Host "Publishing PSWasm browser flavor '$Name' (DOM=$Dom, Web=$Web, Crypto=$Crypto)."
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

    & $Measure -Path (Join-Path $Out 'wwwroot') -SummaryOnly
}
