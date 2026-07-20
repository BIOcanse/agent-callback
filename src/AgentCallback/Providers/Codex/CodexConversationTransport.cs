namespace AgentCallback.Providers.Codex;

public sealed record CodexTransportStatus(
    bool Available,
    string? ClientId,
    string? Error);

public sealed record CodexOperationResult(
    bool Accepted,
    string ClientMessageId,
    string? HandledByClientId,
    string? Error,
    bool DeliveryUncertain);

public interface ICodexConversationTransport
{
    string Name { get; }

    Task<CodexTransportStatus> ProbeAsync(CancellationToken cancellationToken);

    Task<CodexOperationResult> StartTurnAsync(
        string threadId,
        string message,
        string clientMessageId,
        CancellationToken cancellationToken);

    Task<CodexOperationResult> SteerTurnAsync(
        string threadId,
        string message,
        string workingDirectory,
        string clientMessageId,
        CancellationToken cancellationToken);
}
