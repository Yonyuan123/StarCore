using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarCore.Core.App;

/// <summary>
/// 全局 JSON 约定。
/// 统一使用 camelCase，前端拿到的字段名与此处属性名首字母小写一一对应，
/// 避免两端各自维护一套映射。
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>带缩进的序列化。用于诊断报告这类要给人看、甚至要贴到 issue 里的输出。</summary>
    public static string SerializeIndented<T>(T value) => JsonSerializer.Serialize(value, Indented);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch { return null; }
    }

    /// <summary>
    /// 解析成独立的 <see cref="JsonElement"/>。
    /// <see cref="JsonDocument"/> 释放后其 RootElement 会失效，所以必须 Clone ——
    /// 这是使用 System.Text.Json 最容易踩的坑。
    /// </summary>
    public static JsonElement DeserializeElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>把任意对象转成 <see cref="JsonElement"/>，便于把参数对象逐字段读取。</summary>
    public static JsonElement ToElement(object? value)
        => value is JsonElement el ? el : JsonSerializer.SerializeToElement(value, Options);
}
