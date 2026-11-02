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
$dllHashes = @{
    "avcodec-63.dll" = "9de0b9aab1aabac017f6af55179a287cd06708dd3c29ab19a2e2b8c93464a213"
    "avdevice-63.dll" = "23c934febbd246235a3188f388464caf8cb6cd3752436fc4dff14f16b39616df"
    "avfilter-12.dll" = "43eb56cd0aa2a900e10e2ff2b341cfb9fe6339ef7374327562efe680fb95e0d8"
    "avformat-63.dll" = "0df0277eb9800bec426fb3b3e9f386d25ce398d2d8e328f028c7e658f5818b98"
    "avutil-61.dll" = "f8739cd18caadd31cf72e58aab576fe1f8c619145b6a1f2e439dcb8acf66638a"
    "swresample-7.dll" = "d1d2957e5d7273ba0fd6e61ec5add76c22b40f13af9f2df0ab7a6bd2681a941c"
    "swscale-10.dll" = "e8b1c508982d6a6b4fde4fbf00f72759b3e571e5a27d9856f6775075d3c7e586"
}
$tools = @("ffmpeg.exe", "ffprobe.exe")

function Test-FfmpegPresent {
    $avcodec = Join-Path $dest "avcodec-63.dll"
    if (-not (Test-Path $avcodec)) {
        return $false
    }
    foreach ($name in $dlls) {
        $path = Join-Path $dest $name
        if (-not (Test-Path $path)) {
            return $false
        }
        $actual = (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
        if ($actual -ne $dllHashes[$name]) {
            return $false
        }
    }
    foreach ($name in $tools) {
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
foreach ($name in $dlls + $tools) {
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
