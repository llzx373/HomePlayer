[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string[]]$KeepCulture = @('en-us', 'zh-CN', 'zh-Hans', 'zh-Hant', 'zh-TW')
)

$ErrorActionPreference = "Stop"

$resolved = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $resolved)) {
    throw "Directory not found: $resolved"
}

$removed = 0
foreach ($dir in Get-ChildItem -LiteralPath $resolved -Directory) {
    if ($KeepCulture -contains $dir.Name) {
        continue
    }

    $files = @(Get-ChildItem -LiteralPath $dir.FullName -File -Recurse)
    if ($files.Count -eq 0) {
        continue
    }

    # Only touch folders made purely of .NET satellite assemblies or WinUI MUI resources.
    $localizationOnly = @($files | Where-Object {
        -not ($_.Extension -eq ".mui" -or ($_.Extension -eq ".dll" -and $_.Name -like "*.resources.dll"))
    }).Count -eq 0

    if (-not $localizationOnly) {
        continue
    }

    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
    $removed++
}

Write-Host "Trimmed $removed unused localization directories from $resolved"
