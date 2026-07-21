using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace AgentCallback.Providers.Codex.AppServer;

[SupportedOSPlatform("linux")]
public sealed class CodexAppServerConversationTransport : ICodexConversationTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly ICodexAppServerConnectionStore _connections;

    public CodexAppServerConversationTransport(
        ICodexAppServerConnectionStore connections)
    {
        _connections = connections;
    }

    public string Name => "codex-app-server-unix-experimental";

    public async Task<CodexTransportStatus> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var connections = await _connections.ListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return new CodexTransportStatus(
                false,
                null,
                "No shared Codex app-server is registered. Launch Codex with 'agent-callback codex'.");
        }

        string? firstError = null;
        foreach (var connection in connections.Take(8))
        {
            try
            {
                await using var client = await CodexAppServerClient.ConnectAsync(
                    connection.SocketPath,
                    ConnectTimeout,
                    cancellationToken);
                await client.InitializeAsync(RequestTimeout, cancellationToken);
                return new CodexTransportStatus(true, connection.ConnectionId, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                firstError ??= exception.Message;
            }
        }

        return new CodexTransportStatus(
            false,
            connections[0].ConnectionId,
            firstError ?? "Registered Codex app-server connections are unavailable.");
    }

    public Task<CodexOperationResult> StartTurnAsync(
        string threadId,
        string message,
        string clientMessageId,
        CancellationToken cancellationToken) =>
        DeliverAsync(
            threadId,
            message,
            workingDirectory: null,
            clientMessageId,
            steer: false,
            cancellationToken);

    public Task<CodexOperationResult> SteerTurnAsync(
        string threadId,
        string message,
        string workingDirectory,
        string clientMessageId,
        CancellationToken cancellationToken) =>
        DeliverAsync(
            threadId,
            message,
            workingDirectory,
            clientMessageId,
            steer: true,
            cancellationToken);

    private async Task<CodexOperationResult> DeliverAsync(
        string target,
        string message,
        string? workingDirectory,
        string clientMessageId,
        bool steer,
        CancellationToken cancellationToken)
    {
        if (!CodexAppServerTarget.TryParse(target, out var connectionId, out var threadId))
        {
            return Failed(
                clientMessageId,
                "Codex Linux callback target is invalid. Launch the session with 'agent-callback codex'.");
        }

        var connection = await _connections.GetAsync(connectionId, cancellationToken);
        if (connection is null)
        {
            return Failed(
                clientMessageId,
                $"Codex app-server connection '{connectionId}' is not registered.");
        }

        try
        {
            await using var client = await CodexAppServerClient.ConnectAsync(
                connection.SocketPath,
                ConnectTimeout,
                cancellationToken);
            await client.InitializeAsync(RequestTimeout, cancellationToken);
            var resume = await client.CallAsync(
                "thread/resume",
                new { threadId },
                deliveryRequest: false,
                RequestTimeout,
                cancellationToken);

            object parameters;
            if (steer)
            {
                var activeTurnId = FindActiveTurnId(resume);
                if (activeTurnId is null)
                {
                    return Failed(clientMessageId, "No active turn is in progress.");
                }

                parameters = new
                {
                    threadId,
                    clientUserMessageId = clientMessageId,
                    input = BuildTextInput(message),
                    expectedTurnId = activeTurnId
                };
            }
            else
            {
                parameters = new
                {
                    threadId,
                    clientUserMessageId = clientMessageId,
                    input = BuildTextInput(message),
                    cwd = string.IsNullOrWhiteSpace(workingDirectory)
                        ? null
                        : workingDirectory
                };
            }

            await client.CallAsync(
                steer ? "turn/steer" : "turn/start",
                parameters,
                deliveryRequest: true,
                RequestTimeout,
                cancellationToken);
            return new CodexOperationResult(
                true,
                clientMessageId,
                connectionId,
                null,
                DeliveryUncertain: false);
        }
        catch (CodexAppServerException exception)
        {
            return new CodexOperationResult(
                false,
                clientMessageId,
                connectionId,
                exception.Message,
                exception.DeliveryUncertain);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(clientMessageId, exception.Message, connectionId);
        }
    }

    private CodexOperationResult Failed(
        string clientMessageId,
        string error,
        string? connectionId = null) => new(
        false,
        clientMessageId,
        connectionId,
        error,
        DeliveryUncertain: false);

    private static string? FindActiveTurnId(JsonElement resume)
    {
        if (!resume.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (!thread.TryGetProperty("status", out var threadStatus) ||
            !threadStatus.TryGetProperty("type", out var threadStatusType) ||
            !string.Equals(
                threadStatusType.GetString(),
                "active",
                StringComparison.Ordinal))
        {
            return null;
        }

        string? activeTurnId = null;
        foreach (var turn in turns.EnumerateArray())
        {
            if (turn.TryGetProperty("status", out var status) &&
                string.Equals(status.GetString(), "inProgress", StringComparison.Ordinal) &&
                turn.TryGetProperty("id", out var id))
            {
                activeTurnId = id.GetString();
            }
        }

        return activeTurnId;
    }

    private static object[] BuildTextInput(string message) =>
    [
        new
        {
            type = "text",
            text = message,
            text_elements = Array.Empty<object>()
        }
    ];
}

