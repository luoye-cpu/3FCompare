using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using _3FCompare.Core.Settings;
using Microsoft.Win32;

namespace _3FCompare.Services;

/// <summary>默认输出端点的蓝牙 profile 探测（Windows 用户态）。
///
/// <para><b>能拿到什么</b>：MMDevices 音频端点 → ContainerID → BTHENUM 服务节点
/// → <c>Service</c> / <c>HardwareID</c> 服务 UUID。由此可判定当前握手的是
/// A2DP / HFP / HSP / LE Audio，并取设备型号与 MAC。</para>
///
/// <para><b>拿不到什么</b>：具体 codec（SBC/AAC/aptX/LDAC）与 AVDTP Delay Reporting。
/// 前者只能走内核 A2DP Sideband SIOP（<c>IOCTL_SBAUD_GET_SIOP</c> 仅内核态），
/// 后者 Windows 根本不消费。因此本探测只输出 profile 级区间，不伪造 codec。</para>
///
/// <para><b>端点选择</b>：只探测默认 <c>eRender/eConsole</c> 端点。3FCompare 没有
/// 应用内音频端点选择器，播放走系统默认输出；若将来加了选择器，应改为按会话
/// 端点探测。</para>
///
/// <para><b>移植来源</b>：<c>tools/探测蓝牙音频延迟.ps1</c> 已验证的 ContainerID
/// 匹配算法（blob 取尾部 16 字节）与 FriendlyName/MAC 解析。</para></summary>
public static partial class BluetoothAudioDetector
{
    private const string RenderRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";
    private const string BthEnumRoot = @"BTHENUM";
    private const string BthLeRoot = @"BTHLE";

    // MMDevices 端点属性键
    private const string PkeyEndpointFriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PkeyDeviceDesc = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string PkeyContainerId = "{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2";
    private const string PkeyDeviceParentLegacy = "{b3f8fa53-0004-438e-9003-51a46e139bfc},39";
    private const string PkeyDeviceParent = "{4340a6c5-93fa-4706-972c-7b648008a5a7},8";

