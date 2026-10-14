# Genere l'icone de Capture Zeus (dessin original) : un temple dore dans un cadre de capture, sur fond bleu nuit.
# Fichier .ico multi-tailles : 256 px en PNG, 48 / 32 / 16 px en BMP 32 bits (Windows Vista et suivants).
param([string]$Out = (Join-Path $PSScriptRoot '..\src\capture-zeus.ico'))
Add-Type -AssemblyName System.Drawing

function Draw([int]$s) {
    $b = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $k = $s / 256.0
    # fond : carre arrondi bleu nuit
    $r = 44 * $k; $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8*$k, 8*$k, 2*$r, 2*$r, 180, 90); $path.AddArc(248*$k - 2*$r, 8*$k, 2*$r, 2*$r, 270, 90)
    $path.AddArc(248*$k - 2*$r, 248*$k - 2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(8*$k, 248*$k - 2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $s), [Drawing.Color]::FromArgb(28, 58, 110), [Drawing.Color]::FromArgb(12, 28, 60))
    $g.FillPath($bg, $path)
    # cadre de capture : quatre coins blancs
    $w = [Math]::Max(1.5, 14 * $k); $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $a = 40 * $k; $z = 216 * $k; $l = 46 * $k
    foreach ($c in @(@($a, $a, 1, 1), @($z, $a, -1, 1), @($a, $z, 1, -1), @($z, $z, -1, -1))) {
        $g.DrawLine($pen, $c[0], $c[1], $c[0] + $c[2] * $l, $c[1]); $g.DrawLine($pen, $c[0], $c[1], $c[0], $c[1] + $c[3] * $l)
    }
    # temple dore : fronton, entablement, colonnes, marches
    $gold = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(236, 186, 74))
    $pts = [Drawing.PointF[]]@((New-Object Drawing.PointF (128*$k), (62*$k)), (New-Object Drawing.PointF (196*$k), (100*$k)), (New-Object Drawing.PointF (60*$k), (100*$k)))
    $g.FillPolygon($gold, $pts)
    $g.FillRectangle($gold, 64*$k, 104*$k, 128*$k, 12*$k)
    if ($s -ge 32) { foreach ($x in 72, 104, 136, 168) { $g.FillRectangle($gold, $x*$k, 122*$k, 16*$k, 58*$k) } }
    else { foreach ($x in 72, 120, 168) { $g.FillRectangle($gold, $x*$k, 122*$k, 18*$k, 58*$k) } }
    $g.FillRectangle($gold, 58*$k, 184*$k, 140*$k, 12*$k)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    if ($s -ge 256) { $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png) }   # 256 px : PNG (format prevu par Windows)
    else {
        # petites tailles : image BMP 32 bits classique (en-tete, pixels de bas en haut, masque)
        $bw = New-Object IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($s * $s * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $b.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
        $stride = [int]([Math]::Ceiling($s / 32.0) * 4)
        for ($y = $s - 1; $y -ge 0; $y--) { $row = New-Object byte[] $stride; for ($x = 0; $x -lt $s; $x++) { if ($b.GetPixel($x, $y).A -lt 128) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) } }; $bw.Write($row) }
        $bw.Flush()
    }
    $b.Dispose()
    return , $ms.ToArray()
}

$sizes = 256, 48, 32, 16
$imgs = @(); foreach ($s in $sizes) { $imgs += , (Draw $s) }
$fs = [IO.File]::Create($Out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$imgs[$i].Length); $w.Write([uint32]$off)
    $off += $imgs[$i].Length
}
foreach ($img in $imgs) { $w.Write($img) }
$w.Close()
"icone : $Out"
