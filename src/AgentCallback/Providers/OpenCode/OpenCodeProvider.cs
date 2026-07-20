using AgentCallback.Domain;

namespace AgentCallback.Providers.OpenCode;

public sealed class OpenCodeProvider : IAgentProvider
{
    private readonly IOpenCodeConnectionStore _connections;
    private readonly IOpenCodeConversationTransport _transport;

    public OpenCodeProvider(
        IOpenCodeConnectionStore connections,
        IOpenCodeConversationTransport transport)
    {
        _connections = connections;
        _transport = transport;
    }

    public string Name => "opencode";

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var connections = await _connections.ListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return new ProviderStatus(
                Name,
                false,
                Experimental: true,
                _transport.Name,
                null,
                "No OpenCode connection is registered. Restart OpenCode after installing Agent Callback.");
        }

        var candidates = connections.Take(8).ToArray();
        var probes = await Task.WhenAll(candidates.Select(async connection =>
            (Connection: connection, Probe: await _transport.ProbeAsync(
                connection,
                cancellationToken))));
        foreach (var result in probes)
        {
            if (result.Probe.Available)
            {
                return new ProviderStatus(
                    Name,
                    true,
                    Experimental: true,
                    _transport.Name,
                    result.Connection.ConnectionId,
                    null);
            }
        }

        return new ProviderStatus(
            Name,
            false,
            Experimental: true,
            _transport.Name,
            connections[0].ConnectionId,
            probes[0].Probe.Error ?? "Registered OpenCode connections are unavailable.");
    }

    public async Task<CallbackDeliveryResult> DeliverAsync(
        CallbackRecord callback,
        string message,
        CancellationToken cancellationToken)
    {
        if (!OpenCodeTarget.TryParse(
            callback.TargetThreadId,
            out var connectionId,
            out var sessionId))
        {
            return CallbackDeliveryResult.Failed(
                _transport.Name,
                callback.ClientMessageId,
                "OpenCode callback target is invalid.");
        }

        var connection = await _connections.GetAsync(connectionId, cancellationToken);
        if (connection is null)
        {
            return CallbackDeliveryResult.Failed(
                _transport.Name,
                callback.ClientMessageId,
                $"OpenCode connection '{connectionId}' is not registered.");
        }

        var probe = await _transport.ProbeAsync(connection, cancellationToken);
        if (!probe.Available)
        {
            return CallbackDeliveryResult.Retryable(
                _transport.Name,
                callback.ClientMessageId,
                probe.Error ?? "OpenCode server is unavailable.");
        }

        var session = await _transport.ProbeSessionAsync(
            connection,
            sessionId,
            cancellationToken);
        if (!session.Exists)
        {
            return session.Retryable
                ? CallbackDeliveryResult.Retryable(
                    _transport.Name,
                    callback.ClientMessageId,
                    session.Error ?? "OpenCode session probe failed.")
                : CallbackDeliveryResult.Failed(
                    _transport.Name,
                    callback.ClientMessageId,
                    session.Error ?? "OpenCode session was not found.");
        }

        if (session.Busy)
        {
            return CallbackDeliveryResult.Retryable(
                _transport.Name,
                callback.ClientMessageId,
                "OpenCode session is busy; delivery will wait for it to become idle.");
        }

        var prompt = await _transport.PromptAsync(
            connection,
            sessionId,
            message,
            cancellationToken);
        if (prompt.Accepted)
        {
            return CallbackDeliveryResult.Accepted(
                _transport.Name,
                attachedToExisting: false,
                callback.ClientMessageId);
        }

        var error = prompt.Error ?? "OpenCode did not accept the callback prompt.";
        if (prompt.Uncertain)
        {
            return CallbackDeliveryResult.Ambiguous(
                _transport.Name,
                callback.ClientMessageId,
                error);
        }

        return prompt.Retryable
            ? CallbackDeliveryResult.Retryable(_transport.Name, callback.ClientMessageId, error)
            : CallbackDeliveryResult.Failed(_transport.Name, callback.ClientMessageId, error);
    }
}

public static class OpenCodeTarget
{
    public static string Format(string connectionId, string sessionId) =>
        $"{connectionId}:{sessionId}";

    public static bool TryParse(
        string value,
        out string connectionId,
        out string sessionId)
    {
        connectionId = string.Empty;
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf(':', separator + 1) >= 0)
        {
            return false;
        }

        connectionId = value[..separator];
        sessionId = value[(separator + 1)..];
        return connectionId.StartsWith("oc_", StringComparison.Ordinal) &&
            connectionId.Length is >= 8 and <= 64 &&
            sessionId.Length is >= 3 and <= 128 &&
            connectionId.All(IsIdentifierCharacter) &&
            sessionId.All(IsIdentifierCharacter);
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '-';
}
