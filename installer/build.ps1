[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iss = Join-Path $PSScriptRoot "HomePlayer.iss"
$csproj = Join-Path $root "src\HomePlayer.App\HomePlayer.App.csproj"
$distExe = Join-Path $root "dist\HomePlayer.exe"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $root "Directory.Build.props"
    [xml]$props = Get-Content -Path $propsPath
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version }) |
        Where-Object { $_ } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Directory.Build.props 里没有 <Version>。"
    }
}

Write-Host "Restoring FFmpeg shared libraries ..."
& (Join-Path $root "scripts\Restore-Ffmpeg.ps1")

Write-Host "Publishing HomePlayer $Version to dist\ ..."
dotnet publish $csproj -c Release -p:PublishProfile=FolderProfile -p:Version=$Version -p:InformationalVersion=$Version
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
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed."
}

$setup = Join-Path $root "artifacts\HomePlayer-Setup-$Version.exe"
if (-not (Test-Path $setup)) {
    throw "Missing installer $setup"
}
Write-Host "Installer: $setup"
