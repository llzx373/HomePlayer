[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$PreviousVersion = "0.0.1-smoke"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $PSScriptRoot "build.ps1"
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "安装升级烟雾测试需要管理员权限。"
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    & $build
    $propsPath = Join-Path $root "Directory.Build.props"
    [xml]$props = Get-Content $propsPath
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version }) | Where-Object { $_ } | Select-Object -First 1
    $InstallerPath = Join-Path $root "artifacts\HomePlayer-Setup-$version.exe"
}
$InstallerPath = (Resolve-Path $InstallerPath).Path
$currentVersion = [IO.Path]::GetFileNameWithoutExtension($InstallerPath) -replace '^HomePlayer-Setup-', ''
& $build -Version $PreviousVersion
$previousInstaller = Join-Path $root "artifacts\HomePlayer-Setup-$PreviousVersion.exe"
$work = Join-Path $root "artifacts\installer-smoke"
$install = Join-Path $work "app"
$data = Join-Path $work "data"
$exe = Join-Path $install "HomePlayer.exe"
$uninstaller = Join-Path $install "unins000.exe"
$started = @()

function Invoke-Checked([string]$file, [string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$file 退出码为 $($process.ExitCode)。"
    }
}

function Stop-StartedApps {
    foreach ($process in $script:started) {
        if ($process.HasExited) {
            continue
        }
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(3000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
}

function Start-InstalledApp {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $script:exe
    $info.WorkingDirectory = $script:install
    $info.UseShellExecute = $false
    $info.Environment["HOMEPLAYER_DATA_DIR"] = $script:data
    $process = [Diagnostics.Process]::Start($info)
    $script:started += $process
    Start-Sleep -Seconds 8
    if ($process.HasExited) {
        throw "HomePlayer 启动后提前退出，退出码为 $($process.ExitCode)。"
    }
}

function Assert-Installation([string]$expectedVersion) {
    if (-not (Test-Path $script:exe)) {
        throw "安装后找不到 HomePlayer.exe。"
    }
    $productVersion = (Get-Item $script:exe).VersionInfo.ProductVersion
    if ($productVersion -ne $expectedVersion) {
        throw "版本应为 $expectedVersion，实际为 $productVersion。"
    }
    $commandKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\HomePlayer.mkv\shell\open\command"
    $command = (Get-ItemProperty $commandKey).'(default)'
    if (-not $command.Contains($script:exe)) {
        throw "MKV 打开命令未指向临时安装目录：$command"
    }
    $association = Get-ItemProperty "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.mkv\OpenWithProgids" -Name "HomePlayer.mkv" -ErrorAction SilentlyContinue
    if ($null -eq $association) {
        throw "MKV 文件关联未登记。"
    }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $work | Out-Null
    $silent = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=$install")
    Write-Host "Installing previous version $PreviousVersion ..."
    Invoke-Checked $previousInstaller $silent
    Assert-Installation $PreviousVersion
    Start-InstalledApp
    Stop-StartedApps

    Write-Host "Upgrading to $currentVersion ..."
    Invoke-Checked $InstallerPath $silent
    Assert-Installation $currentVersion
    Start-InstalledApp
    Stop-StartedApps

    Write-Host "Uninstalling ..."
    Invoke-Checked $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
    if (Test-Path $exe) {
        throw "卸载后仍保留 HomePlayer.exe。"
    }
    if (Test-Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\HomePlayer.mkv") {
        throw "卸载后仍保留 HomePlayer.mkv 文件类型。"
    }
    Write-Host "Installer install/start/upgrade/association/uninstall smoke test passed."
}
finally {
    Stop-StartedApps
    if (Test-Path $uninstaller) {
        & $uninstaller /VERYSILENT /SUPPRESSMSGBOXES /NORESTART | Out-Null
    }
    Remove-Item $previousInstaller -Force -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
