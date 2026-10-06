param(
    [Parameter(Mandatory = $true)][string]$Title,
    [string]$Mode = 'dump',
    [string]$Text = '',
    [int]$Index = -1,
    [int]$X = 0,
    [int]$Y = 0,
    [long]$Hwnd = 0,
    [string]$Out = '',
    [int]$W = 0,
    [int]$H = 0
)
# ui_probe.ps1 —— 实机 UI 探针/点击/抓图入口（判据不依赖焦点，详见 WinUi.cs 头注释）
#   -Mode dump                        列出全部子窗口
#   -Mode clicktext -Text '常见图片'    按 Control.Text 命中并点击
#   -Mode clickindex -Index 12         按子窗口序号点击
#   -Mode clickxy -X 1234 -Y 567       按屏幕坐标点击（自绘选项卡项无独立 HWND 时用）
#   -Mode capture -Hwnd N -Out path    按 HWND 用 PrintWindow 抓图（遮挡/焦点无关）
#                                      -Hwnd 0 时按 -Title 找
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Add-Type -Path 不继承 -AssemblyName 的引用，Bitmap/Graphics 必须显式给 -ReferencedAssemblies
Add-Type -Path (Join-Path $dir 'WinUi.cs') -ReferencedAssemblies 'System.Drawing', 'System.Windows.Forms'
switch ($Mode) {
    'dump'       { [WinUi]::Dump($Title) }
    'clicktext'  { [WinUi]::ClickByText($Title, $Text) }
    'clickindex' { [WinUi]::ClickByIndex($Title, $Index) }
    'clickxy'    { [WinUi]::ClickScreen($Title, $X, $Y) }
    'hover'      { [WinUi]::Hover($Title, $X, $Y) }
    'focus'      { [WinUi]::Focus($Title) }
    'realclick'  { [WinUi]::RealClick($Hwnd, $X, $Y) }
    'capture'    {
        if ($Out -eq '') { throw 'capture 需要 -Out' }
        $h = if ($Hwnd -ne 0) { $Hwnd } else { [WinUi]::FindWindowByTitle($Title) }
        [WinUi]::Capture($h, $Out, $W, $H)
    }
    default      { throw "未知 Mode: $Mode" }
}
