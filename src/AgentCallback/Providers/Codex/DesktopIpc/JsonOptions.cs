using System.Text.Json;

namespace AgentCallback.Providers.Codex.DesktopIpc;

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web);

    public static string? GetString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
