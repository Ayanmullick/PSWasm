param(
    [ValidateSet('core','web','AzAuth','full')]
    [string[]]$Flavor = @('core','web','AzAuth','full'),

    [string]$OutputRoot = './.WorkDir/build/publish/BrowserFlavors',

    [string]$HostedRoot = '',

    [ValidatePattern('^$|^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$HostedVersion = '',

    [switch]$IncludeSampleHost,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$WorkRoot = Join-Path $RepoRoot '.WorkDir'
$PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function ConvertTo-PackagePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A package path must not be empty.' }
    if ($IsWindows) {
        if ($Path -match '[*?<>|"\x00-\x1F]' -or $Path.StartsWith('\\') -or
            ($Path -replace '^[A-Za-z]:', '') -match ':') { throw "Expected a regular filesystem path: $Path" }
        foreach ($Part in ($Path -split '[\\/]')) {
            if ($Part -in @('','.', '..') -or $Part -match '^[A-Za-z]:$') { continue }
            if ($Part -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
                throw "Ambiguous or reserved path component: $Part"
            }
        }
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, (Get-Location).Path))
}

function Assert-PackagePath([string]$Path, [string]$Parent) {
    $FullPath,$Parent = (ConvertTo-PackagePath $Path), (ConvertTo-PackagePath $Parent)
    if ($Parent -eq [IO.Path]::GetPathRoot($Parent) -or
        (-not $FullPath.Equals($Parent, $PathComparison) -and
        -not $FullPath.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $PathComparison))) {
        throw "Package path must stay inside its dedicated parent: $FullPath"
    }
    # Check ancestors before inspecting descendants; missing output directories may be created later.
    $Current = $Parent
    $Parts = @('') + @([IO.Path]::GetRelativePath($Parent, $FullPath).Split([IO.Path]::DirectorySeparatorChar) |
        Where-Object { $_ -ne '.' })
    foreach ($Part in $Parts) {
        if ($Part) { $Current = Join-Path $Current $Part }
        try { $Item = Get-Item -LiteralPath $Current -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] {
            if ($Current -eq $Parent) { throw }
            break
        }
        if ($Item.LinkTarget) { throw "Package operations cannot traverse links: $Current" }
        if ($IsWindows -and $Current -ne $Parent) {
            # Directory enumeration supplies the real name even when Get-Item accepted an 8.3 alias.
            $Name = [IO.Path]::GetFileName($Current)
            $Canonical = Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Current)) -Force -ErrorAction Stop |
                Where-Object { $_.Name.Equals($Name, $PathComparison) } | Select-Object -First 1
            if (-not $Canonical) { throw "Use the canonical package path instead of an alias: $Current" }
        }
        if (($Current -eq $Parent -or $Current -ne $FullPath) -and -not $Item.PSIsContainer) {
            throw "Package path ancestor is not a directory: $Current"
        }
    }
    return $FullPath
}

function Assert-PackageTree([string]$Path, [string]$Parent) {
    $Path = Assert-PackagePath $Path $Parent
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $Pending = [Collections.Generic.Queue[string]]::new()
    $Pending.Enqueue($Path)
    while ($Pending.Count) {
        $Item = Get-Item -LiteralPath $Pending.Dequeue() -Force -ErrorAction Stop
        if ($Item.LinkTarget) { throw "Package operations cannot traverse links: $($Item.FullName)" }
        if ($Item.PSIsContainer) {
            foreach ($Child in Get-ChildItem -LiteralPath $Item.FullName -Force -ErrorAction Stop) {
                $Pending.Enqueue($Child.FullName)
            }
        }
    }
}

function Resolve-InRepoPath {
    param([string]$Path, [string]$Name)

    $FullPath = ConvertTo-PackagePath $Path

    $AllowedRoots = @((Join-Path $WorkRoot 'build/publish'), (Join-Path $WorkRoot 'TestResults'))
    if (-not ($AllowedRoots | Where-Object {
        $FullPath.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, $PathComparison)
    })) {
        throw "$Name must be a dedicated folder under .WorkDir/build/publish or .WorkDir/TestResults: $FullPath"
    }

    Assert-PackagePath $FullPath $RepoRoot
}

