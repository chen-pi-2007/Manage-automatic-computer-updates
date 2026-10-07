# 生成程序图标：蓝色圆角方块 + 白色环形刷新箭头 + 对勾（"更新" + "放心"）。
# 输出 src/UpdateHelper.App/Assets/app.ico（16~256 多尺寸）和 app.png（256，标题栏用）。改了设计就重跑这个脚本。
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot "..\src\UpdateHelper.App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # 圆角方块，蓝色渐变
    $r = 56 * $s; $pad = 8 * $s; $w = $size - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90)
    $path.AddArc($pad + $w - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $path.AddArc($pad + $w - 2*$r, $pad + $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x8C, 0xF0)), ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x4F, 0xC4))
    $g.FillPath($brush, $path)

    # 环形刷新箭头：一段 290 度的圆弧，末端一个三角箭头
    $white = [System.Drawing.Color]::White
    $penW = [Math]::Max(1.6, 20 * $s)
    $pen = New-Object System.Drawing.Pen $white, $penW
    $pen.StartCap = 'Round'
    $cx = 128 * $s; $cy = 128 * $s; $rad = 70 * $s
    $g.DrawArc($pen, $cx - $rad, $cy - $rad, 2*$rad, 2*$rad, -60, 290)
    # 箭头在弧的终点（-60+290=230 度），沿顺时针切线方向
    $end = (230) * [Math]::PI / 180
    $ex = $cx + $rad * [Math]::Cos($end); $ey = $cy + $rad * [Math]::Sin($end)
    $tx = -[Math]::Sin($end); $ty = [Math]::Cos($end)       # 顺时针切线
    $nx = [Math]::Cos($end);  $ny = [Math]::Sin($end)       # 径向
    $a = 30 * $s
    $pts = @(
        (New-Object System.Drawing.PointF ($ex + $tx*$a), ($ey + $ty*$a)),
        (New-Object System.Drawing.PointF ($ex + $nx*$a*0.85 - $tx*$a*0.2), ($ey + $ny*$a*0.85 - $ty*$a*0.2)),
        (New-Object System.Drawing.PointF ($ex - $nx*$a*0.85 - $tx*$a*0.2), ($ey - $ny*$a*0.85 - $ty*$a*0.2))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), [System.Drawing.PointF[]]$pts)

    # 中间的对勾（太小的尺寸省略，免得糊成一团）
    if ($size -ge 32) {
        $check = New-Object System.Drawing.Pen $white, ([Math]::Max(2, 18 * $s))
        $check.StartCap = 'Round'; $check.EndCap = 'Round'; $check.LineJoin = 'Round'
        $g.DrawLines($check, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF (100*$s), (130*$s)),
            (New-Object System.Drawing.PointF (121*$s), (151*$s)),
            (New-Object System.Drawing.PointF (158*$s), (110*$s))))
    }
    $g.Dispose()
    return $bmp
}

# 小尺寸存成 32 位 BMP（DIB），所有 Windows 和 .NET 版本都能读；256 存 PNG（标准做法，否则文件太大）
function To-Dib($bmp) {
    $n = $bmp.Width; $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    # BITMAPINFOHEADER：高度写两倍（颜色 + AND 掩码）
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32](2 * $n)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($n * $n * 4 + $maskRow * $n)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $n; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
    $w.Write((New-Object byte[] ($maskRow * $n)))   # 掩码全 0：透明度由 alpha 通道决定
    $w.Flush(); return , $ms.ToArray()
}

# ICO 文件：头 + 目录项 + 各尺寸图像
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($n in $sizes) {
    $bmp = Draw-Icon $n
    if ($n -eq 256) {
        $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save((Join-Path $assets "app.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else { $bytes = To-Dib $bmp }
    $bmp.Dispose(); , $bytes
}
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $d = if ($n -ge 256) { 0 } else { $n }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
[System.IO.File]::WriteAllBytes((Join-Path $assets "app.ico"), $out.ToArray())
"已生成 app.ico（$($sizes -join '/')）和 app.png"
