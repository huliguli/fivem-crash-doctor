# Erzeugt src/CrashDoctor/Assets/app.ico (16/24/32/48/64/256 px, PNG-Einträge) mit System.Drawing.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\CrashDoctor\Assets\app.ico'))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(3, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 203, 143))), $path)
    # Herzschlag-Linie
    $w = [Math]::Max(1.6, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 10, 30, 22)), $w
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $pts = @(
        [System.Drawing.PointF]::new($s * 0.14, $s * 0.55),
        [System.Drawing.PointF]::new($s * 0.36, $s * 0.55),
        [System.Drawing.PointF]::new($s * 0.45, $s * 0.28),
        [System.Drawing.PointF]::new($s * 0.57, $s * 0.76),
        [System.Drawing.PointF]::new($s * 0.66, $s * 0.48),
        [System.Drawing.PointF]::new($s * 0.86, $s * 0.48)
    )
    $g.DrawLines($pen, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Icon geschrieben: $Out"
