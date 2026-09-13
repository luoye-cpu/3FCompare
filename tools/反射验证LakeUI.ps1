# 反射验证 LakeUI.UltraDetailListView 的 DragSelectZoneWidth 属性
Add-Type -AssemblyName System.Windows.Forms
# 从 FFF.Player 输出目录加载（依赖齐全）
$appDir = "C:\PLAN\3FCompare\third_party\fff_project\FFF.Player\bin\Release\net10.0-windows10.0.26100.0"
Get-ChildItem $appDir -Filter "*.dll" | ForEach-Object {
    try { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $appDir "LakeUI.dll"))
$type = $asm.GetType("LakeUI.UltraDetailListView", $false)
if ($type -eq $null) {
    Write-Host "❌ 未找到 LakeUI.UltraDetailListView 类型"
    Write-Host "LakeUI 程序集全部公开类型："
    $asm.GetExportedTypes() | Where-Object { $_.Name -like "*List*" } | ForEach-Object { Write-Host "  $($_.FullName)" }
    exit 1
}
Write-Host "类型: $($type.FullName)"
foreach ($propName in @("DragSelectZoneWidth", "AllowDragReorder", "MultiSelect", "ClientSize", "DeviceDpi", "Columns", "Padding", "BorderSize", "BorderRadius")) {
    $prop = $type.GetProperty($propName)
    if ($prop -eq $null) {
        Write-Host "❌ 属性不存在: $propName"
    } else {
        Write-Host "✅ 属性存在: $propName ($($prop.PropertyType.Name))"
    }
}
