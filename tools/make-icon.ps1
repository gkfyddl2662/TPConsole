# Draws the TPConsole icon (no bundled art): a dark rounded square with two level bars and a routing
# dot, rendered per size for crisp small icons, packed as a PNG-compressed multi-size .ico.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\TPConsole.App\app.ico'
$png = Join-Path $root 'web\public\icon.png'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Draw([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $n / 32.0
    # background: rounded square
    $r = [Math]::Max(2, 7 * $s)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $n - 1
    $p.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90); $p.AddArc($w - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc(0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x22, 0x22, 0x1f))), $p)
    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xe0, 0xa2, 0x4e))
    $mint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x7c, 0xc4, 0xa4))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x8e, 0xb1, 0xe0))
    # three level bars in the mix colours, bottom-aligned at different heights, rounded caps
    function Bar($brush, [double]$x, [double]$top, [double]$bottom, [double]$bw) {
        $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rr = $bw / 2
        $bp.AddArc($x, $top, $bw, $bw, 180, 180)
        $bp.AddLine($x + $bw, $top + $rr, $x + $bw, $bottom - $rr)
        $bp.AddArc($x, $bottom - $bw, $bw, $bw, 0, 180)
        $bp.CloseFigure()
        $g.FillPath($brush, $bp)
    }
    $bw = 4.6 * $s
    Bar $amber (6.4 * $s) (12 * $s) (25.5 * $s) $bw
    Bar $mint (13.7 * $s) (6.5 * $s) (25.5 * $s) $bw
    Bar $blue (21 * $s) (16 * $s) (25.5 * $s) $bw
    $g.Dispose()
    return $bmp
}

$images = foreach ($n in $sizes) {
    $b = Draw $n
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($n -eq 256) { $b.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO: header, one directory entry per image, then the PNG data.
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($n % 256)); $bw.Write([byte]($n % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $out and $png"
