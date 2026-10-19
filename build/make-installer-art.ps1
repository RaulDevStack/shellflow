# Generates installer\License.rtf from LICENSE and the WiX wizard bitmaps (Banner.bmp 493x58, Dialog.bmp 493x312).
# Run with Windows PowerShell 5.1:  powershell -File build\make-installer-art.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'installer'

# License.rtf
$text = Get-Content (Join-Path $root 'LICENSE') -Raw
$escaped = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}') -replace "`r?`n", "\par`r`n"
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18`r`n$escaped`r`n}"
[IO.File]::WriteAllText((Join-Path $out 'License.rtf'), $rtf, [Text.Encoding]::ASCII)

$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'src\ShellFlow.App\Assets\ShellFlow.png'))
$bg = [System.Drawing.Color]::FromArgb(255, 0x18, 0x1A, 0x20)
$fg = [System.Drawing.Color]::FromArgb(255, 0xF0, 0xF0, 0xF5)
$muted = [System.Drawing.Color]::FromArgb(255, 0xA0, 0xA6, 0xB5)
$accent = [System.Drawing.Color]::FromArgb(255, 0xA6, 0xB7, 0xFF)

# Banner: shown at the top of inner pages, with dark text drawn by Windows on the left, so keep the left side light.
$banner = New-Object System.Drawing.Bitmap 493, 58
$g = [System.Drawing.Graphics]::FromImage($banner)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
$g.Clear([System.Drawing.Color]::White)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 300, 0), (New-Object System.Drawing.Point 493, 0), ([System.Drawing.Color]::White), $bg
$g.FillRectangle($brush, 300, 0, 193, 58)
$g.DrawImage($icon, 445, 11, 36, 36)
$g.Dispose()
$banner.Save((Join-Path $out 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)

# Dialog: welcome/finish pages. Windows draws the title and text on the right part (from x≈164), keep it light there.
$dialog = New-Object System.Drawing.Bitmap 493, 312
$g = [System.Drawing.Graphics]::FromImage($dialog)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $bg), 0, 0, 164, 312)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 162, 0, 2, 312)
$g.DrawImage($icon, 42, 70, 80, 80)
$format = New-Object System.Drawing.StringFormat; $format.Alignment = 'Center'
$g.DrawString('ShellFlow', (New-Object System.Drawing.Font 'Segoe UI Semibold', 14), (New-Object System.Drawing.SolidBrush $fg), (New-Object System.Drawing.RectangleF 0, 165, 164, 30), $format)
$g.DrawString('Your terminal. In flow.', (New-Object System.Drawing.Font 'Segoe UI', 8.5), (New-Object System.Drawing.SolidBrush $muted), (New-Object System.Drawing.RectangleF 0, 195, 164, 20), $format)
$g.Dispose()
$dialog.Save((Join-Path $out 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$icon.Dispose()
Write-Host "Wrote License.rtf, Banner.bmp and Dialog.bmp to $out"
