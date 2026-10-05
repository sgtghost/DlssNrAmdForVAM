param([string]$Dir, [int]$RX = 0, [int]$RY = 0, [int]$RW = 0, [int]$RH = 0)  # region (PowerShell names are case-insensitive: not X/W)
# Motion-vector quality inside a region of two consecutive dumped frames (frame0 = previous, frame1 =
# current): mean luma error of reprojecting frame1 onto frame0 with the stored motion (prev = p +
# (-mvx*w, +mvy*h), the convention mvcheck.ps1 established) versus with zero motion, over all textured
# pixels, moving or not. A ratio well below 1 means the motion vectors describe what moved.
Add-Type -AssemblyName System.Drawing
function Luma([string]$path) {
    $b = [Drawing.Bitmap]::FromFile($path)
    $r = New-Object Drawing.Rectangle 0, 0, $b.Width, $b.Height
    $d = $b.LockBits($r, 'ReadOnly', [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $bytes = New-Object byte[] ($d.Stride * $b.Height)
    [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $bytes, 0, $bytes.Length)
    $b.UnlockBits($d)
    $l = New-Object float[] ($b.Width * $b.Height)
    for ($y = 0; $y -lt $b.Height; $y++) { $o = $y * $d.Stride; for ($x = 0; $x -lt $b.Width; $x++) { $i = $o + 3 * $x; $l[$y * $b.Width + $x] = 0.114 * $bytes[$i] + 0.587 * $bytes[$i + 1] + 0.299 * $bytes[$i + 2] } }
    $res = @{ W = $b.Width; H = $b.Height; L = $l }; $b.Dispose(); $res
}
$c0 = Luma "$Dir\frame0\input_colour.png"
$c1 = Luma "$Dir\frame1\input_colour.png"
$w = $c1.W; $h = $c1.H
if ($RW -le 0) { $RX = 0; $RY = 0; $RW = $w; $RH = $h }
$raw = [IO.File]::ReadAllBytes("$Dir\frame1\input_motion.raw")
$mv = New-Object float[] ($raw.Length / 4)
[Buffer]::BlockCopy($raw, 0, $mv, 0, $raw.Length)
$eMv = 0.0; $eZero = 0.0; $n = 0; $moving = 0
for ($y = [Math]::Max(8, $RY); $y -lt [Math]::Min($h - 8, $RY + $RH); $y += 2) {
    for ($x = [Math]::Max(8, $RX); $x -lt [Math]::Min($w - 8, $RX + $RW); $x += 2) {
        $k = $y * $w + $x
        $g = [Math]::Abs($c1.L[$k + 1] - $c1.L[$k - 1]) + [Math]::Abs($c1.L[$k + $w] - $c1.L[$k - $w])
        if ($g -lt 6) { continue }
        $mx = $mv[2 * $k] * $w; $my = $mv[2 * $k + 1] * $h
        if ([Math]::Abs($mx) + [Math]::Abs($my) -ge 0.5) { $moving++ }
        $v = $c1.L[$k]
        $px = [int][Math]::Round($x - $mx); $py = [int][Math]::Round($y + $my)
        if ($px -lt 0 -or $py -lt 0 -or $px -ge $w -or $py -ge $h) { $eMv += 64 } else { $eMv += [Math]::Abs($v - $c0.L[$py * $w + $px]) }
        $eZero += [Math]::Abs($v - $c0.L[$k])
        $n++
    }
}
if ($n -eq 0) { "no textured pixels in the region"; return }
"region {0},{1} {2}x{3}: textured samples {4}, with motion {5:P0}; luma error with motion vectors {6:N2}, with zero motion {7:N2}, ratio {8:N2}" -f $RX, $RY, $RW, $RH, $n, ($moving / $n), ($eMv / $n), ($eZero / $n), ($eMv / $eZero)
