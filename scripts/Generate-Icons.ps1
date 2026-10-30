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
    mkv  = @{ Light = '#65E8D4'; Dark = '#20B8A7' }
    mp4  = @{ Light = '#64C7FF'; Dark = '#3478F6' }
    m4v  = @{ Light = '#B5C0FF'; Dark = '#7C83F0' }
    mov  = @{ Light = '#E0B7FF'; Dark = '#AF72E8' }
    avi  = @{ Light = '#FFD58A'; Dark = '#FF9F0A' }
    wmv  = @{ Light = '#C4E2F5'; Dark = '#70A7C9' }
    flv  = @{ Light = '#FFB4A6'; Dark = '#FF6F61' }
    ts   = @{ Light = '#A0F2B8'; Dark = '#34C759' }
    m2ts = @{ Light = '#93E7F5'; Dark = '#32ADE6' }
    mpg  = @{ Light = '#FFC48A'; Dark = '#FF8A34' }
    mpeg = @{ Light = '#FFE49A'; Dark = '#E5B93B' }
    webm = @{ Light = '#DDF19A'; Dark = '#9EC13E' }
    rmvb = @{ Light = '#FFC2D5'; Dark = '#F7779E' }
    rm   = @{ Light = '#FFC2D5'; Dark = '#F7779E' }
    '3gp' = @{ Light = '#64C7FF'; Dark = '#3478F6' }
    '3g2' = @{ Light = '#64C7FF'; Dark = '#3478F6' }
    ogv  = @{ Light = '#DDF19A'; Dark = '#9EC13E' }
    mts  = @{ Light = '#93E7F5'; Dark = '#32ADE6' }
    vob  = @{ Light = '#FFC48A'; Dark = '#FF8A34' }
    mxf  = @{ Light = '#B5C0FF'; Dark = '#7C83F0' }
    dv   = @{ Light = '#FFD58A'; Dark = '#FF9F0A' }
    wtv  = @{ Light = '#C4E2F5'; Dark = '#70A7C9' }
    y4m  = @{ Light = '#FFE49A'; Dark = '#E5B93B' }
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

function New-FilePath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [single]$Radius,
        [single]$Fold
    )

    $d = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddLine($Rect.X + $Radius, $Rect.Y, $Rect.Right - $Fold, $Rect.Y)
    $path.AddLine($Rect.Right - $Fold, $Rect.Y, $Rect.Right, $Rect.Y + $Fold)
    $path.AddLine($Rect.Right, $Rect.Y + $Fold, $Rect.Right, $Rect.Bottom - $Radius)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-ModernPlayMark {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size,
        [bool]$ShowOrbit = $true
    )

    $centerY = if ($ShowOrbit) { 0.39 } else { 0.46 }
    $diameter = [single]($Size * 0.39)
    $orbitRect = New-Object System.Drawing.RectangleF(
        [single]($Size * 0.5 - $diameter / 2),
        [single]($Size * $centerY - $diameter / 2),
        $diameter,
        $diameter)

    if ($ShowOrbit) {
        $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(24, 126, 211, 201), [single]($Size * 0.070))
        $glowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $glowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawArc($glowPen, $orbitRect, 38, 286)
        $glowPen.Dispose()

        $orbitBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $orbitRect,
            [System.Drawing.Color]::FromArgb(235, 190, 255, 246),
            [System.Drawing.Color]::FromArgb(75, 255, 255, 255),
            [single]45)
        $orbitPen = New-Object System.Drawing.Pen($orbitBrush, [single]([Math]::Max(0.8, $Size * 0.018)))
        $orbitPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $orbitPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawArc($orbitPen, $orbitRect, 38, 286)
        $orbitPen.Dispose()
        $orbitBrush.Dispose()
    }

    $triangleRect = New-Object System.Drawing.RectangleF(
        [single]($Size * 0.415),
        [single]($Size * $centerY - $Size * 0.105),
        [single]($Size * 0.19),
        [single]($Size * 0.21))
    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($triangleRect.X, $triangleRect.Y)),
        (New-Object System.Drawing.PointF($triangleRect.X, $triangleRect.Bottom)),
        (New-Object System.Drawing.PointF($triangleRect.Right, [single]($triangleRect.Y + $triangleRect.Height / 2)))
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($points)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $triangleRect, [System.Drawing.Color]::White, [System.Drawing.Color]::FromArgb(255, 206, 248, 242), [single]90)
    $Graphics.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen($brush, [single]([Math]::Max(0.6, $Size * 0.016)))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $Graphics.DrawPath($pen, $path)
    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
}

