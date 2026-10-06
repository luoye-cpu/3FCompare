param(
    [string]$Exts = '.png,.jpg,.jpeg,.jpe,.jfif,.apng,.bmp,.gif,.webp,.avif,.ico,.tiff,.tif,.heic'
)
# userchoice.ps1 —— 查 Windows 的**有效默认程序**
# 为什么单独查：3FP 写的是 HKCU\Software\Classes\<ext> 的 ProgID，
#   而 Win10/11 双击时优先看 FileExts\<ext>\UserChoice（带 Hash 保护，第三方写不进去）。
#   ⇒ "注册表写对了"和"双击真的走我们"是两件事，必须分别取证。
foreach ($e in ($Exts -split ',')) {
    $p = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$e\UserChoice"
    $classes = "HKCU:\Software\Classes\$e"
    $cls = ''
    if (Test-Path $classes) { $cls = [string](Get-Item $classes).GetValue('') }
    if (Test-Path $p) {
        $uc = (Get-ItemProperty $p).ProgId
        $match = if ($uc -like 'FFF.Player*') { '命中FFF' } elseif ($uc -eq $cls) { '与Classes一致' } else { '不一致' }
        Write-Output ("{0,-7} UserChoice={1,-28} Classes={2,-18} => {3}" -f $e, $uc, $cls, $match)
    } else {
        Write-Output ("{0,-7} UserChoice=(无)                  Classes={1,-18} => 走 Classes" -f $e, $cls)
    }
}
