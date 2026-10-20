[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\ffmpeg"
$zipName = "ffmpeg-n9.0.2-28-g669f5ab083-win64-lgpl-shared-9.0.zip"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-19-13-06/$zipName"
$sha256 = "557513674c1628d82cad40515d75bc28d26c4730582b4029b018c4a1ff77a49c"
$productMark = "n9.0.2-28-g669f5ab083"
$dlls = @(
    "avcodec-63.dll",
    "avdevice-63.dll",
    "avfilter-12.dll",
    "avformat-63.dll",
    "avutil-61.dll",
    "swresample-7.dll",
    "swscale-10.dll"
)

function Test-FfmpegPresent {
    $avcodec = Join-Path $dest "avcodec-63.dll"
    if (-not (Test-Path $avcodec)) {
        return $false
    }
    foreach ($name in $dlls) {
        if (-not (Test-Path (Join-Path $dest $name))) {
            return $false
        }
    }
    $product = [string](Get-Item $avcodec).VersionInfo.ProductVersion
    return $product.Contains($productMark)
}

if (-not $Force -and (Test-FfmpegPresent)) {
    Write-Host "FFmpeg shared libraries already present ($productMark)."
    return
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$zipPath = Join-Path $root "third_party\$zipName"

if (Test-Path $zipPath) {
    $existing = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    if ($existing -ne $sha256) {
        Remove-Item -Force $zipPath
    }
}

if (-not (Test-Path $zipPath)) {
    Write-Host "Downloading FFmpeg $productMark ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zipPath
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
if ($hash -ne $sha256) {
    throw "FFmpeg archive hash mismatch. Expected $sha256, got $hash."
}

$extract = Join-Path $root "third_party\ffmpeg-extract"
if (Test-Path $extract) {
    Remove-Item -Recurse -Force $extract
}
Expand-Archive -Path $zipPath -DestinationPath $extract -Force

$avcodec = Get-ChildItem -Path $extract -Recurse -Filter "avcodec-63.dll" | Select-Object -First 1
if (-not $avcodec) {
    throw "Downloaded archive does not contain avcodec-63.dll."
}

$binDir = $avcodec.DirectoryName
foreach ($name in $dlls) {
    $source = Join-Path $binDir $name
    if (-not (Test-Path $source)) {
        throw "Downloaded archive is missing $name."
    }
    Copy-Item -Force $source (Join-Path $dest $name)
}

$license = Get-ChildItem -Path $extract -Recurse -Filter "LICENSE.txt" | Select-Object -First 1
if ($license) {
    Copy-Item -Force $license.FullName (Join-Path $dest "LICENSE.txt")
}

Remove-Item -Recurse -Force $extract
Write-Host "Restored FFmpeg shared libraries to third_party\ffmpeg."
