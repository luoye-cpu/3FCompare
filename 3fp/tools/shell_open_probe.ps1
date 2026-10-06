param(
    [Parameter(Mandatory=$true)][string]$File,
    [int]$WaitMs = 9000
)
# shell_open_probe.ps1 —— 走**双击同一条解析路径**（ShellExecute）打开一个文件，
# 然后报告到底是哪个进程接手、它的窗口标题是什么。
# 为什么不用 exe + 参数：那绕过了关联解析，证明不了"双击"（docs/06 早已证命令行走通、双击不通）。
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'WinUi.cs') `
     -ReferencedAssemblies 'System.Drawing', 'System.Windows.Forms'

$before = @{}
Get-Process | ForEach-Object { $before["$($_.Id):$($_.ProcessName)"] = $true }
Write-Output "打开 $File"
Start-Process -FilePath $File          # UseShellExecute 默认开启 ⇒ 与资源管理器双击等价

$目标名 = [IO.Path]::GetFileNameWithoutExtension($File)
$截止 = (Get-Date).AddMilliseconds($WaitMs)
$命中 = $null
while ((Get-Date) -lt $截止) {
    Start-Sleep -Milliseconds 400
    foreach ($p in Get-Process) {
        $key = "$($p.Id):$($p.ProcessName)"
        if ($before.ContainsKey($key)) { continue }
        if ($p.MainWindowHandle -ne 0) {
            $标题 = ([WinUi]::WindowTitle($p.MainWindowHandle))
            Write-Output ("新进程 pid={0} name={1} 主窗口标题='{2}'" -f $p.Id, $p.ProcessName, $标题)
            $before[$key] = $true
        }
    }
    # 已有进程也可能只是新开窗口：扫一遍所有 FFF.Player 进程的窗口标题
    foreach ($p in (Get-Process -Name 'FFF.Player' -ErrorAction SilentlyContinue)) {
        foreach ($t in [WinUi]::TitlesOfProcess($p.Id)) {
            if ($t -like "*$目标名*") { $命中 = "pid=$($p.Id) 标题=$t"; break }
        }
        if ($命中) { break }
    }
    if ($命中) { break }
}
if ($命中) { Write-Output "结果：FFF.Player 接手 —— $命中" }
else       { Write-Output "结果：FFF.Player 未接手（该扩展名的有效默认程序不是我们）" }
