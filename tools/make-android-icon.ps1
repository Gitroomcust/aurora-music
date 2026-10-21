$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Generates the Android launcher icons in the Aurora palette.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-android-icon.ps1

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$res = Join-Path $root 'android\app\src\main\res\drawable'
New-Item -ItemType Directory -Force -Path $res | Out-Null

$size = 256

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $arc = New-Object System.Drawing.RectangleF($x, $y, $d, $d)
    $path.AddArc($arc, 180, 90)
    $arc.X = $x + $w - $d
    $path.AddArc($arc, 270, 90)
    $arc.Y = $y + $h - $d
    $path.AddArc($arc, 0, 90)
    $arc.X = $x
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

function New-Icon([string]$file, [bool]$round) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
    if ($round) {
        $base = New-Object System.Drawing.Drawing2D.GraphicsPath
        $base.AddEllipse($rect)
    } else {
        $base = New-RoundedPath 8 8 240 240 58
    }

    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(8, 8)),
        (New-Object System.Drawing.PointF(248, 248)),
        [System.Drawing.ColorTranslator]::FromHtml('#8B5CF6'),
        [System.Drawing.ColorTranslator]::FromHtml('#22D3EE'))
    $g.FillPath($gradient, $base)

    $g.SetClip($base)
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(24, 128, 208, 208)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(90, 244, 114, 182)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 244, 114, 182))
    $g.FillPath($glowBrush, $glow)

    $highlight = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, 170)),
        [System.Drawing.Color]::FromArgb(34, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillRectangle($highlight, 0, 0, $size, 170)
    $g.ResetClip()

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#05050C'))
    $spark = New-SparkPath 126 126 74 0.42
    $g.FillPath($ink, $spark)
    $g.FillEllipse($ink, 176, 62, 20, 20)

    $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)

    $ink.Dispose()
    $highlight.Dispose()
    $glowBrush.Dispose()
    $glow.Dispose()
    $gradient.Dispose()
    $base.Dispose()
    $spark.Dispose()
    $g.Dispose()
    $bitmap.Dispose()
}

New-Icon (Join-Path $res 'ic_launcher.png') $false
New-Icon (Join-Path $res 'ic_launcher_round.png') $true
Write-Host "Aurora launcher icons generated in $res"
