param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [switch]$SummaryOnly
)

$Root = if (Test-Path -LiteralPath (Join-Path $Path 'wwwroot')) { Join-Path $Path 'wwwroot' } else { $Path }
$Root = (Resolve-Path -LiteralPath $Root).Path

function Format-ByteSize {
    param([long]$Bytes)

    if ($Bytes -ge 1GB) { return ('{0:n2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:n2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:n2} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

$Rows = foreach ($File in Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object Extension -notin '.br','.gz') {
    $Br = Get-Item -LiteralPath ($File.FullName + '.br') -ErrorAction SilentlyContinue
    $Gz = Get-Item -LiteralPath ($File.FullName + '.gz') -ErrorAction SilentlyContinue
    $EncodedFile = if ($Br) { $Br } elseif ($Gz) { $Gz } else { $File }
    $Encoding = if ($Br) { 'br' } elseif ($Gz) { 'gzip' } else { 'identity' }

    [pscustomobject]@{
        Path = [IO.Path]::GetRelativePath($Root, $File.FullName).Replace('\', '/')
        RawBytes = $File.Length
        TransferBytes = $EncodedFile.Length
        Encoding = $Encoding
        RawSize = Format-ByteSize $File.Length
        TransferSize = Format-ByteSize $EncodedFile.Length
    }
}

$Totals = [pscustomobject]@{
    Files = @($Rows).Count
    RawBytes = ($Rows | Measure-Object RawBytes -Sum).Sum
    TransferBytes = ($Rows | Measure-Object TransferBytes -Sum).Sum
}

if (-not $SummaryOnly) {
    $Rows | Sort-Object TransferBytes -Descending |
        Select-Object Path,Encoding,RawSize,TransferSize,RawBytes,TransferBytes |
        Format-Table -AutoSize
}

[pscustomobject]@{
    Files = $Totals.Files
    RawSize = Format-ByteSize $Totals.RawBytes
    TransferSize = Format-ByteSize $Totals.TransferBytes
    RawBytes = $Totals.RawBytes
    TransferBytes = $Totals.TransferBytes
}
