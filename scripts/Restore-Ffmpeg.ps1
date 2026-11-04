[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Proxy = $env:HOMEPLAYER_DOWNLOAD_PROXY
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\ffmpeg"
$zipName = "ffmpeg-n9.0.2-45-g1e65c6cd5d-win64-lgpl-shared-9.0.zip"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-11-02-12-51/$zipName"
$sha256 = "a05d78fcd027645a493a1397cd33b3195a182050912c51a69aa507a0ea465fa1"
$productMark = "n9.0.2-45-g1e65c6cd5d"
$dlls = @(
    "avcodec-63.dll",
    "avdevice-63.dll",
    "avfilter-12.dll",
    "avformat-63.dll",
    "avutil-61.dll",
    "swresample-7.dll",
    "swscale-10.dll",
    "ffmpeg.exe"
)
$dllHashes = @{
    "avcodec-63.dll" = "8f48ec7587a6905699d441b66f42c838a0e29b00347742330cbc12745b35d020"
    "avdevice-63.dll" = "e4ad2fe72df12b29029dc6e13d3174d1c0d291842c2bf20b2dadddf4861ce94a"
    "avfilter-12.dll" = "ff8c43ea64c2e2e07a3db53eaeb8b47ea1b51e8aca3a5370ea9ec6ddfc4953cf"
    "avformat-63.dll" = "4b26a56f85ca7b0387cff5b789d43f20d3e51a733c04809da68709437353fb8c"
    "avutil-61.dll" = "9fd7cde4ca3d1d817311c35447b4fe2d1551f924966b5b58645bcbc161ac003b"
    "swresample-7.dll" = "e385fff12f82d405e3696ca6b20a452b81a7fc7bccbe0114575192ced6ce5382"
    "swscale-10.dll" = "779e062ae3771ba5a4b6822f77c8659c3f7d89233b7997f42f71d467e4b62789"
    "ffmpeg.exe" = "27219c276fee25da1d3156011289689f0b1559584be73f5ea28947d5cf80f946"
}
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
    $product = [string](Get-Item $avcodec).VersionInfo.ProductVersion
    return $product.Contains($productMark)
}

if (-not $Force -and (Test-FfmpegPresent)) {
    Write-Host "FFmpeg shared libraries already present ($productMark)."
    return
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$zipPath = Join-Path $root "third_party\$zipName"
$zipSize = 76576876

if ((Test-Path $zipPath) -and (Get-Item -LiteralPath $zipPath).Length -eq $zipSize) {
    $existing = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    if ($existing -ne $sha256) {
        Remove-Item -Force $zipPath
    }
}

if (-not (Test-Path $zipPath) -or (Get-Item -LiteralPath $zipPath).Length -ne $zipSize) {
    Write-Host "Downloading FFmpeg $productMark ..."
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $curlArgs = @("--fail", "--location", "--silent", "--show-error", "--continue-at", "-", "--connect-timeout", "20", "--max-time", "300", "--output", $zipPath, $url)
        if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
            $curlArgs = @("--proxy", $Proxy) + $curlArgs
        }
        & curl.exe @curlArgs
        if ($LASTEXITCODE -eq 0) {
            break
        }
        if ($attempt -eq 6) {
            throw "FFmpeg download failed: $url"
        }
        Start-Sleep -Seconds (2 * $attempt)
    }
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
