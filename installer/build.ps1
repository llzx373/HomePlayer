$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iss = Join-Path $PSScriptRoot "HomePlayer.iss"
$csproj = Join-Path $root "src\HomePlayer.App\HomePlayer.App.csproj"
$distExe = Join-Path $root "dist\HomePlayer.exe"
$version = "0.1.0"

Write-Host "Publishing HomePlayer to dist\ ..."
dotnet publish $csproj -c Release -p:PublishProfile=FolderProfile
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}
if (-not (Test-Path $distExe)) {
    throw "Missing $distExe after publish."
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "未找到 Inno Setup 6。请安装 https://jrsoftware.org/isdl.php 后重试。"
}

Write-Host "Building installer with $iscc ..."
& $iscc /DAppVersion=$version $iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed."
}

$setup = Join-Path $root "artifacts\HomePlayer-Setup-$version.exe"
Write-Host "Installer: $setup"
