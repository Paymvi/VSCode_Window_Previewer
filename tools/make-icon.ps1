# Regenerates src/ProjectPreviews/Assets/app.ico (stacked "window cards" glyph). Run once; the .ico is checked in.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\ProjectPreviews\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); $p
}

$pngs = foreach ($s in 16, 24, 32, 48, 64, 256) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 32.0
    # back card
    $back = RoundRect (7 * $k) (3 * $k) (22 * $k) (17 * $k) (3.5 * $k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 70, 229))), $back)
    # front card
    $front = RoundRect (3 * $k) (11 * $k) (22 * $k) (17 * $k) (3.5 * $k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 14, 165, 233))), $front)
    # "alias" bar centered on the front card
    $bar = RoundRect (7.5 * $k) (18 * $k) (13 * $k) ([Math]::Max(2.5 * $k, 1.5)) (1.2 * $k)
    $g.FillPath([System.Drawing.Brushes]::White, $bar)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , @($s, $ms.ToArray())
}

$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $d = $p[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()
"wrote $out"
