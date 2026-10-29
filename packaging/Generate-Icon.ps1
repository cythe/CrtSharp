$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $projectRoot 'Assets'
[System.IO.Directory]::CreateDirectory($assetsDirectory) | Out-Null

function New-RoundedRectanglePath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $scale = 4
    $canvasSize = $Size * $scale
    $bitmap = New-Object System.Drawing.Bitmap($canvasSize, $canvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $margin = $canvasSize * 0.045
    $outer = New-RoundedRectanglePath $margin $margin ($canvasSize - 2 * $margin) ($canvasSize - 2 * $margin) ($canvasSize * 0.19)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($canvasSize, $canvasSize)),
        [System.Drawing.Color]::FromArgb(255, 18, 32, 55),
        [System.Drawing.Color]::FromArgb(255, 6, 12, 24))
    $graphics.FillPath($gradient, $outer)
    $outerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 55, 190, 225), ($canvasSize * 0.018))
    $graphics.DrawPath($outerPen, $outer)

    $screenX = $canvasSize * 0.13
    $screenY = $canvasSize * 0.17
    $screenWidth = $canvasSize * 0.74
    $screenHeight = $canvasSize * 0.66
    $screen = New-RoundedRectanglePath $screenX $screenY $screenWidth $screenHeight ($canvasSize * 0.065)
    $screenBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 8, 14, 25))
    $graphics.FillPath($screenBrush, $screen)
    $screenPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 43, 71, 96), ($canvasSize * 0.012))
    $graphics.DrawPath($screenPen, $screen)

    $headerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 43, 71, 96), ($canvasSize * 0.011))
    $graphics.DrawLine($headerPen, $screenX, ($canvasSize * 0.30), ($screenX + $screenWidth), ($canvasSize * 0.30))

    $dotSize = $canvasSize * 0.038
    $dotY = $canvasSize * 0.215
    $dotColors = @(
        [System.Drawing.Color]::FromArgb(255, 241, 76, 76),
        [System.Drawing.Color]::FromArgb(255, 220, 180, 70),
        [System.Drawing.Color]::FromArgb(255, 78, 201, 176))
    for ($index = 0; $index -lt 3; $index++) {
        $dotBrush = New-Object System.Drawing.SolidBrush($dotColors[$index])
        $graphics.FillEllipse($dotBrush, ($canvasSize * (0.20 + 0.075 * $index)), $dotY, $dotSize, $dotSize)
        $dotBrush.Dispose()
    }

    $promptPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 72, 202, 228), ($canvasSize * 0.047))
    $promptPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $promptPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($promptPen, ($canvasSize * 0.27), ($canvasSize * 0.43), ($canvasSize * 0.43), ($canvasSize * 0.56))
    $graphics.DrawLine($promptPen, ($canvasSize * 0.43), ($canvasSize * 0.56), ($canvasSize * 0.27), ($canvasSize * 0.69))

    $cursorPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 126, 231, 135), ($canvasSize * 0.047))
    $cursorPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cursorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($cursorPen, ($canvasSize * 0.51), ($canvasSize * 0.69), ($canvasSize * 0.72), ($canvasSize * 0.69))

    $result = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resultGraphics = [System.Drawing.Graphics]::FromImage($result)
    $resultGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $resultGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $resultGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $resultGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $resultGraphics.DrawImage($bitmap, 0, 0, $Size, $Size)

    $stream = New-Object System.IO.MemoryStream
    $result.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()

    $stream.Dispose()
    $resultGraphics.Dispose()
    $result.Dispose()
    $cursorPen.Dispose()
    $promptPen.Dispose()
    $headerPen.Dispose()
    $screenPen.Dispose()
    $screenBrush.Dispose()
    $screen.Dispose()
    $outerPen.Dispose()
    $gradient.Dispose()
    $outer.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $images += ,(New-IconPng $size)
}

$iconPath = Join-Path $assetsDirectory 'CrtSharp.ico'
$file = New-Object System.IO.FileStream($iconPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $bytes = $images[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) {
    $writer.Write($bytes)
}
$writer.Dispose()
$file.Dispose()

[System.IO.File]::WriteAllBytes((Join-Path $assetsDirectory 'CrtSharp-256.png'), $images[-1])
Write-Output "Generated $iconPath"
