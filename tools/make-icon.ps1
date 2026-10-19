$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
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

$outer = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
$outerPath = New-RoundedRectangle $outer 58
$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(20, 20)),
    (New-Object System.Drawing.PointF(236, 236)),
    [System.Drawing.Color]::FromArgb(255, 108, 80, 232),
    [System.Drawing.Color]::FromArgb(255, 255, 105, 140))
$graphics.FillPath($gradient, $outerPath)

$innerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(35, 255, 255, 255), 1.5)
$graphics.DrawEllipse($innerPen, 170, -20, 170, 170)
$graphics.DrawEllipse($innerPen, 205, 15, 100, 100)
$graphics.DrawEllipse($innerPen, -30, 168, 120, 120)

$warmBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(62, 255, 255, 255))
$graphics.FillEllipse($warmBrush, 62, 86, 132, 132)

$barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$barHeights = @(54, 104, 78, 126, 66, 92)
$barWidth = 13
$gap = 9
$totalWidth = ($barHeights.Count * $barWidth) + (($barHeights.Count - 1) * $gap)
$startX = [float](($size - $totalWidth) / 2)
$centerY = 128

for ($i = 0; $i -lt $barHeights.Count; $i++) {
    $height = $barHeights[$i]
    $rect = New-Object System.Drawing.RectangleF(
        ($startX + $i * ($barWidth + $gap)),
        ($centerY - $height / 2),
        $barWidth,
        $height)
    $barPath = New-RoundedRectangle $rect 6.5
    if ($i -eq 0 -or $i -eq 5) {
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(205, 255, 255, 255))
    } else {
        $brush = $barBrush
    }
    $graphics.FillPath($brush, $barPath)
    $barPath.Dispose()
    if ($i -eq 0 -or $i -eq 5) { $brush.Dispose() }
}

$pngPath = Join-Path $assets 'lumen-256.png'
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()
$outerPath.Dispose()
$gradient.Dispose()
$innerPen.Dispose()
$warmBrush.Dispose()
$barBrush.Dispose()

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
