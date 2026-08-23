using System.Text.Json;
using System.Text.Json.Serialization;

namespace _3FCompare.Core.Settings;

/// <summary>JSON 序列化源生成上下文（NativeAOT 兼容：避免运行时反射 JsonSerializer）。
/// 使用示例：<c>JsonSerializer.Serialize(settings, JsonAotContext.Default.AppSettings)</c>。</summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(SessionSnapshot.SessionItem))]
public sealed partial class JsonAotContext : JsonSerializerContext;