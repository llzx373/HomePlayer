[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Proxy = "http://127.0.0.1:7890"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$destinationRoot = Join-Path $root "third_party\ai"
$downloadRoot = Join-Path $root "third_party"
$extractRoot = Join-Path $root "third_party\ai-extract"
$packages = @(
    @{
        Id = "whisper"
        LicenseId = "whisper"
        Version = "v1.8.3"
        Archive = "whisper-bin-x64.zip"
        Size = 3968674
        Url = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.8.3/whisper-bin-x64.zip"
        Sha256 = "d824b1e37599f882b396e73f1ee0bfd5d0529f700314c48311dcbd00b803321d"
        Executable = "whisper-cli.exe"
    },
    @{
        Id = "whisper-cuda"
        LicenseId = "whisper"
        Version = "v1.8.3-cuda12.4"
        Archive = "whisper-cublas-12.4.0-bin-x64.zip"
        Size = 459854042
        Url = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.8.3/whisper-cublas-12.4.0-bin-x64.zip"
        Sha256 = "c12a563333d3c3707be70754dc0e87c1cb58aa6333a87055bbcf9b524488dfb0"
        Executable = "whisper-cli.exe"
    }
)

function Invoke-Download($url, $path) {
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        & curl.exe --proxy $Proxy --fail --location --silent --show-error --continue-at - --connect-timeout 20 --max-time 300 --output $path $url
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
    $destination = Join-Path $destinationRoot $package.Id
    $marker = Join-Path $destination "version.txt"
    if (-not $Force -and (Test-Path (Join-Path $destination $package.Executable) -PathType Leaf) -and
        (Test-Path $marker -PathType Leaf) -and
        (Get-Content -Raw -LiteralPath $marker).Trim() -eq $package.Version) {
        Write-Host "$($package.Id) $($package.Version) already present."
        continue
    }

    $archive = Join-Path $downloadRoot $package.Archive
    if ((Test-Path $archive -PathType Leaf) -and
        (Get-Item -LiteralPath $archive).Length -eq $package.Size -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant() -ne $package.Sha256) {
        Remove-Item -Force -LiteralPath $archive
    }
    if (-not (Test-Path $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -ne $package.Size) {
        Write-Host "Downloading $($package.Id) $($package.Version) ..."
        Invoke-Download $package.Url $archive
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actual -ne $package.Sha256) {
        throw "$($package.Id) archive hash mismatch. Expected $($package.Sha256), got $actual."
    }

    $extract = Join-Path $extractRoot $package.Id
    if (Test-Path $extract) {
        Remove-Item -Recurse -Force -LiteralPath $extract
    }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -Path $archive -DestinationPath $extract -Force
    $executable = Get-ChildItem -Path $extract -Recurse -Filter $package.Executable | Select-Object -First 1
    if (-not $executable) {
        throw "$($package.Id) archive does not contain $($package.Executable)."
    }
    $runtime = $executable.DirectoryName
    if (Test-Path $destination) {
        Remove-Item -Recurse -Force -LiteralPath $destination
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -Force -LiteralPath $executable.FullName -Destination $destination
    Get-ChildItem -Path $runtime -Filter "*.dll" | ForEach-Object {
        Copy-Item -Force -LiteralPath $_.FullName -Destination $destination
    }
    $licenseSource = Join-Path $root "third_party\ai-licenses\$($package.LicenseId)-LICENSE.txt"
    Copy-Item -Force -LiteralPath $licenseSource -Destination (Join-Path $destination "LICENSE.txt")
    Set-Content -LiteralPath $marker -Value $package.Version -Encoding utf8NoBOM
    Remove-Item -Recurse -Force -LiteralPath $extract
    Write-Host "Restored $($package.Id) to third_party\ai\$($package.Id)."
}

$aiHashes = [ordered]@{}
Get-ChildItem -Path $destinationRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll") } | ForEach-Object {
    $relative = "ai/" + $_.FullName.Substring($destinationRoot.Length + 1).Replace("\", "/")
    $aiHashes[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$aiHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root "third_party\ai-files.json") -Encoding utf8NoBOM
Write-Host "Generated third_party\ai-files.json with $($aiHashes.Count) runtime files."
