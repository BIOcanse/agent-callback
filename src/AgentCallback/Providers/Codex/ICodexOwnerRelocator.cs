namespace AgentCallback.Providers.Codex;

/// <summary>
/// Makes Codex load a conversation in an owner window without sending anything, so a delivery
/// that was rejected because no window owned the conversation can be retried once.
/// </summary>
public interface ICodexOwnerRelocator
{
    /// <returns>True when the relocation was requested and the owner had time to register.</returns>
    Task<bool> RelocateAsync(string threadId, CancellationToken cancellationToken);
}
