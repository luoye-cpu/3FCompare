# 扫描主窗口标题栏区域的图标像素分布，定位各按钮
param(
    [int]$WinLeft = 851,
    [int]$WinTop = 400
)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(857, 100)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($WinLeft, $WinTop, 0, 0, $bmp.Size)
$g.Dispose()

# 按钮在客户区顶部 40px；标题栏约 30px 高。扫描 y=435..475（屏幕坐标 = WinTop+35..75）
$colCount = @{}
for ($y = 35; $y -lt 75; $y += 2) {
    for ($x = 700; $x -lt 857; $x += 2) {
        $p = $bmp.GetPixel($x, $y)
        # 图标像素：接近白色且饱和度低
        $max = [Math]::Max($p.R, [Math]::Max($p.G, $p.B))
        $min = [Math]::Min($p.R, [Math]::Min($p.G, $p.B))
        if ($max -gt 200 -and ($max - $min) -lt 60) {
            if (-not $colCount.ContainsKey($x)) { $colCount[$x] = 0 }
            $colCount[$x]++
        }
    }
}
# 按连续区间输出图标列范围
$xs = $colCount.Keys | Sort-Object
Write-Host "图标亮像素列（x 相对窗口左缘 + 851 = 屏幕坐标）:"
$prev = $null; $start = $null
foreach ($x in $xs) {
    if ($prev -eq $null) { $start = $x; $prev = $x; continue }
    if ($x - $prev -gt 4) {
        Write-Host ("  图标区: x={0}-{1} (屏幕 {2}-{3}) 峰值列数={4}" -f $start, $prev, ($start+851), ($prev+851), ($colCount[$start..$prev] | Measure-Object -Maximum).Maximum)
        $start = $x
    }
    $prev = $x
}
if ($start -ne $null) {
    Write-Host ("  图标区: x={0}-{1} (屏幕 {2}-{3})" -f $start, $prev, ($start+851), ($prev+851))
}
$bmp.Dispose()
