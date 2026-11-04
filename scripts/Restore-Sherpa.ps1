[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Proxy = $env:HOMEPLAYER_DOWNLOAD_PROXY
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$downloadRoot = Join-Path $root "third_party"
$extractRoot = Join-Path $root "third_party\ai-extract\sherpa"
$packages = @(
    @{
        Id = "sherpa-cuda"
        Archive = "sherpa-cuda-2.0.1.tar.bz2"
        Url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.1/sherpa-onnx-v2.0.1-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2"
        Size = 597231017
        Sha256 = "cd983fa95431a8a08555b15005e1effd0b771f161d30f7f3bd52d518da48f6b6"
        Kind = "TarBzip2"
    },
    @{
        Id = "cuda12.8-cudart"
        Archive = "cuda-cudart-12.8.zip"
        Url = "https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.8.90-archive.zip"
        Size = 3037735
        Sha256 = "4a39058fd8519444a81cfc7ae055d136f48d1a31ffa41ae255b35b2edd61e13b"
        Kind = "Zip"
    },
    @{
        Id = "cuda12.8-cublas"
        Archive = "cuda-cublas-12.8.zip"
        Url = "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.4.1-archive.zip"
        Size = 563660944
        Sha256 = "57a470112cec7e112c95253dde8b3c7184d795dbd92b0bde77a4cb7f8c94c8aa"
        Kind = "Zip"
    },
    @{
        Id = "cuda12.8-cudnn"
        Archive = "cudnn-9.8.zip"
        Url = "https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.8.0.87_cuda12-archive.zip"
        Size = 675349654
        Sha256 = "d8a23705e3884b137b7e05449fb2b61bfa524e7cfc3fda80743d633f423c6ce4"
        Kind = "Zip"
    }
)

function Invoke-Download($url, $path, $expectedSize) {
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $curlArgs = @("--fail", "--location", "--silent", "--show-error", "--continue-at", "-", "--connect-timeout", "20", "--max-time", "300", "--output", $path, $url)
        if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
            $curlArgs = @("--proxy", $Proxy) + $curlArgs
        }
        & curl.exe @curlArgs
        if ($LASTEXITCODE -eq 0) {
            return
        }
        if ($attempt -eq 6) {
            throw "Download failed: $url"
        }
        Start-Sleep -Seconds (2 * $attempt)
    }
}

foreach ($package in $packages) {
    $destination = Join-Path $root "third_party\ai\$($package.Id)"
    $marker = Join-Path $destination ".restored"
    if (-not $Force -and (Test-Path $marker -PathType Leaf)) {
        Write-Host "$($package.Id) already present."
        continue
    }
    $archive = Join-Path $downloadRoot $package.Archive
    if (-not (Test-Path $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -ne $package.Size) {
        Write-Host "Downloading $($package.Id) ..."
        Invoke-Download $package.Url $archive $package.Size
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actual -ne $package.Sha256) {
        throw "$($package.Id) hash mismatch. Expected $($package.Sha256), got $actual."
    }
    $extract = Join-Path $extractRoot $package.Id
    if (Test-Path $extract) {
        Remove-Item -Recurse -Force -LiteralPath $extract
    }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    if ($package.Kind -eq "Zip") {
        Expand-Archive -Path $archive -DestinationPath $extract -Force
    }
    else {
        & "C:\Windows\System32\tar.exe" -xjf $archive -C $extract
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract $($package.Id)."
        }
    }
    if (Test-Path $destination) {
        Remove-Item -Recurse -Force -LiteralPath $destination
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -Path $extract -Recurse -Filter "*.dll" | ForEach-Object {
        Copy-Item -Force -LiteralPath $_.FullName -Destination $destination
    }
    Set-Content -LiteralPath $marker -Value $package.Sha256 -Encoding utf8NoBOM
    Remove-Item -Recurse -Force -LiteralPath $extract
    Write-Host "Restored $($package.Id)."
}
Copy-Item -Force -LiteralPath (Join-Path $root "third_party\ai-licenses\sherpa-LICENSE.txt") -Destination (Join-Path $root "third_party\ai\sherpa-cuda\LICENSE.txt")
Copy-Item -Force -LiteralPath (Join-Path $root "third_party\ai-licenses\CUDA-NOTICE.txt") -Destination (Join-Path $root "third_party\ai\cuda12.8-cudart\NOTICE.txt")