function Copy-BrowserPackage {
    param([string]$SourceRoot, [string]$DestinationRoot)

    $SourceRoot = ConvertTo-PackagePath $SourceRoot
    $DestinationRoot = Resolve-InRepoPath $DestinationRoot 'Hosted destination'

    if (-not $DestinationRoot.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, $PathComparison)) {
        throw "Hosted destination must stay inside the repository: $DestinationRoot"
    }
    if ($SourceRoot.Equals($DestinationRoot, $PathComparison) -or
        $SourceRoot.StartsWith($DestinationRoot + [IO.Path]::DirectorySeparatorChar, $PathComparison) -or
        $DestinationRoot.StartsWith($SourceRoot + [IO.Path]::DirectorySeparatorChar, $PathComparison)) {
        throw "Hosted destination must not overlap the source package: $DestinationRoot"
    }

    Assert-PackageTree $SourceRoot $RepoRoot
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) { throw "Package source is missing: $SourceRoot" }
    Assert-PackageTree $DestinationRoot $RepoRoot
    if (Test-Path -LiteralPath $DestinationRoot) {
        Remove-Item -LiteralPath $DestinationRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceRoot -Force | Copy-Item -Destination $DestinationRoot -Recurse -Force
}

$OutputRoot = Resolve-InRepoPath $OutputRoot 'OutputRoot'
$HostedRoot = if ($HostedRoot -ne '') { Resolve-InRepoPath $HostedRoot 'HostedRoot' } else { '' }
if ($HostedRoot -ne '' -and $HostedRoot.Equals($OutputRoot, $PathComparison)) {
    throw 'HostedRoot must differ from OutputRoot.'
}
if ($HostedRoot -eq '' -and $HostedVersion -ne '') {
    throw 'HostedVersion requires HostedRoot.'
}

# Validate every destination before any flavor output can be removed.
$FlavorOutputs = @($Flavor | ForEach-Object { Resolve-InRepoPath (Join-Path $OutputRoot $_) 'Flavor output' })
$HostedDestinations = @()
if ($HostedRoot -ne '') {
    $HostedDestinations = @($Flavor | ForEach-Object {
        Resolve-InRepoPath (Join-Path $HostedRoot $_) 'Hosted destination'
        if ($HostedVersion -ne '') {
            Resolve-InRepoPath (Join-Path (Join-Path $HostedRoot $HostedVersion) $_) 'Versioned hosted destination'
        }
    })
    for ($Index = 0; $Index -lt $HostedDestinations.Count; $Index++) {
        for ($Other = $Index + 1; $Other -lt $HostedDestinations.Count; $Other++) {
            $Left,$Right = $HostedDestinations[$Index], $HostedDestinations[$Other]
            if ($Left.Equals($Right, $PathComparison) -or
                $Left.StartsWith($Right + [IO.Path]::DirectorySeparatorChar, $PathComparison) -or
                $Right.StartsWith($Left + [IO.Path]::DirectorySeparatorChar, $PathComparison)) {
                throw "Hosted destinations must not overlap each other: $Left and $Right"
            }
        }
    }
    foreach ($Destination in $HostedDestinations) {
        foreach ($Output in $FlavorOutputs) {
            if ($Destination.Equals($Output, $PathComparison) -or
                $Destination.StartsWith($Output + [IO.Path]::DirectorySeparatorChar, $PathComparison) -or
                $Output.StartsWith($Destination + [IO.Path]::DirectorySeparatorChar, $PathComparison)) {
                throw "Hosted destination must not overlap any selected flavor output: $Destination"
            }
        }
    }
}

# Preflight all affected trees before removing any selected output, including links below a destination root.
foreach ($Destination in @($FlavorOutputs) + @($HostedDestinations)) {
    Assert-PackageTree $Destination $RepoRoot
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

    $Out = Resolve-InRepoPath (Join-Path $OutputRoot $Name) 'Flavor output'
    Assert-PackageTree $Out $RepoRoot
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

    Assert-PackageTree $Out $RepoRoot
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
