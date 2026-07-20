using System.IO.Pipes;
using System.Text.Json;

namespace AgentCallback.Providers.Codex.DesktopIpc;

internal sealed class CodexDesktopIpcInvoker
{
    private const string PipeName = "codex-ipc";
    private const int MaxMessagesPerRequest = 256;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(5);

    public async Task<string> ProbeAsync(CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(cancellationToken);
        return session.ClientId;
    }

    public async Task<JsonElement> InvokeAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(cancellationToken);
        var requestId = Guid.NewGuid().ToString();
        var request = new Dictionary<string, object?>
        {
            ["type"] = "request",
            ["requestId"] = requestId,
            ["sourceClientId"] = session.ClientId,
            ["version"] = MethodVersion(method),
            ["method"] = method,
            ["params"] = parameters,
            ["timeoutMs"] = (int)timeout.TotalMilliseconds
        };

        try
        {
            await CodexDesktopFrameCodec.WriteAsync(session.Stream, request, cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            throw new CodexDesktopIpcException(
                "Codex Desktop IPC request may have been partially written.",
                deliveryUncertain: true,
                exception);
        }

        try
        {
            return await WaitForResponseAsync(
                session.Stream,
                requestId,
                timeout,
                cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            throw new CodexDesktopIpcException(
                $"Codex Desktop IPC response was not observed: {exception.Message}",
                deliveryUncertain: true,
                exception);
        }
    }

    private static async Task<CodexDesktopIpcSession> OpenSessionAsync(
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectTimeout.Token);
            var clientId = await InitializeAsync(pipe, cancellationToken);
            return new CodexDesktopIpcSession(pipe, clientId);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            await pipe.DisposeAsync();
            throw new CodexDesktopIpcException(
                $"Codex Desktop IPC is unavailable: {exception.Message}",
                deliveryUncertain: false,
                exception);
        }
    }

    private static async Task<string> InitializeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();
        var request = new Dictionary<string, object?>
        {
            ["type"] = "request",
            ["requestId"] = requestId,
            ["sourceClientId"] = "initializing-client",
            ["version"] = 0,
            ["method"] = "initialize",
            ["params"] = new Dictionary<string, object?>
            {
                ["clientType"] = "agent-callback"
            }
        };
        await CodexDesktopFrameCodec.WriteAsync(stream, request, cancellationToken);
        var response = await WaitForResponseAsync(
            stream,
            requestId,
            InitializeTimeout,
            cancellationToken);
        if (!string.Equals(
                JsonOptions.GetString(response, "resultType"),
                "success",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                JsonOptions.GetString(response, "error") ?? "Codex Desktop IPC initialize failed.");
        }

        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("clientId", out var clientId) ||
            clientId.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Codex Desktop IPC initialize returned no client id.");
        }

        return clientId.GetString() ?? throw new InvalidOperationException(
            "Codex Desktop IPC initialize returned an empty client id.");
    }

    private static async Task<JsonElement> WaitForResponseAsync(
        Stream stream,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var messageCount = 0;
        while (messageCount < MaxMessagesPerRequest)
        {
            messageCount++;
            var message = await CodexDesktopFrameCodec.ReadAsync(stream, timeoutSource.Token);
            var type = JsonOptions.GetString(message, "type");
            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    JsonOptions.GetString(message, "requestId"),
                    requestId,
                    StringComparison.Ordinal))
            {
                return message;
            }

            if (string.Equals(type, "client-discovery-request", StringComparison.OrdinalIgnoreCase))
            {
                await WriteCannotHandleDiscoveryAsync(stream, message, timeoutSource.Token);
            }
        }

        throw new InvalidDataException(
            $"Codex Desktop IPC exceeded {MaxMessagesPerRequest} messages without a matching response.");
    }

    private static async Task WriteCannotHandleDiscoveryAsync(
        Stream stream,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        var requestId = JsonOptions.GetString(request, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        await CodexDesktopFrameCodec.WriteAsync(
            stream,
            new Dictionary<string, object?>
            {
                ["type"] = "client-discovery-response",
                ["requestId"] = requestId,
                ["response"] = new Dictionary<string, object?>
                {
                    ["canHandle"] = false
                }
            },
            cancellationToken);
    }

    private static int MethodVersion(string method) => method switch
    {
        "thread-follower-start-turn" => 1,
        "thread-follower-steer-turn" => 1,
        _ => 0
    };

    private static bool IsTransportFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is
            IOException or
            TimeoutException or
            OperationCanceledException or
            InvalidOperationException or
            InvalidDataException or
            JsonException;
}
internal sealed class CodexDesktopIpcSession : IAsyncDisposable
{
    public CodexDesktopIpcSession(Stream stream, string clientId)
    {
        Stream = stream;
        ClientId = clientId;
    }

    public Stream Stream { get; }
    public string ClientId { get; }
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

internal sealed class CodexDesktopIpcException : IOException
{
    public CodexDesktopIpcException(
        string message,
        bool deliveryUncertain,
        Exception innerException)
        : base(message, innerException)
    {
        DeliveryUncertain = deliveryUncertain;
    }

    public bool DeliveryUncertain { get; }
}
