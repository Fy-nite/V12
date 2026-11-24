using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12.MCP;

/// <summary>Shared JSON settings for requests to the probe and tool output.</summary>
internal static class Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    public static string Pretty(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? "" : JsonSerializer.Serialize(element, Indented);

    public static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long Long(JsonElement element, string property, long fallback = 0)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : fallback;

    public static double Number(JsonElement element, string property, double fallback = 0)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;
}
