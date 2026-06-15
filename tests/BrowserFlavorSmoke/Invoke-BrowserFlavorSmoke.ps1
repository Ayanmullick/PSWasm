param(
    [ValidateSet('core','dom','crypto','web','dom-crypto','dom-web','web-crypto','dom-web-crypto','full')]
    [string[]]$Flavor = @('core','dom-web-crypto'),

    [string]$OutputRoot = '.\artifacts\BrowserFlavorSmoke',

    [switch]$NoRestore
)

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputRoot))
}

if (-not $OutputRoot.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay inside the repository: $OutputRoot"
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Get-FrameworkFiles {
    param([string]$FlavorName)

    $Root = Join-Path $OutputRoot "$FlavorName\wwwroot"
    $Framework = Join-Path $Root '_framework'

    Assert-Condition (Test-Path -LiteralPath $Root -PathType Container) "Missing flavor wwwroot: $Root"
    Assert-Condition (Test-Path -LiteralPath (Join-Path $Root 'app.js') -PathType Leaf) "Missing app.js for $FlavorName"
    Assert-Condition (Test-Path -LiteralPath (Join-Path $Root 'app.d.ts') -PathType Leaf) "Missing app.d.ts for $FlavorName"
    Assert-Condition (Test-Path -LiteralPath $Framework -PathType Container) "Missing _framework for $FlavorName"
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $Root 'index.html') -PathType Leaf)) "Flavor $FlavorName should not include BrowserHost index.html by default."
    Assert-Condition (-not (Get-ChildItem -LiteralPath $Root -Filter '*.ps1' -File -ErrorAction SilentlyContinue)) "Flavor $FlavorName should not include sample .ps1 files by default."

    $Files = Get-ChildItem -LiteralPath $Framework -File
    Assert-Condition ($Files.Name -contains 'dotnet.js') "Missing dotnet.js for $FlavorName"
    Assert-Condition ([bool]($Files | Where-Object Name -Like 'PowerShell.Wasm*.wasm')) "Missing PowerShell.Wasm wasm asset for $FlavorName"
    Assert-Condition ([bool]($Files | Where-Object Name -Like 'PSWasm.BrowserHost*.wasm')) "Missing PSWasm.BrowserHost wasm asset for $FlavorName"
    return $Files
}

$FlavorVerifyArgs = @('run','--project',(Join-Path $RepoRoot 'tests\PowerShell.Wasm.FlavorVerify\PowerShell.Wasm.FlavorVerify.csproj'),
    '--configuration','Release')
if ($NoRestore) {
    $FlavorVerifyArgs += '--no-restore'
}

Write-Host 'Verifying core flavor command exclusions.'
& dotnet @FlavorVerifyArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$PublishParams = @{ Flavor = $Flavor; OutputRoot = $OutputRoot }
if ($NoRestore) {
    $PublishParams.NoRestore = $true
}

& (Join-Path $RepoRoot 'tools\Publish-BrowserFlavors.ps1') @PublishParams
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

foreach ($Name in $Flavor) {
    $Files = Get-FrameworkFiles $Name
    $Summary = & (Join-Path $RepoRoot 'tools\Measure-BrowserPayload.ps1') -Path (Join-Path $OutputRoot "$Name\wwwroot") -SummaryOnly
    Assert-Condition ($Summary.TransferBytes -gt 0) "Flavor $Name did not report positive transfer bytes."
    Write-Host ("PASS {0} package shape: {1} raw, {2} transfer" -f $Name, $Summary.RawSize, $Summary.TransferSize)

    if ($Name -eq 'core') {
        Assert-Condition (-not ($Files | Where-Object Name -Like 'System.Net.Http*.wasm')) 'Core flavor should not include System.Net.Http wasm.'
        Assert-Condition (-not ($Files | Where-Object Name -Like 'System.Net.Primitives*.wasm')) 'Core flavor should not include System.Net.Primitives wasm.'
        Assert-Condition (-not ($Files | Where-Object Name -Like 'System.Private.Uri*.wasm')) 'Core flavor should not include System.Private.Uri wasm.'
        Write-Host 'PASS core package excludes web-request framework assets.'
    }

    if ($Name -eq 'dom-web-crypto') {
        Assert-Condition ([bool]($Files | Where-Object Name -Like 'System.Net.Http*.wasm')) 'dom-web-crypto should include System.Net.Http wasm.'
        Assert-Condition ([bool]($Files | Where-Object Name -Like 'System.Private.Uri*.wasm')) 'dom-web-crypto should include System.Private.Uri wasm.'
        Assert-Condition ([bool]($Files | Where-Object Name -Like 'System.Security.Cryptography*.wasm')) 'dom-web-crypto should include System.Security.Cryptography wasm.'
        Write-Host 'PASS dom-web-crypto package includes web and crypto framework assets.'
    }
}
