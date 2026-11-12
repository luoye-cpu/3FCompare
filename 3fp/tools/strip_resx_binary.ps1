# 清理 .resx 的二进制/模板死条目（2026-11-12）。
# 背景：MSBuild 18.9+ GenerateResource 对带 mimetype/typed 条目的 resx 做 MOTW 信任
# 检查；本机 IE 安全组件被精简，本地路径也被判受限区 → 第一方 resx 全部 MSB3821。
# 这些二进制条目（Color1/Bitmap1/Icon1/$this.Icon）经盘点只有 Form1 的 $this.Icon 被
# 使用（改为运行时从 ICO.ico 加载，见 Form1.Designer.vb），其余全是模板死条目。
# 移除后 resx 只剩字符串/布局元数据，不再触发检查，正常构建链恢复。
# 用法：powershell -File tools\strip_resx_binary.ps1   （幂等）
$ErrorActionPreference = 'Stop'
$player = Join-Path (Split-Path $PSScriptRoot -Parent) 'kernel\FFF.Player'
foreach ($f in Get-ChildItem $player -Filter '*.resx') {
    $text = [IO.File]::ReadAllText($f.FullName)
    $orig = $text
    # 带 mimetype 的 data 块（BinaryFormatter/bytearray 序列化对象）整块移除
    $text = [regex]::Replace($text, '(?s)[ \t]*<data\b[^>]*mimetype[^>]*>.*?</data>\r?\n', '')
    # 模板死条目：Color1（typed Color）与 Name1（长字符串示例）
    $text = [regex]::Replace($text, '(?s)[ \t]*<data name="Color1"[^>]*>.*?</data>\r?\n', '')
    $text = [regex]::Replace($text, '(?s)[ \t]*<data name="Name1"[^>]*>.*?</data>\r?\n', '')
    # typed metadata（TrayLocation/TrayHeight 设计器托盘记录）：wave 18.9 下同样判危险，
    # 且对运行时零影响（组件本身照常创建，托盘位置回到默认）
    $text = [regex]::Replace($text, '(?s)[ \t]*<metadata\b[^>]*type="[^"]*"[^>]*>.*?</metadata>\r?\n', '')
    if ($text -ne $orig) {
        [IO.File]::WriteAllText($f.FullName, $text)
        Write-Host "stripped $($f.Name)"
    } else {
        Write-Host "clean    $($f.Name)"
    }
}
