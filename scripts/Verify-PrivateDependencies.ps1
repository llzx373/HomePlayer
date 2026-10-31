[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$expected = @{
    "ffmpeg/avcodec-63.dll" = "9de0b9aab1aabac017f6af55179a287cd06708dd3c29ab19a2e2b8c93464a213"
    "ffmpeg/avdevice-63.dll" = "23c934febbd246235a3188f388464caf8cb6cd3752436fc4dff14f16b39616df"
    "ffmpeg/avfilter-12.dll" = "43eb56cd0aa2a900e10e2ff2b341cfb9fe6339ef7374327562efe680fb95e0d8"
    "ffmpeg/avformat-63.dll" = "0df0277eb9800bec426fb3b3e9f386d25ce398d2d8e328f028c7e658f5818b98"
    "ffmpeg/avutil-61.dll" = "f8739cd18caadd31cf72e58aab576fe1f8c619145b6a1f2e439dcb8acf66638a"
    "ffmpeg/swresample-7.dll" = "d1d2957e5d7273ba0fd6e61ec5add76c22b40f13af9f2df0ab7a6bd2681a941c"
    "ffmpeg/swscale-10.dll" = "e8b1c508982d6a6b4fde4fbf00f72759b3e571e5a27d9856f6775075d3c7e586"
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
foreach ($license in @("ffmpeg/LICENSE.txt", "libplacebo/licenses/libplacebo/LICENSE")) {
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
Add-Package "FFmpeg" "n9.0.2-28-g669f5ab083" "LGPL-3.0-or-later" "https://github.com/BtbN/FFmpeg-Builds" @($files | Where-Object { $_.fileName.StartsWith("third_party/ffmpeg/") })
Add-Package "libplacebo" "7.360.1" "LGPL-2.1-or-later" "https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-libplacebo-7.360.1-1-any.pkg.tar.zst" @($files | Where-Object { $_.fileName.StartsWith("third_party/libplacebo/") })

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
