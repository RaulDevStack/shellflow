# Renders the ShellFlow application icon (ICO with PNG frames) and a 256px PNG.
# Run with Windows PowerShell 5.1 (uses System.Drawing):  powershell -File build\make-icon.ps1
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\ShellFlow.App\Assets')
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $tile = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x30, 0x3D)), ([System.Drawing.Color]::FromArgb(255, 0x17, 0x19, 0x1F))
    $g.FillPath($bg, $tile)
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0xA6, 0xB7, 0xFF)), ([Math]::Max(1, 3 * $s))
    $g.DrawPath($border, $tile)

    # Prompt chevron
    $chevron = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xA6, 0xB7, 0xFF)), ([Math]::Max(1.6, 26 * $s))
    $chevron.StartCap = 'Round'; $chevron.EndCap = 'Round'; $chevron.LineJoin = 'Round'
    $g.DrawLines($chevron, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (70 * $s), (82 * $s)),
        (New-Object System.Drawing.PointF (124 * $s), (128 * $s)),
        (New-Object System.Drawing.PointF (70 * $s), (174 * $s))))

    # Flow stroke (a soft wave where the cursor would be)
    $flow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xA3, 0xDC, 0xC5)), ([Math]::Max(1.6, 22 * $s))
    $flow.StartCap = 'Round'; $flow.EndCap = 'Round'
    $g.DrawBezier($flow,
        (New-Object System.Drawing.PointF (140 * $s), (176 * $s)),
        (New-Object System.Drawing.PointF (158 * $s), (156 * $s)),
        (New-Object System.Drawing.PointF (178 * $s), (196 * $s)),
        (New-Object System.Drawing.PointF (196 * $s), (176 * $s)))

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bmp = Render $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir 'ShellFlow.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'ShellFlow.ico'), $ico.ToArray())
Write-Host "Wrote ShellFlow.ico and ShellFlow.png to $OutDir"
