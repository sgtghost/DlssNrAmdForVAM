param([string[]]$Images, [string[]]$Labels, [int]$X, [int]$Y, [int]$W, [int]$H, [int]$Scale = 2, [string]$Out)
# Side-by-side crops (nearest-neighbour upscaled) with labels, for visual A/B comparison.
Add-Type -AssemblyName System.Drawing
$cw = $W * $Scale; $ch = $H * $Scale
$sheet = New-Object Drawing.Bitmap ($cw * $Images.Count), ($ch + 28)
$g = [Drawing.Graphics]::FromImage($sheet)
$g.Clear([Drawing.Color]::Black)
$g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
$font = New-Object Drawing.Font 'Arial', 14
for ($i = 0; $i -lt $Images.Count; $i++) {
    $img = [Drawing.Image]::FromFile($Images[$i])
    $g.DrawImage($img, (New-Object Drawing.Rectangle ($i * $cw), 28, $cw, $ch), (New-Object Drawing.Rectangle $X, $Y, $W, $H), [Drawing.GraphicsUnit]::Pixel)
    $img.Dispose()
    $g.DrawString($Labels[$i], $font, [Drawing.Brushes]::White, ($i * $cw + 6), 4)
}
$sheet.Save($Out)
"wrote $Out ($($sheet.Width)x$($sheet.Height))"
