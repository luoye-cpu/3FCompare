#requires -Version 7
<#
.SYNOPSIS
    探测 Windows 当前蓝牙音频端点的协议类别，并给出端到端延迟估计区间。

.DESCRIPTION
    Windows 的公开 API 不提供"当前协商的蓝牙音频编解码器"，也不提供 AVDTP Delay
    Reporting 的延迟值。但"当前握手的是哪个 profile"是可判定的：

      音频端点 (SWD\MMDEVAPI\{...})
        └─ ContainerID ──> BTHENUM\<服务UUID> 设备节点
                             └─ Service = BthA2dp / BthHFEnum / BthLEAudio
                             └─ HardwareID 含服务 UUID（110B=A2DP, 111E/1112=HFP）

    本脚本按 ContainerID 把 MMDevices 音频端点与 BTHENUM 服务节点对上，
    输出协议类别 + profile 级延迟区间。延迟区间只到"类别"粒度；具体设备的
    真实值必须实测（见 docs/86 的校准一节）。

.PARAMETER Json
    输出 JSON（供应用/脚本消费），字段稳定。

.EXAMPLE
    pwsh -File .\tools\探测蓝牙音频延迟.ps1

.EXAMPLE
    pwsh -File .\tools\探测蓝牙音频延迟.ps1 -Json | ConvertFrom-Json
#>
[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$ServiceProfiles = @{
    '0000110B-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'A2DP'; Kind = '媒体音频'; Low = 150; High = 300
        Note = 'SBC/AAC 为主；aptX Classic 可到 100-200；Windows 不暴露实际 codec'
    }
    '0000110E-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'AVRCP'; Kind = '遥控'; Low = $null; High = $null
        Note = '仅控制通道，不承载音频'
    }
    '0000110C-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'AVRCP'; Kind = '遥控'; Low = $null; High = $null
        Note = '仅控制通道，不承载音频'
    }
    '0000111E-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'HFP'; Kind = '通话/语音'; Low = 100; High = 250
        Note = 'mSBC/CVSD；麦克风一开就全局切到此端点'
    }
    '00001112-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'HFP-AG'; Kind = '通话/语音'; Low = 100; High = 250
        Note = 'Audio Gateway 角色，同样不是媒体通路'
    }
    '00001108-0000-1000-8000-00805F9B34FB' = [pscustomobject]@{
        Profile = 'HSP'; Kind = '通话/语音'; Low = 100; High = 250
        Note = '旧式耳机 profile'
    }
}

function Get-EndpointContainerMap {
    $map = @{}
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
    if (-not (Test-Path -LiteralPath $root)) { return $map }

    foreach ($ep in Get-ChildItem -LiteralPath $root) {
        $props = Get-ItemProperty -LiteralPath ($ep.PSPath + '\Properties') -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }

        # MMDevices 的 ContainerId 属性是"8 字节头 + 16 字节 GUID"的 blob，
        # 不是裸 GUID；取尾部 16 字节才是容器 ID。
        $raw = $props.'{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2'
        if ($raw -isnot [byte[]] -or $raw.Length -lt 16) { continue }
        $guidBytes = [byte[]]$raw[($raw.Length - 16)..($raw.Length - 1)]

        $container = ([guid]::new($guidBytes)).ToString('B').ToUpperInvariant()
        $state = (Get-ItemProperty -LiteralPath $ep.PSPath -ErrorAction SilentlyContinue).DeviceState
        $map[$container] = [pscustomobject]@{
            EndpointId   = $ep.PSChildName
            EndpointName = $props.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
            DeviceDesc   = $props.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
            State        = $state
        }
    }
    return $map
}

function Format-Mac([string]$leaf) {
    if ($leaf -match '&([0-9A-Fa-f]{12})_') {
        $h = $Matches[1].ToUpperInvariant()
        $pairs = for ($i = 0; $i -lt 12; $i += 2) { $h.Substring($i, 2) }
        return ($pairs -join ':')
    }
    return ''
}

