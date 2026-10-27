#Requires -Version 5.1
<#
.SYNOPSIS
    生成 HomePlayer 的应用图标与各视频后缀的文件图标。

.DESCRIPTION
    图标全部用 GDI+ 直接按目标尺寸绘制，边缘是真正的 alpha 抗锯齿，
    不经过「黑底压平」或最近邻放大，因此小尺寸下不会出现暗色描边。

    产物：
      src/HomePlayer.App/Assets/App.ico
      src/HomePlayer.App/Assets/FileIcons/<ext>.ico

.PARAMETER AssetsDir
    图标输出目录，默认是应用项目的 Assets 目录。

.EXAMPLE
    pwsh -File scripts/Generate-Icons.ps1
#>
[CmdletBinding()]
param(
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\src\HomePlayer.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

$appPalette = @{ Light = '#7ED3C9'; Dark = '#2F8A82' }

$filePalettes = [ordered]@{
    mkv  = @{ Light = '#7BCFC6'; Dark = '#2E8B83' }
    mp4  = @{ Light = '#82B4E2'; Dark = '#3A6CA6' }
    m4v  = @{ Light = '#8FA0DA'; Dark = '#4A58A0' }
    mov  = @{ Light = '#B49EDA'; Dark = '#6E56A0' }
    avi  = @{ Light = '#E7BD7B'; Dark = '#AF7932' }
    wmv  = @{ Light = '#9DB2C4'; Dark = '#5A7387' }
    flv  = @{ Light = '#E5A599'; Dark = '#B0604F' }
    ts   = @{ Light = '#8FCB9C'; Dark = '#468853' }
    m2ts = @{ Light = '#7FC7D8'; Dark = '#3B8397' }
    mpg  = @{ Light = '#E9AE7D'; Dark = '#AE6C34' }
    mpeg = @{ Light = '#E4C67F'; Dark = '#A98C3A' }
    webm = @{ Light = '#B5C67C'; Dark = '#74873B' }
    rmvb = @{ Light = '#DFA1B5'; Dark = '#A4556F' }
}

function Get-Color {
    param([string]$Hex)
    $Hex = $Hex.TrimStart('#')
    return [System.Drawing.Color]::FromArgb(
        255,
        [Convert]::ToInt32($Hex.Substring(0, 2), 16),
        [Convert]::ToInt32($Hex.Substring(2, 2), 16),
        [Convert]::ToInt32($Hex.Substring(4, 2), 16))
}

function New-RoundedRectPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [single]$Radius
    )
    $d = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-PlayTriangle {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size
    )
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $left = [single]($Size * 0.405)
    $right = [single]($Size * 0.695)
    $top = [single]($Size * 0.315)
    $bottom = [single]($Size * 0.685)
    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($left, $top)),
        (New-Object System.Drawing.PointF($left, $bottom)),
        (New-Object System.Drawing.PointF($right, [single]($Size * 0.5)))
    )
    $Graphics.FillPolygon($brush, $points)
    $brush.Dispose()
}

function Add-ExtensionLabel {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size,
        [string]$Label
    )

    $family = New-Object System.Drawing.FontFamily('Segoe UI')
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Near
    $format.LineAlignment = [System.Drawing.StringAlignment]::Near

    $maxWidth = [single]($Size * 0.80)
    $maxHeight = [single]($Size * 0.46)
    $layout = New-Object System.Drawing.RectangleF(0, 0, [single]($Size * 4), [single]($Size * 4))

    $fontSize = [single]($Size * 0.52)
    $path = $null
    while ($fontSize -ge 5) {
        if ($path) { $path.Dispose() }
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddString($Label, $family, [int][System.Drawing.FontStyle]::Bold, $fontSize, $layout, $format)
        $bounds = $path.GetBounds()
        if ($bounds.Width -le $maxWidth -and $bounds.Height -le $maxHeight) {
            break
        }
        $fontSize -= 0.5
    }

    $bounds = $path.GetBounds()
    $dx = [single](($Size - $bounds.Width) / 2 - $bounds.X)
    $dy = [single](($Size - $bounds.Height) / 2 - $bounds.Y)
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($dx, $dy)
    $path.Transform($matrix)

    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $Graphics.FillPath($brush, $path)
    $brush.Dispose()
    $matrix.Dispose()
    $path.Dispose()
    $format.Dispose()
    $family.Dispose()
}

function Write-IcoFile {
    param(
        [string]$Path,
        [string]$Kind,
        [string]$Label,
        [string]$Light,
        [string]$Dark
    )

    $pngs = New-Object System.Collections.Generic.List[byte[]]
    $usedSizes = New-Object System.Collections.Generic.List[int]

    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $lightColor = Get-Color $Light
        $darkColor = Get-Color $Dark

        if ($Kind -eq 'app') {
            $pad = [single]([Math]::Max(0.5, $size * 0.055))
            $rect = New-Object System.Drawing.RectangleF($pad, $pad, [single]($size - 2 * $pad), [single]($size - 2 * $pad))
            $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $lightColor, $darkColor, [single]90)
            $graphics.FillEllipse($brush, $rect)
            $brush.Dispose()
            Add-PlayTriangle -Graphics $graphics -Size $size
        }
        else {
            $pad = [single]($size * 0.04)
            $rect = New-Object System.Drawing.RectangleF($pad, $pad, [single]($size - 2 * $pad), [single]($size - 2 * $pad))
            $tile = New-RoundedRectPath -Rect $rect -Radius ([single]($size * 0.24))
            $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $lightColor, $darkColor, [single]90)
            $graphics.FillPath($brush, $tile)
            $brush.Dispose()
            $tile.Dispose()

            if ($size -ge 32) {
                Add-ExtensionLabel -Graphics $graphics -Size $size -Label $Label
            }
            else {
                Add-PlayTriangle -Graphics $graphics -Size $size
            }
        }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs.Add($stream.ToArray())
        $usedSizes.Add($size)
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($ms)
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$pngs.Count)

    $offset = 6 + 16 * $pngs.Count
    for ($i = 0; $i -lt $pngs.Count; $i++) {
        $size = $usedSizes[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngs[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }

    foreach ($png in $pngs) {
        $writer.Write($png)
    }

    $writer.Flush()
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $writer.Dispose()
    $ms.Dispose()

    Write-Host ("  {0}" -f $Path)
}

Write-Host 'Generating app icon ...'
Write-IcoFile -Path (Join-Path $AssetsDir 'App.ico') -Kind 'app' -Label '' -Light $appPalette.Light -Dark $appPalette.Dark

Write-Host 'Generating file icons ...'
foreach ($ext in $filePalettes.Keys) {
    $palette = $filePalettes[$ext]
    $label = $ext.ToUpperInvariant()
    Write-IcoFile -Path (Join-Path $AssetsDir (Join-Path 'FileIcons' "$ext.ico")) -Kind 'file' -Label $label -Light $palette.Light -Dark $palette.Dark
}

Write-Host 'Done.'
