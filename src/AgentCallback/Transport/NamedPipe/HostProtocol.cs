using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCallback.Transport.NamedPipe;

public static class HostProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;
}

public sealed record HostRequest(
    int ProtocolVersion,
    string Operation,
    JsonElement Payload);

public sealed record HostResponse(
    int ProtocolVersion,
    bool Ok,
    JsonElement? Payload,
    string? Error)
{
    public static HostResponse Success<T>(T value) => new(
        HostProtocol.Version,
        true,
        JsonSerializer.SerializeToElement(value, HostJson.Options),
        null);

    public static HostResponse Failure(string error) => new(
        HostProtocol.Version,
        false,
        null,
        error);
}

public sealed record CallbackIdRequest(string CallbackId);

public sealed record CallbackListRequest(string? State, int Limit = 100);

public sealed record ProviderStatusRequest(string Provider = "codex");

public sealed record HostStatus(
    bool Running,
    int ProcessId,
    int ProtocolVersion,
    DateTimeOffset StartedUtc,
    string DatabasePath);

internal static class HostJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