function Add-ModernFileIcon {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size,
        [string]$Label,
        [System.Drawing.Color]$Light,
        [System.Drawing.Color]$Dark
    )

    $rect = New-Object System.Drawing.RectangleF(
        [single]($Size * 0.14), [single]($Size * 0.055),
        [single]($Size * 0.72), [single]($Size * 0.875))
    $fold = [single]($Size * 0.19)
    $radius = [single]($Size * 0.105)
    $file = New-FilePath -Rect $rect -Radius $radius -Fold $fold

    for ($i = 7; $i -ge 1; $i--) {
        $shadow = $file.Clone()
        $matrix = New-Object System.Drawing.Drawing2D.Matrix
        $matrix.Translate(0, [single]($Size * (0.008 + $i * 0.0025)))
        $shadow.Transform($matrix)
        $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(4, 5, 20, 24))
        $Graphics.FillPath($shadowBrush, $shadow)
        $shadowBrush.Dispose()
        $matrix.Dispose()
        $shadow.Dispose()
    }

    $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $Light, $Dark, [single]55)
    $Graphics.FillPath($baseBrush, $file)
    $baseBrush.Dispose()

    $Graphics.SetClip($file)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse([single](-$Size * 0.18), [single](-$Size * 0.24), [single]($Size * 1.05), [single]($Size * 0.92))
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(64, 255, 255, 255)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $glowBrush.CenterPoint = New-Object System.Drawing.PointF([single]($Size * 0.28), [single]($Size * 0.18))
    $Graphics.FillEllipse($glowBrush, [single](-$Size * 0.18), [single](-$Size * 0.24), [single]($Size * 1.05), [single]($Size * 0.92))
    $glowBrush.Dispose()
    $glowPath.Dispose()
    $Graphics.ResetClip()

    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(105, 255, 255, 255), [single]([Math]::Max(0.45, $Size * 0.006)))
    $Graphics.DrawPath($borderPen, $file)
    $borderPen.Dispose()

    $foldPoints = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([single]($rect.Right - $fold), $rect.Y)),
        (New-Object System.Drawing.PointF($rect.Right, [single]($rect.Y + $fold))),
        (New-Object System.Drawing.PointF([single]($rect.Right - $fold), [single]($rect.Y + $fold)))
    )
    $foldBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(64, 255, 255, 255))
    $Graphics.FillPolygon($foldBrush, $foldPoints)
    $foldBrush.Dispose()
    $foldPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(82, 255, 255, 255), [single]([Math]::Max(0.4, $Size * 0.005)))
    $Graphics.DrawLine($foldPen, [single]($rect.Right - $fold), $rect.Y, [single]($rect.Right - $fold), [single]($rect.Y + $fold))
    $Graphics.DrawLine($foldPen, [single]($rect.Right - $fold), [single]($rect.Y + $fold), $rect.Right, [single]($rect.Y + $fold))
    $foldPen.Dispose()

    $showDetails = $Size -ge 32
    Add-ModernPlayMark -Graphics $Graphics -Size $Size -ShowOrbit $showDetails
    if ($showDetails) {
        Add-ExtensionLabel -Graphics $Graphics -Size $Size -Label $Label -CenterY 0.79 -MaxWidthRatio 0.58 -MaxHeightRatio 0.16
    }
    $file.Dispose()
}

function Add-ExtensionLabel {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size,
        [string]$Label,
        [single]$CenterY = 0.5,
        [single]$MaxWidthRatio = 0.80,
        [single]$MaxHeightRatio = 0.46
    )

    $family = New-Object System.Drawing.FontFamily('Segoe UI')
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Near
    $format.LineAlignment = [System.Drawing.StringAlignment]::Near

    $maxWidth = [single]($Size * $MaxWidthRatio)
    $maxHeight = [single]($Size * $MaxHeightRatio)
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
    $dy = [single]($Size * $CenterY - $bounds.Height / 2 - $bounds.Y)
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
            Add-ModernFileIcon -Graphics $graphics -Size $size -Label $Label -Light $lightColor -Dark $darkColor
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
