[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Proxy = $env:HOMEPLAYER_DOWNLOAD_PROXY
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\ai\cuda-runtime"
$zipName = "cudart-llama-bin-win-cuda-12.4-x64.zip"
$url = "https://github.com/ggml-org/llama.cpp/releases/download/b11960/$zipName"
$sha256 = "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"
$zipSize = 391443627
$required = @("cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll")
if (-not $Force -and (Test-Path (Join-Path $dest "cudart64_12.dll")) -and (Test-Path (Join-Path $dest "NOTICE.txt"))) {
    Write-Host "CUDA 12.4 runtime already present."
    return
}

$archive = Join-Path $root "third_party\$zipName"
if ((Test-Path $archive) -and (Get-Item -LiteralPath $archive).Length -eq $zipSize -and
    (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant() -ne $sha256) {
    Remove-Item -Force -LiteralPath $archive
}
if (-not (Test-Path $archive) -or (Get-Item -LiteralPath $archive).Length -ne $zipSize) {
    Write-Host "Downloading CUDA 12.4 runtime ..."
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $curlArgs = @("--fail", "--location", "--silent", "--show-error", "--continue-at", "-", "--connect-timeout", "20", "--max-time", "300", "--output", $archive, $url)
        if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
            $curlArgs = @("--proxy", $Proxy) + $curlArgs
        }
        & curl.exe @curlArgs
        if ($LASTEXITCODE -eq 0) {
            break
        }
        if ($attempt -eq 6) {
            throw "CUDA runtime download failed: $url"
        }
        Start-Sleep -Seconds (2 * $attempt)
    }
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    throw "CUDA runtime archive hash mismatch. Expected $sha256, got $actual."
}

$extract = Join-Path $root "third_party\ai-extract\cuda-runtime"
if (Test-Path $extract) {
    Remove-Item -Recurse -Force -LiteralPath $extract
}
Expand-Archive -Path $archive -DestinationPath $extract -Force
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($name in $required) {
    $file = Get-ChildItem -Path $extract -Recurse -Filter $name | Select-Object -First 1
    if (-not $file) {
        throw "CUDA runtime archive is missing $name."
    }
    Copy-Item -Force -LiteralPath $file.FullName -Destination $dest
}
Copy-Item -Force -LiteralPath (Join-Path $root "third_party\ai-licenses\CUDA-NOTICE.txt") -Destination (Join-Path $dest "NOTICE.txt")
Remove-Item -Recurse -Force -LiteralPath $extract
Write-Host "Restored CUDA runtime to third_party\ai\cuda-runtime."
