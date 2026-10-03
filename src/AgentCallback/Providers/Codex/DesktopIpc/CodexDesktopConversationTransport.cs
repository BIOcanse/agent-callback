namespace AgentCallback.Providers.Codex.DesktopIpc;

public sealed class CodexDesktopConversationTransport : ICodexConversationTransport
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private readonly CodexDesktopIpcInvoker _invoker = new();

    public string Name => "codex-desktop-ipc-experimental";

    public async Task<CodexTransportStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clientId = await _invoker.ProbeAsync(cancellationToken);
            return new CodexTransportStatus(true, clientId, null);
        }
        catch (CodexDesktopIpcException exception)
        {
            return new CodexTransportStatus(false, null, exception.Message);
        }
    }

    public Task<CodexOperationResult> StartTurnAsync(
        string threadId,
        string message,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        // start-turn version 2 (Codex Desktop 26.901 and later): the request is wrapped in
        // turnStart.request and the thread keeps its own settings. A version-1 envelope is
        // rejected by the router as no-client-found, so idle threads were never reached.
        var parameters = new Dictionary<string, object?>
        {
            ["conversationId"] = threadId,
            ["turnStart"] = new Dictionary<string, object?>
            {
                ["request"] = new Dictionary<string, object?>
                {
                    ["threadId"] = threadId,
                    ["clientUserMessageId"] = clientMessageId,
                    ["input"] = BuildTextInput(message)
                },
                ["context"] = new Dictionary<string, object?>
                {
                    ["inheritThreadSettings"] = true
                }
            }
        };
        return InvokeDeliveryAsync(
            "thread-follower-start-turn",
            parameters,
            clientMessageId,
            cancellationToken);
    }

    public Task<CodexOperationResult> SteerTurnAsync(
        string threadId,
        string message,
        string workingDirectory,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        var cwd = workingDirectory.Trim();
        var workspaceRoots = cwd.Length == 0 ? Array.Empty<string>() : new[] { cwd };
        var parameters = new Dictionary<string, object?>
        {
            ["conversationId"] = threadId,
            ["clientUserMessageId"] = clientMessageId,
            ["input"] = BuildTextInput(message),
            ["serviceTier"] = null,
            ["attachments"] = Array.Empty<object>(),
            ["restoreMessage"] = new Dictionary<string, object?>
            {
                ["id"] = clientMessageId,
                ["text"] = message,
                ["context"] = new Dictionary<string, object?>
                {
                    ["prompt"] = message,
                    ["workspaceRoots"] = workspaceRoots,
                    ["collaborationMode"] = null,
                    ["imageAttachments"] = Array.Empty<object>(),
                    ["fileAttachments"] = Array.Empty<object>(),
                    ["pastedTextAttachments"] = Array.Empty<object>(),
                    ["addedFiles"] = Array.Empty<object>(),
                    ["commentAttachments"] = Array.Empty<object>(),
                    ["mcpAppModelContextAttachments"] = Array.Empty<object>(),
                    ["appshotContexts"] = Array.Empty<object>()
                },
                ["cwd"] = cwd,
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
        return InvokeDeliveryAsync(
            "thread-follower-steer-turn",
            parameters,
            clientMessageId,
            cancellationToken);
    }

    private async Task<CodexOperationResult> InvokeDeliveryAsync(
        string method,
        object parameters,
        string clientMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _invoker.InvokeAsync(
                method,
                parameters,
                SendTimeout,
                cancellationToken);
            var accepted = string.Equals(
                JsonOptions.GetString(response, "resultType"),
                "success",
                StringComparison.OrdinalIgnoreCase);
            return new CodexOperationResult(
                accepted,
                clientMessageId,
                JsonOptions.GetString(response, "handledByClientId"),
                accepted
                    ? null
                    : JsonOptions.GetString(response, "error") ??
                      "Codex Desktop IPC rejected the request.",
                DeliveryUncertain: false);
        }
        catch (CodexDesktopIpcException exception)
        {
            return new CodexOperationResult(
                Accepted: false,
                clientMessageId,
                HandledByClientId: null,
                exception.Message,
                exception.DeliveryUncertain);
        }
    }

    private static object[] BuildTextInput(string message) =>
    [
        new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = message,
            ["text_elements"] = Array.Empty<object>()
        }
    ];

}