    private static readonly Regex ServiceGuidRx =
        new(@"\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}",
            RegexOptions.Compiled);
    private static readonly Regex MacRx =
        new(@"&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);
    private static readonly Regex FriendlyModelRx =
        new(@"\(([^()]+)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex LocalizedPrefixRx =
        new(@"@[^;]*;", RegexOptions.Compiled);

    private const string GuidA2dp = "0000110B-0000-1000-8000-00805F9B34FB";
    private const string GuidHfp = "0000111E-0000-1000-8000-00805F9B34FB";
    private const string GuidHfpAg = "00001112-0000-1000-8000-00805F9B34FB";
    private const string GuidHsp = "00001108-0000-1000-8000-00805F9B34FB";

    /// <summary>探测当前默认渲染端点。任何失败都返回非蓝牙记录并带 Detail，
    /// 绝不抛异常打断设置页/播放。</summary>
    public static BluetoothAudioInfo DetectDefaultOutput()
    {
        try
        {
            var endpointId = TryGetDefaultEndpointId();
            if (string.IsNullOrWhiteSpace(endpointId))
                return Fail("无法读取系统默认输出端点（IMMDeviceEnumerator 不可用）");

            return DetectEndpoint(endpointId);
        }
        catch (Exception ex)
        {
            return Fail($"蓝牙探测失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>探测指定端点 ID（测试/未来多端点选择器的入口）。</summary>
    public static BluetoothAudioInfo DetectEndpoint(string endpointId)
    {
        var endpoint = ReadEndpoint(endpointId);
        if (endpoint is null)
            return Fail("端点不在 MMDevices 注册表中", endpointId);

        // 1) ContainerID 精确匹配 BTHENUM/BTHLE 服务节点（脚本同款算法）。
        var node = FindNodeByContainer(endpoint.ContainerId);
        if (node is not null)
            return Build(endpoint, node);

        // 2) 某些系统/LE Audio 端点没有可用 ContainerID，退到父设备实例路径。
        var parent = endpoint.ParentInstance;
        if (!string.IsNullOrWhiteSpace(parent) &&
            (parent.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
             parent.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)))
        {
            node = ReadNode(parent);
            if (node is not null)
                return Build(endpoint, node);
        }

        return new BluetoothAudioInfo
        {
            IsBluetooth = false,
            IsConnected = endpoint.State == 1,
            Profile = BluetoothAudioProfile.None,
            EndpointId = endpointId,
            EndpointName = endpoint.FriendlyName,
            EndpointState = endpoint.State,
            Detail = endpoint.ContainerId.Length > 0
                ? "端点容器未匹配到蓝牙服务节点（可能不是蓝牙设备）"
                : "端点没有 ContainerID（无法判定是否为蓝牙）",
        };
    }

    private static BluetoothAudioInfo Fail(string detail, string endpointId = "")
        => new()
        {
            IsBluetooth = false,
            IsConnected = false,
            Profile = BluetoothAudioProfile.None,
            EndpointId = endpointId,
            Detail = detail,
        };

    private static BluetoothAudioInfo Build(EndpointRecord endpoint, BluetoothNode node)
    {
        var profile = MapProfile(node.Service, node.HardwareId, node.InstancePath);
        var (low, high) = BluetoothLatencyPolicy.RangeFor(profile);
        var model = FriendlyModel(node.FriendlyName);
        if (string.IsNullOrWhiteSpace(model))
            model = endpoint.DeviceDesc;
        if (string.IsNullOrWhiteSpace(model))
            model = endpoint.FriendlyName;

        var stateLabel = endpoint.State switch
        {
            1 => "在位",
            2 => "已禁用",
            4 => "未连接",
            8 => "未插入",
            _ => "未知",
        };

        return new BluetoothAudioInfo
        {
            IsBluetooth = true,
            IsConnected = endpoint.State == 1,
            Profile = profile,
            ProfileName = ProfileName(profile, node.Service),
            LatencyLowMs = low,
            LatencyHighMs = high,
            DeviceName = model,
            EndpointName = endpoint.FriendlyName,
            Mac = node.Mac,
            EndpointId = endpoint.EndpointId,
            EndpointState = endpoint.State,
            Detail = $"{stateLabel}；Service={node.Service}",
        };
    }

    private static string ProfileName(BluetoothAudioProfile profile, string service)
    {
        if (profile == BluetoothAudioProfile.LeAudio) return "LE-Audio";
        if (profile == BluetoothAudioProfile.A2dp) return "A2DP";
        if (profile == BluetoothAudioProfile.Hfp)
            return service.Contains("HSP", StringComparison.OrdinalIgnoreCase) ? "HSP" : "HFP";
        return "Bluetooth";
    }

    private static BluetoothAudioProfile MapProfile(string service, string hardwareId, string instancePath)
    {
        if (service.Contains("BthLEAudio", StringComparison.OrdinalIgnoreCase) ||
            instancePath.Contains("BTHLE", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioProfile.LeAudio;

        var guid = ExtractServiceGuid(hardwareId) ?? ExtractServiceGuid(instancePath);
        if (guid is not null)
        {
            var upper = guid.ToUpperInvariant();
            if (upper == GuidA2dp) return BluetoothAudioProfile.A2dp;
            if (upper is GuidHfp or GuidHfpAg or GuidHsp) return BluetoothAudioProfile.Hfp;
        }

        if (service.Contains("BthA2dp", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioProfile.A2dp;
        if (service.Contains("BthHF", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("BthHsp", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioProfile.Hfp;

        return BluetoothAudioProfile.Unknown;
    }

    private static string? ExtractServiceGuid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ServiceGuidRx.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── MMDevices 端点 ───────────────────────────────────────────────

    private sealed record EndpointRecord(
        string EndpointId,
        string ContainerId,
        string FriendlyName,
        string DeviceDesc,
        int State,
        string ParentInstance);

    private static EndpointRecord? ReadEndpoint(string endpointId)
    {
        var guid = LastGuid(endpointId);
        if (guid is null) return null;

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var keyPath = $@"{RenderRoot}\{guid}";
        using var epKey = baseKey.OpenSubKey(keyPath);
        if (epKey is null) return null;

        var state = ReadInt(epKey.GetValue("DeviceState")) ?? 0;
        using var props = epKey.OpenSubKey("Properties");
        if (props is null) return null;

        var container = ReadContainerId(props.GetValue(PkeyContainerId));
        var name = ReadString(props.GetValue(PkeyEndpointFriendlyName)) ?? "";
        var desc = ReadString(props.GetValue(PkeyDeviceDesc)) ?? "";
        var parent = ReadString(props.GetValue(PkeyDeviceParent))
                     ?? ReadString(props.GetValue(PkeyDeviceParentLegacy))
                     ?? "";

        return new EndpointRecord(endpointId, container, name, desc, state, parent);
    }

    /// <summary>ContainerId 属性是"8 字节头 + 16 字节 GUID"的 blob；取尾部 16 字节。</summary>
    private static string ReadContainerId(object? raw)
    {
        if (raw is byte[] bytes && bytes.Length >= 16)
        {
            var tail = new byte[16];
            Array.Copy(bytes, bytes.Length - 16, tail, 0, 16);
            return new Guid(tail).ToString("B").ToUpperInvariant();
        }
        // 少数系统直接给 REG_SZ GUID
        return ReadString(raw)?.Trim().ToUpperInvariant() ?? "";
    }

    private static string? LastGuid(string text)
    {
        var start = text.LastIndexOf('{');
        if (start < 0) return null;
        var end = text.IndexOf('}', start);
        if (end <= start) return null;
        var guid = text.Substring(start, end - start + 1);
        return Guid.TryParse(guid, out var parsed)
            ? parsed.ToString("B").ToUpperInvariant()
            : null;
    }

    // ── BTHENUM / BTHLE 服务节点 ─────────────────────────────────────

    private sealed record BluetoothNode(
        string ContainerId,
        string Service,
        string HardwareId,
        string FriendlyName,
        string InstancePath,
        string Mac);

    private static BluetoothNode? FindNodeByContainer(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return null;

        BluetoothNode? best = null;
        foreach (var root in new[] { BthEnumRoot, BthLeRoot })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var rootKey = baseKey.OpenSubKey($@"{EnumRoot}\{root}");
            if (rootKey is null) continue;

            foreach (var topName in rootKey.GetSubKeyNames())
            {
                using var topKey = rootKey.OpenSubKey(topName);
                if (topKey is null) continue;

                // 有些机型（尤其 BTHLE）把 Service 直接放在顶层节点。
                var topNode = ReadNodeFromKey(topKey, $@"{root}\{topName}");
                if (topNode is not null && ContainerEquals(topNode.ContainerId, containerId))
                    best = Prefer(best, topNode);

                foreach (var childName in topKey.GetSubKeyNames())
                {
                    using var childKey = topKey.OpenSubKey(childName);
                    if (childKey is null) continue;
                    var node = ReadNodeFromKey(childKey, $@"{root}\{topName}\{childName}");
                    if (node is not null && ContainerEquals(node.ContainerId, containerId))
                        best = Prefer(best, node);
                }
            }
        }
        return best;
    }

    private static BluetoothNode? Prefer(BluetoothNode? current, BluetoothNode candidate)
    {
        if (current is null) return candidate;
        // 同一容器可能同时挂了 A2DP 与 HFP 服务节点；媒体端点的默认选择优先 A2DP。
        if (current.Service.Contains("BthA2dp", StringComparison.OrdinalIgnoreCase)) return current;
        if (candidate.Service.Contains("BthA2dp", StringComparison.OrdinalIgnoreCase)) return candidate;
        if (current.Service.Contains("BthLEAudio", StringComparison.OrdinalIgnoreCase)) return current;
        if (candidate.Service.Contains("BthLEAudio", StringComparison.OrdinalIgnoreCase)) return candidate;
        return current;
    }

    private static BluetoothNode? ReadNode(string instancePath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey($@"{EnumRoot}\{instancePath}");
        return key is null ? null : ReadNodeFromKey(key, instancePath);
    }

    private static BluetoothNode? ReadNodeFromKey(RegistryKey key, string instancePath)
    {
        var service = ReadString(key.GetValue("Service")) ?? "";
        if (service.Length == 0) return null;
        if (!(service.Contains("BthA2dp", StringComparison.OrdinalIgnoreCase) ||
              service.Contains("BthHF", StringComparison.OrdinalIgnoreCase) ||
              service.Contains("BthHsp", StringComparison.OrdinalIgnoreCase) ||
              service.Contains("BthLEAudio", StringComparison.OrdinalIgnoreCase)))
            return null;

        var hardwareId = FirstString(key.GetValue("HardwareID")) ?? "";
        var friendly = ReadString(key.GetValue("FriendlyName")) ?? "";
        var container = ReadString(key.GetValue("ContainerID"))?.Trim().ToUpperInvariant() ?? "";
        var mac = FormatMac(instancePath[(instancePath.LastIndexOf('\\') + 1)..]);

        return new BluetoothNode(container, service, hardwareId, friendly, instancePath, mac);
    }

    private static bool ContainerEquals(string a, string b)
        => a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FormatMac(string leaf)
    {
        var m = MacRx.Match(leaf);
        if (!m.Success) return "";
        var hex = m.Groups[1].Value.ToUpperInvariant();
        var parts = new string[6];
        for (int i = 0; i < 6; i++) parts[i] = hex.Substring(i * 2, 2);
        return string.Join(':', parts);
    }

    private static string FriendlyModel(string? localized)
    {
        if (string.IsNullOrWhiteSpace(localized)) return "";
        var m = FriendlyModelRx.Match(localized);
        if (m.Success) return m.Groups[1].Value.Trim();
        return LocalizedPrefixRx.Replace(localized, "").Trim();
    }

    // ── 基础读取 ─────────────────────────────────────────────────────

    private static string? ReadString(object? value)
    {
        if (value is string s) return s;
        if (value is string[] arr && arr.Length > 0) return arr[0];
        return null;
    }

    private static string? FirstString(object? value)
    {
        if (value is string s) return s;
        if (value is string[] arr && arr.Length > 0) return arr[0];
        return null;
    }

    private static int? ReadInt(object? value)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is string s && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    // ── COM：默认渲染端点（裸 vtable，不用 [ComImport]） ─────────────
    //
    // 与 DxgiOutputInfo 同一理由：.NET 11 的 ComImport 内置封送在本机对 DXGI
    // 会返回 INVALID_CALL（vtable 槽位与签名都对也不工作），裸函数指针正常。
    // 这里只调用 IMMDeviceEnumerator::GetDefaultAudioEndpoint 与 IMMDevice::GetId，
    // 槽位固定（IUnknown 0..2；IMMDeviceEnumerator 3..7；IMMDevice 3..6）。

    private const int ERender = 0;
    private const int EConsole = 0;
    private const uint ClsCtxInprocServer = 0x1;
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint clsCtx, ref Guid iid, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDefaultAudioEndpointFn(
        IntPtr self, int dataFlow, int role, out IntPtr endpoint);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetIdFn(IntPtr self, out IntPtr id);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseFn(IntPtr self);

    private static IntPtr VtableSlot(IntPtr instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void ReleaseCom(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        try
        {
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(VtableSlot(instance, 2));
            release(instance);
        }
        catch { /* 释放失败不能盖住探测结果 */ }
    }

    private static string? TryGetDefaultEndpointId()
    {
        IntPtr enumerator = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iid = IidMmDeviceEnumerator;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero,
                ClsCtxInprocServer, ref iid, out enumerator);
            if (hr < 0 || enumerator == IntPtr.Zero) return null;

            var getDefault = Marshal.GetDelegateForFunctionPointer<GetDefaultAudioEndpointFn>(
                VtableSlot(enumerator, 4));
            hr = getDefault(enumerator, ERender, EConsole, out device);
            if (hr < 0 || device == IntPtr.Zero) return null;

            var getId = Marshal.GetDelegateForFunctionPointer<GetIdFn>(VtableSlot(device, 5));
            hr = getId(device, out var idPtr);
            if (hr < 0 || idPtr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(idPtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(idPtr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }
}
