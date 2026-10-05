param([string]$A, [string]$B, [string]$Out)
# Mean absolute difference of two images (0-255), share of pixels differing by more than 8, and an x8 amplified diff image.
Add-Type -AssemblyName System.Drawing
$ia = [Drawing.Bitmap]::FromFile($A); $ib = [Drawing.Bitmap]::FromFile($B)
$w = [Math]::Min($ia.Width, $ib.Width); $h = [Math]::Min($ia.Height, $ib.Height)
$rect = New-Object Drawing.Rectangle 0, 0, $w, $h
$fmt = [Drawing.Imaging.PixelFormat]::Format24bppRgb
$da = $ia.LockBits($rect, 'ReadOnly', $fmt); $db = $ib.LockBits($rect, 'ReadOnly', $fmt)
$na = New-Object byte[] ($da.Stride * $h); $nb = New-Object byte[] ($db.Stride * $h)
[Runtime.InteropServices.Marshal]::Copy($da.Scan0, $na, 0, $na.Length); [Runtime.InteropServices.Marshal]::Copy($db.Scan0, $nb, 0, $nb.Length)
$ia.UnlockBits($da); $ib.UnlockBits($db); $ia.Dispose(); $ib.Dispose()
$o = New-Object Drawing.Bitmap $w, $h, $fmt
$dout = $o.LockBits($rect, 'WriteOnly', $fmt)
$no = New-Object byte[] ($dout.Stride * $h)
$sum = 0.0; $big = 0; $n = $w * $h
for ($y = 0; $y -lt $h; $y++) {
    $ra = $y * $da.Stride
    for ($x = 0; $x -lt $w; $x++) {
        $i = $ra + 3 * $x
        $d = ([Math]::Abs([int]$na[$i] - $nb[$i]) + [Math]::Abs([int]$na[$i + 1] - $nb[$i + 1]) + [Math]::Abs([int]$na[$i + 2] - $nb[$i + 2])) / 3.0
        $sum += $d
        if ($d -gt 8) { $big++ }
        $v = [byte][Math]::Min(255, [int]($d * 8))
        $j = $y * $dout.Stride + 3 * $x
        $no[$j] = $v; $no[$j + 1] = $v; $no[$j + 2] = $v
    }
}
[Runtime.InteropServices.Marshal]::Copy($no, 0, $dout.Scan0, $no.Length)
$o.UnlockBits($dout); $o.Save($Out); $o.Dispose()
"{0}: mean |diff| {1:N2} / 255, pixels differing > 8: {2:P2}" -f (Split-Path $Out -Leaf), ($sum / $n), ($big / $n)