[SupportedOSPlatform("linux")]
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _invoker;
    private long _requestId;

    private CodexAppServerClient(
        ClientWebSocket socket,
        HttpMessageInvoker invoker)
    {
        _socket = socket;
        _invoker = invoker;
    }

    public static async Task<CodexAppServerClient> ConnectAsync(
        string socketPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Codex app-server Unix sockets are available only on Linux.");
        }

        var normalizedPath = CodexAppServerConnectionStore.NormalizeSocketPath(socketPath);
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var handler = new SocketsHttpHandler();
        handler.ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(normalizedPath),
                    token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await webSocket.ConnectAsync(
                new Uri("ws://localhost/"),
                invoker,
                timeoutSource.Token);
            return new CodexAppServerClient(webSocket, invoker);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            invoker.Dispose();
            throw new CodexAppServerException(
                $"Could not connect to Codex app-server socket '{normalizedPath}': {exception.Message}",
                deliveryUncertain: false,
                exception);
        }
    }

    public async Task InitializeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await CallAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "agent-callback",
                    title = "Agent Callback",
                    version = "0.1.0-alpha.4"
                },
                capabilities = new
                {
                    experimentalApi = false,
                    requestAttestation = false
                }
            },
            deliveryRequest: false,
            timeout,
            cancellationToken);
        await SendAsync(
            new { jsonrpc = "2.0", method = "initialized", @params = new { } },
            cancellationToken);
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object parameters,
        bool deliveryRequest,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var sent = false;
        try
        {
            await SendAsync(
                new { jsonrpc = "2.0", id, method, @params = parameters },
                cancellationToken);
            sent = true;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(timeout);
            return await ReceiveResponseAsync(id, timeoutSource.Token);
        }
        catch (CodexAppServerException exception)
        {
            throw new CodexAppServerException(
                exception.Message,
                exception.IsDeterministicResponse
                    ? false
                    : deliveryRequest && (sent || exception.DeliveryUncertain),
                exception,
                exception.IsDeterministicResponse);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexAppServerException(
                $"Codex app-server request '{method}' timed out.",
                deliveryRequest && sent,
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CodexAppServerException(
                $"Codex app-server request '{method}' failed: {exception.Message}",
                deliveryRequest && sent,
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    timeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or
                OperationCanceledException)
            {
                // Best effort only; the request connection is intentionally short-lived.
            }
        }

        _socket.Dispose();
        _invoker.Dispose();
    }

    private async Task<JsonElement> ReceiveResponseAsync(
        long expectedId,
        CancellationToken cancellationToken)
    {
        for (var messageCount = 0; messageCount < 256; messageCount++)
        {
            using var document = await ReceiveDocumentAsync(cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id) &&
                id.TryGetInt64(out var numericId) &&
                numericId == expectedId)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;
                    throw new CodexAppServerException(
                        message ?? "Codex app-server rejected the request.",
                        deliveryUncertain: false,
                        isDeterministicResponse: true);
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new CodexAppServerException(
                        "Codex app-server returned a malformed response.",
                        deliveryUncertain: false);
                }

                return result.Clone();
            }

            if (root.TryGetProperty("id", out var serverRequestId) &&
                root.TryGetProperty("method", out _))
            {
                await SendAsync(
                    new
                    {
                        jsonrpc = "2.0",
                        id = serverRequestId.Clone(),
                        error = new
                        {
                            code = -32601,
                            message = "Agent Callback does not handle server-initiated requests."
                        }
                    },
                    cancellationToken);
            }
        }

        throw new CodexAppServerException(
            "Codex app-server response limit was exceeded.",
            deliveryUncertain: false);
    }

    private async Task<JsonDocument> ReceiveDocumentAsync(
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new CodexAppServerException(
                    "Codex app-server closed the connection.",
                    deliveryUncertain: false);
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new CodexAppServerException(
                    "Codex app-server sent a non-text WebSocket message.",
                    deliveryUncertain: false);
            }

            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > MaximumMessageBytes)
            {
                throw new CodexAppServerException(
                    "Codex app-server response exceeds the 4 MiB limit.",
                    deliveryUncertain: false);
            }

            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(buffer.ToArray());
            }
        }
    }

    private async Task SendAsync(object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }
}

internal sealed class CodexAppServerException : Exception
{
    public CodexAppServerException(
        string message,
        bool deliveryUncertain,
        Exception? innerException = null,
        bool isDeterministicResponse = false)
        : base(message, innerException)
    {
        DeliveryUncertain = deliveryUncertain;
        IsDeterministicResponse = isDeterministicResponse;
    }

    public bool DeliveryUncertain { get; }
    public bool IsDeterministicResponse { get; }
}
