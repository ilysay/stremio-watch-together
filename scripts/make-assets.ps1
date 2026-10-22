# Regenerates src\assets from the source images in design\ (run only when one of them changes):
#   logo-source.png  -> logo.png + app.ico (window header, exe icon)
#   diary-source.png -> histo.png + histo.ico (diary button and windows)
Set-Location (Split-Path $PSScriptRoot)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force src\assets | Out-Null

# Image découpée en cercle (le fond carré disparaît), à la taille demandée.
function Round($src, [int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $m = [int]($size * 0.07)   # le cercle occupe ~86 % de l'image
    $path.AddEllipse($m, $m, $size - 2 * $m, $size - 2 * $m)
    $g.SetClip($path)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

# .ico multi-tailles (entrées PNG, format accepté par Windows Vista et suivants).
function Write-Ico($src, [int[]]$sizes, [string]$out) {
    $pngs = foreach ($s in $sizes) { $ms = New-Object IO.MemoryStream; (Round $src $s).Save($ms, [Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
    $stream = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $stream
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length; $b = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot $out), $stream.ToArray())
}

$wt = [Drawing.Image]::FromFile((Resolve-Path design\logo-source.png))
(Round $wt 128).Save("src\assets\logo.png", [Drawing.Imaging.ImageFormat]::Png)
Write-Ico $wt @(16, 24, 32, 48, 64, 128, 256) "src\assets\app.ico"
$wt.Dispose()

$histo = [Drawing.Image]::FromFile((Resolve-Path design\diary-source.png))
(Round $histo 96).Save("src\assets\histo.png", [Drawing.Imaging.ImageFormat]::Png)
Write-Ico $histo @(16, 24, 32, 48) "src\assets\histo.ico"
$histo.Dispose()

Get-ChildItem src\assets | Select-Object Name, Length
