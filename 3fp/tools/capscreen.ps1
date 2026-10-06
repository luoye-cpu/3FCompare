param(
    [Parameter(Mandatory=$true)][int]$X, [Parameter(Mandatory=$true)][int]$Y,
    [Parameter(Mandatory=$true)][int]$W, [Parameter(Mandatory=$true)][int]$H,
    [Parameter(Mandatory=$true)][string]$Out
)
# capscreen.ps1 —— 按**屏幕矩形**抓屏（不依赖窗口标题；capwin 用 PrintWindow 按标题抓，
# 标题被应用改掉时就用这个兜底。代价：会把遮挡它的窗口一起拍进来）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out ${W}x${H} @ $X,$Y"
