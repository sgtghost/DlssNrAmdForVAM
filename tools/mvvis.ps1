param([string[]]$Dirs, [string]$Out, [int]$RX = 0, [int]$RY = 0, [int]$RW = 0, [int]$RH = 0)
# Side by side for each dump dir: frame1 colour and motion magnitude (25 grey levels per pixel of motion)
# of a region. PowerShell names are case-insensitive, hence RX..RH rather than X..H.
Add-Type -AssemblyName System.Drawing
$Dirs = @($Dirs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$first = [Drawing.Bitmap]::FromFile("$($Dirs[0])\frame1\input_colour.png")
if ($RW -le 0) { $RX = 0; $RY = 0; $RW = $first.Width; $RH = $first.Height }
$first.Dispose()
$img = New-Object Drawing.Bitmap ($RW * 2 + 10), (($RH + 10) * $Dirs.Count)
$g = [Drawing.Graphics]::FromImage($img)
$g.Clear([Drawing.Color]::Magenta)
$row = 0
foreach ($d in $Dirs) {
    $col = [Drawing.Bitmap]::FromFile("$d\frame1\input_colour.png")
    $w = $col.Width; $h = $col.Height
    $g.DrawImage($col, (New-Object Drawing.Rectangle 0, ($row * ($RH + 10)), $RW, $RH), (New-Object Drawing.Rectangle $RX, $RY, $RW, $RH), [Drawing.GraphicsUnit]::Pixel)
    $col.Dispose()
    $raw = [IO.File]::ReadAllBytes("$d\frame1\input_motion.raw")
    $mv = New-Object float[] ($raw.Length / 4)
    [Buffer]::BlockCopy($raw, 0, $mv, 0, $raw.Length)
    $vis = New-Object Drawing.Bitmap $RW, $RH
    for ($y = 0; $y -lt $RH; $y++) {
        for ($x = 0; $x -lt $RW; $x++) {
            $k = ($RY + $y) * $w + ($RX + $x)
            $mx = $mv[2 * $k] * $w; $my = $mv[2 * $k + 1] * $h
            $m = [Math]::Min(255, [int]([Math]::Sqrt($mx * $mx + $my * $my) * 25))
            $vis.SetPixel($x, $y, [Drawing.Color]::FromArgb($m, $m, $m))
        }
    }
    $g.DrawImage($vis, ($RW + 10), ($row * ($RH + 10)))
    $vis.Dispose()
    $row++
}
$img.Save($Out)
"saved $Out"
