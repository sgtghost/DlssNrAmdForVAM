param([string]$Dir)
# Motion-vector convention check on two consecutive dumped frames (frame0 = previous, frame1 = current).
# For each candidate sign (sx, sy): prev = p + (sx*mvx*w, sy*mvy*h) in top-down pixels; the right
# convention reprojects frame1 onto frame0 with the lowest colour error.
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
$raw = [IO.File]::ReadAllBytes("$Dir\frame1\input_motion.raw")
$mv = New-Object float[] ($raw.Length / 4)
[Buffer]::BlockCopy($raw, 0, $mv, 0, $raw.Length)
$cands = @(@(1, 1), @(1, -1), @(-1, 1), @(-1, -1), @(0, 0))
$err = @(0.0, 0.0, 0.0, 0.0, 0.0); $n = 0; $sumMx = 0.0; $sumMy = 0.0; $moving = 0
for ($y = 8; $y -lt $h - 8; $y += 3) {
    for ($x = 8; $x -lt $w - 8; $x += 3) {
        $k = $y * $w + $x
        $mx = $mv[2 * $k] * $w; $my = $mv[2 * $k + 1] * $h
        if ([Math]::Abs($mx) + [Math]::Abs($my) -lt 1.0) { continue }
        # skip flat areas: matching needs texture
        $g = [Math]::Abs($c1.L[$k + 1] - $c1.L[$k - 1]) + [Math]::Abs($c1.L[$k + $w] - $c1.L[$k - $w])
        if ($g -lt 6) { continue }
        $moving++; $sumMx += $mx; $sumMy += $my
        $v = $c1.L[$k]
        for ($c = 0; $c -lt 5; $c++) {
            $px = [int][Math]::Round($x + $cands[$c][0] * $mx); $py = [int][Math]::Round($y + $cands[$c][1] * $my)
            if ($px -lt 0 -or $py -lt 0 -or $px -ge $w -or $py -ge $h) { $err[$c] += 64; continue }
            $err[$c] += [Math]::Abs($v - $c0.L[$py * $w + $px])
        }
        $n++
    }
}
if ($n -eq 0) { "no textured pixels with motion >= 1 px: motion vectors are empty or the camera was still"; return }
"textured moving samples: $n; mean motion (pixels, as stored * size): x {0:N2}, y {1:N2}" -f ($sumMx / $moving), ($sumMy / $moving)
for ($c = 0; $c -lt 5; $c++) { "  prev = p + ({0,2}*mvx*w, {1,2}*mvy*h): mean luma error {2:N2}" -f $cands[$c][0], $cands[$c][1], ($err[$c] / $n) }
