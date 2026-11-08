# Generates src/Desktop/Assets/app.ico: an original "segmented disk" mark rendered at several sizes.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Desktop\Assets\app.ico'))
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(1, [int]($size * 0.06))
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $blue = [System.Drawing.Color]::FromArgb(255, 79, 128, 253)
    $light = [System.Drawing.Color]::FromArgb(255, 160, 189, 255)
    $violet = [System.Drawing.Color]::FromArgb(255, 157, 107, 255)
    $g.FillPie((New-Object System.Drawing.SolidBrush $blue), $rect.X, $rect.Y, $rect.Width, $rect.Height, -90, 220)
    $g.FillPie((New-Object System.Drawing.SolidBrush $light), $rect.X, $rect.Y, $rect.Width, $rect.Height, 130, 85)
    $g.FillPie((New-Object System.Drawing.SolidBrush $violet), $rect.X, $rect.Y, $rect.Width, $rect.Height, 215, 55)
    $hole = $size * 0.36
    $g.FillEllipse([System.Drawing.Brushes]::White, ($size - $hole) / 2, ($size - $hole) / 2, $hole, $hole)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1, $size * 0.035))
    foreach ($angle in -90, 130, 215) {
        $rad = $angle * [Math]::PI / 180
        $c = $size / 2
        $g.DrawLine($pen, $c, $c, $c + [Math]::Cos($rad) * $size, $c + [Math]::Sin($rad) * $size)
    }
    $g.FillEllipse([System.Drawing.Brushes]::White, ($size - $hole) / 2, ($size - $hole) / 2, $hole, $hole)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"wrote $Out"
