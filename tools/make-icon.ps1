$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Builds the Windows icon (PNG + ICO) in the Aurora palette.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

function New-RoundedRectangle([System.Drawing.RectangleF]$rect, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $arc = New-Object System.Drawing.RectangleF($rect.X, $rect.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $rect.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $rect.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $rect.Left
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SparkPath([float]$cx, [float]$cy, [float]$outer, [float]$pull) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $points = @()
    for ($i = 0; $i -lt 4; $i++) {
        $angle = ((-90 + 90 * $i) * [Math]::PI) / 180
        $points += New-Object System.Drawing.PointF(
            ($cx + $outer * [Math]::Cos($angle)),
            ($cy + $outer * [Math]::Sin($angle)))
    }
    $path.StartFigure()
    for ($i = 0; $i -lt 4; $i++) {
        $p0 = $points[$i]
        $p1 = $points[($i + 1) % 4]
        $c1 = New-Object System.Drawing.PointF(($cx + ($p0.X - $cx) * $pull), ($cy + ($p0.Y - $cy) * $pull))
        $c2 = New-Object System.Drawing.PointF(($cx + ($p1.X - $cx) * $pull), ($cy + ($p1.Y - $cy) * $pull))
        $path.AddBezier($p0, $c1, $c2, $p1)
    }
    $path.CloseFigure()
    return $path
}

$outer = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
$outerPath = New-RoundedRectangle $outer 58

$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(8, 8)),
    (New-Object System.Drawing.PointF(248, 248)),
    [System.Drawing.ColorTranslator]::FromHtml('#8B5CF6'),
    [System.Drawing.ColorTranslator]::FromHtml('#22D3EE'))
$graphics.FillPath($gradient, $outerPath)

$graphics.SetClip($outerPath)
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(24, 128, 208, 208)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$glow.CenterColor = [System.Drawing.Color]::FromArgb(90, 244, 114, 182)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 244, 114, 182))
$graphics.FillPath($glow, $glowPath)

$highlight = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF(0, 170)),
    [System.Drawing.Color]::FromArgb(34, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$graphics.FillRectangle($highlight, 0, 0, $size, 170)
$graphics.ResetClip()

$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#05050C'))
$spark = New-SparkPath 126 126 74 0.42
$graphics.FillPath($ink, $spark)
$graphics.FillEllipse($ink, 176, 62, 20, 20)

$pngPath = Join-Path $assets 'lumen-256.png'
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$spark.Dispose()
$ink.Dispose()
$highlight.Dispose()
$glow.Dispose()
$glowPath.Dispose()
$gradient.Dispose()
$outerPath.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

$png = [System.IO.File]::ReadAllBytes($pngPath)
$icoPath = Join-Path $assets 'lumen.ico'
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$png.Length)
$writer.Write([UInt32]22)
$writer.Write($png)
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Output "Icon written: $icoPath"