function Get-FriendlyModel([string]$locString) {
    # 枚举键里的 FriendlyName 是本地化字符串，形如
    #   "@btha2dp.sys,#1;%1%0\n;(耳机型号)"
    # 真正的型号在最后一个括号里。
    if ($locString -match '\(([^()]+)\)\s*$') { return $Matches[1].Trim() }
    return ($locString -replace '@[^;]*;', '').Trim()
}

$endpointByContainer = Get-EndpointContainerMap
$rows = @()

foreach ($svcKey in Get-ChildItem -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM' -ErrorAction SilentlyContinue) {
    foreach ($devKey in Get-ChildItem -LiteralPath $svcKey.PSPath -ErrorAction SilentlyContinue) {
        $p = Get-ItemProperty -LiteralPath $devKey.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $p -or [string]::IsNullOrWhiteSpace($p.Service)) { continue }
        if ($p.Service -notmatch 'BthA2dp|BthHF|BthLEAudio|BthAvrcp') { continue }

        $serviceUuid = ($p.HardwareID | Select-Object -First 1)
        $guidText = if ($serviceUuid -match '\{([0-9A-Fa-f-]{36})\}') { $Matches[1].ToUpperInvariant() } else { '' }

        if ($p.Service -match 'BthLEAudio') {
            $profile = [pscustomobject]@{
                Profile = 'LE-Audio'; Kind = 'LE Audio (LC3)'; Low = 30; High = 100
                Note = 'BAP/TMAP；需 Windows 11 22H2+ 与 LE Audio 驱动，且开关已打开'
            }
        }
        elseif ($ServiceProfiles.ContainsKey($guidText)) {
            $profile = $ServiceProfiles[$guidText]
        }
        else {
            $profile = [pscustomobject]@{
                Profile = $p.Service; Kind = '蓝牙音频'; Low = 150; High = 300
                Note = '未识别的服务；按 A2DP 级保守估计'
            }
        }

        $container = if ($p.ContainerID) { $p.ContainerID.ToString().ToUpperInvariant() } else { '' }
        $endpoint = if ($container -and $endpointByContainer.ContainsKey($container)) { $endpointByContainer[$container] } else { $null }

        $stateLabel = switch ($endpoint.State) {
            1 { '在位' }
            2 { '已禁用' }
            4 { '未连接' }
            8 { '未插入' }
            default { '未知' }
        }

        $model = Get-FriendlyModel $p.FriendlyName
        if ([string]::IsNullOrWhiteSpace($model) -and $endpoint) { $model = $endpoint.DeviceDesc }

        $rows += [pscustomobject]@{
            Device      = $model
            Endpoint    = if ($endpoint.EndpointName) { $endpoint.EndpointName } else { '' }
            Profile     = $profile.Profile
            Kind        = $profile.Kind
            Service     = $p.Service
            State       = if ($endpoint) { $stateLabel } else { '无音频端点' }
            LatencyLow  = $profile.Low
            LatencyHigh = $profile.High
            LatencyText = if ($profile.Low) { "$($profile.Low)-$($profile.High) ms" } else { '—' }
            Mac         = Format-Mac $devKey.PSChildName
            Note        = $profile.Note
        }
    }
}

$rows = $rows | Sort-Object @{ Expression = { if ($_.State -eq '在位') { 0 } else { 1 } } }, Device, Profile

if ($Json) {
    $rows | ConvertTo-Json -Depth 4
    return
}

if ($rows.Count -eq 0) {
    Write-Output '未发现蓝牙音频服务节点（设备可能从未配对，或蓝牙适配器未启用）。'
    return
}

$rows | Format-Table Device, Endpoint, Profile, Kind, State, LatencyText, Mac -AutoSize |
    Out-String -Width 160

Write-Output ''
Write-Output '说明：'
Write-Output '  * 延迟区间是 profile 级估计，含 Windows 音频栈与设备缓冲，不含本应用自身的 WASAPI 缓冲。'
Write-Output '  * Windows 不公开当前协商 codec，也不消费 AVDTP Delay Reporting；精确值需一次实测校准。'
Write-Output '  * 媒体播放应落在 A2DP；一旦有应用打开蓝牙麦克风，系统会全局切到 HFP，延迟与音质同时恶化。'
