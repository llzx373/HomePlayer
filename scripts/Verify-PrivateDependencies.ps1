[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$expected = @{
    "ffmpeg/avcodec-63.dll" = "8f48ec7587a6905699d441b66f42c838a0e29b00347742330cbc12745b35d020"
    "ffmpeg/avdevice-63.dll" = "e4ad2fe72df12b29029dc6e13d3174d1c0d291842c2bf20b2dadddf4861ce94a"
    "ffmpeg/avfilter-12.dll" = "ff8c43ea64c2e2e07a3db53eaeb8b47ea1b51e8aca3a5370ea9ec6ddfc4953cf"
    "ffmpeg/avformat-63.dll" = "4b26a56f85ca7b0387cff5b789d43f20d3e51a733c04809da68709437353fb8c"
    "ffmpeg/avutil-61.dll" = "9fd7cde4ca3d1d817311c35447b4fe2d1551f924966b5b58645bcbc161ac003b"
    "ffmpeg/swresample-7.dll" = "e385fff12f82d405e3696ca6b20a452b81a7fc7bccbe0114575192ced6ce5382"
    "ffmpeg/swscale-10.dll" = "779e062ae3771ba5a4b6822f77c8659c3f7d89233b7997f42f71d467e4b62789"
    "ffmpeg/ffmpeg.exe" = "27219c276fee25da1d3156011289689f0b1559584be73f5ea28947d5cf80f946"
    "libplacebo/libplacebo-360.dll" = "46b31fc5429f0b348d7c7183a805ba68d80170843eee2f35e8ac5962d6992e66"
    "libplacebo/libdovi.dll" = "d0ebc0d98ac67dc28b176f77af125f2066dcb6cf6283f71c102d67f0eeb1df7c"
    "libplacebo/libgcc_s_seh-1.dll" = "661f70dc19fde8c25bb56387b807b7a9ab7e532cf58fa8aeee078256c19d8980"
    "libplacebo/liblcms2-2.dll" = "dc241cbdf2d295846ec564c1eb3c01639d43c0d72415a8758e65d71df31cc0ba"
    "libplacebo/liblcms2_fast_float-2.dll" = "7a5412aa4333c0ccfc440e61de38da85a03bfaa6039383a0700b78e501cf93a4"
    "libplacebo/libshaderc_shared.dll" = "803dd276af99afa40e96836484fea513852fba773fc45690c5eef5a5ebc3ae94"
    "libplacebo/libspirv-cross-c-shared.dll" = "b769dd849d7dcbf7668ddbb55f66acbd0e26152da1112d796fbd0635c3aacd29"
    "libplacebo/libstdc++-6.dll" = "b720a96289bcd6d9ec6f5f1f5612f1b1a523ce15d3ada20fe119791274cb512f"
    "libplacebo/libwinpthread-1.dll" = "69d7bb54bfa7502501b8cda3e09ae9d0a7ae3325d7419aeaeb55c13f4ae8dddb"
    "libplacebo/vulkan-1.dll" = "1d848aabe2673d89ccbced220cbc0b98793b49aa38e3b2d7b5a71e28f4875016"
}

$aiManifestPath = Join-Path $root "third_party/ai-files.json"
if (-not (Test-Path $aiManifestPath -PathType Leaf)) {
    throw "Missing AI runtime manifest: third_party/ai-files.json"
}
$aiManifest = Get-Content -Raw -LiteralPath $aiManifestPath | ConvertFrom-Json
foreach ($entry in $aiManifest.PSObject.Properties) {
    $expected[$entry.Name] = ([string]$entry.Value).ToLowerInvariant()
}

$files = @()
foreach ($relative in $expected.Keys | Sort-Object) {
    $path = Join-Path $root ("third_party/" + $relative)
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing private dependency: $relative"
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    if ($actual -ne $expected[$relative]) {
        throw "Private dependency hash mismatch: $relative. Expected $($expected[$relative]), got $actual."
    }
    $id = ($relative -replace "[^A-Za-z0-9.-]", "-")
    $files += [ordered]@{
        fileName = "third_party/$relative"
        SPDXID = "SPDXRef-File-$id"
        checksums = @(@{ algorithm = "SHA256"; checksumValue = $actual })
    }
}
foreach ($license in @("ffmpeg/LICENSE.txt", "libplacebo/licenses/libplacebo/LICENSE", "ai/whisper/LICENSE.txt", "ai/whisper-cuda/LICENSE.txt", "ai/cuda-runtime/NOTICE.txt", "ai/sherpa-cuda/LICENSE.txt", "ai/cuda12.8-cudart/NOTICE.txt", "ai/cosyvoice/LICENSE.txt", "ai/cuda13.1-cudart/NOTICE.txt", "ai/cuda13.1-cublas/NOTICE.txt")) {
    if (-not (Test-Path (Join-Path $root "third_party/$license") -PathType Leaf)) {
        throw "Missing license: $license"
    }
}

$version = [xml](Get-Content -LiteralPath (Join-Path $root "Directory.Build.props"))
$appVersion = @($version.Project.PropertyGroup | ForEach-Object { $_.Version }) | Where-Object { $_ } | Select-Object -First 1
$packages = @([ordered]@{
    name = "HomePlayer"
    SPDXID = "SPDXRef-Package-HomePlayer"
    versionInfo = $appVersion
    downloadLocation = "NOASSERTION"
    filesAnalyzed = $false
    licenseConcluded = "MIT"
    licenseDeclared = "MIT"
    supplier = "Person: llzx373"
})
$relationships = @()
function Add-Package($name, $version, $license, $download, $packageFiles) {
    $id = ($name -replace "[^A-Za-z0-9.-]", "-")
    $script:packages += [ordered]@{
        name = $name
        SPDXID = "SPDXRef-Package-$id"
        versionInfo = $version
        downloadLocation = $download
        filesAnalyzed = ($packageFiles.Count -gt 0)
        licenseConcluded = $license
        licenseDeclared = $license
        supplier = "NOASSERTION"
        files = $packageFiles
    }
    $script:relationships += [ordered]@{
        spdxElementId = "SPDXRef-Package-HomePlayer"
        relatedSpdxElement = "SPDXRef-Package-$id"
        relationshipType = "DEPENDS_ON"
    }
}
Add-Package "FFmpeg" "n9.0.2-45-g1e65c6cd5d" "LGPL-3.0-or-later" "https://github.com/BtbN/FFmpeg-Builds" @($files | Where-Object { $_.fileName.StartsWith("third_party/ffmpeg/") })
Add-Package "libplacebo" "7.360.1" "LGPL-2.1-or-later" "https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-libplacebo-7.360.1-1-any.pkg.tar.zst" @($files | Where-Object { $_.fileName.StartsWith("third_party/libplacebo/") })
Add-Package "whisper.cpp" "v1.8.3" "MIT" "https://github.com/ggml-org/whisper.cpp/releases/tag/v1.8.3" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/whisper/") })
Add-Package "NVIDIA CUDA Runtime" "12.4" "LicenseRef-NVIDIA-CUDA-EULA" "https://github.com/ggml-org/llama.cpp/releases/tag/b11960" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/cuda-runtime/") })
Add-Package "sherpa-onnx" "2.0.1" "Apache-2.0" "https://github.com/k2-fsa/sherpa-onnx/releases/tag/v2.0.1" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/sherpa-cuda/") })
Add-Package "NVIDIA CUDA and cuDNN" "12.8 / 9.8" "LicenseRef-NVIDIA-CUDA-EULA" "https://developer.download.nvidia.com/" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/cuda12.8-") })
Add-Package "CosyVoice.cpp" "93a01fa" "MIT" "https://github.com/Lourdle/cosyvoice.cpp" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/cosyvoice/") })
Add-Package "NVIDIA CUDA Runtime" "13.1" "LicenseRef-NVIDIA-CUDA-EULA" "https://developer.download.nvidia.com/" @($files | Where-Object { $_.fileName.StartsWith("third_party/ai/cuda13.1-") })

$libraryVersions = @{}
foreach ($assets in Get-ChildItem -Path (Join-Path $root "src") -Recurse -Filter "project.assets.json") {
    $json = Get-Content -Raw -LiteralPath $assets.FullName | ConvertFrom-Json
    foreach ($entry in $json.libraries.PSObject.Properties) {
        if ($entry.Value.type -eq "package") {
            $parts = $entry.Name.Split('/')
            $libraryVersions[$parts[0]] = $parts[1]
        }
    }
}
foreach ($name in $libraryVersions.Keys | Sort-Object) {
    Add-Package $name $libraryVersions[$name] "NOASSERTION" "https://www.nuget.org/packages/$name/$($libraryVersions[$name])" @()
}

$sbom = [ordered]@{
    spdxVersion = "SPDX-2.3"
    dataLicense = "CC0-1.0"
    SPDXID = "SPDXRef-DOCUMENT"
    name = "HomePlayer-$appVersion"
    documentNamespace = "https://github.com/llzx373/HomePlayer/sbom/$appVersion"
    creationInfo = @{ created = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"); creators = @("Tool: HomePlayer Verify-PrivateDependencies") }
    packages = $packages
    files = $files
    relationships = $relationships
}
$destination = Join-Path $root "third_party/sbom.json"
$sbom | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8NoBOM
Write-Host "Verified $($expected.Count) private DLLs and generated third_party/sbom.json with $($packages.Count) packages."
