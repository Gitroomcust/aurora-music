param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$WindowWidth = 1108,
    [int]$WindowHeight = 700,
    [int]$Inset = 14
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path $InputFile))
try {
    $contentWidth = $WindowWidth - ($Inset * 2)
    $contentHeight = $WindowHeight - ($Inset * 2)
    $content = New-Object System.Drawing.Bitmap($contentWidth, $contentHeight)
    $contentGraphics = [System.Drawing.Graphics]::FromImage($content)
    $contentGraphics.DrawImage($source,
        (New-Object System.Drawing.Rectangle(0, 0, $contentWidth, $contentHeight)),
        (New-Object System.Drawing.Rectangle(($X + $Inset), ($Y + $Inset), $contentWidth, $contentHeight)),
        [System.Drawing.GraphicsUnit]::Pixel)
    $contentGraphics.Dispose()

    $padding = 20
    $canvas = New-Object System.Drawing.Bitmap(($contentWidth + $padding * 2), ($contentHeight + $padding * 2))
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 241, 236, 249))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = 28
    $diameter = $radius * 2
    $rect = New-Object System.Drawing.RectangleF($padding, $padding, $contentWidth, $contentHeight)
    $arc = New-Object System.Drawing.RectangleF($rect.X, $rect.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $rect.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $rect.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $rect.Left
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()

    $graphics.SetClip($path)
    $graphics.DrawImage($content, $padding, $padding)
    $graphics.ResetClip()

    $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(35, 40, 28, 70), 1)
    $graphics.DrawPath($border, $path)

    $canvas.Save($OutputFile, [System.Drawing.Imaging.ImageFormat]::Png)

    $border.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
    $content.Dispose()
}
finally {
    $source.Dispose()
}

Write-Output "Saved $OutputFile"
