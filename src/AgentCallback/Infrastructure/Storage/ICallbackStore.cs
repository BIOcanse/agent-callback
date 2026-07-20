using AgentCallback.Domain;

namespace AgentCallback.Infrastructure.Storage;

public interface ICallbackStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<CallbackRecord> CreateAsync(
        CallbackRecord callback,
        byte[]? triggerSecretHash,
        CancellationToken cancellationToken);

    Task<CallbackRecord?> GetAsync(string callbackId, CancellationToken cancellationToken);

    Task<byte[]?> GetTriggerSecretHashAsync(
        string callbackId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CallbackRecord>> ListAsync(
        CallbackState? state,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CallbackRecord>> ListWatchingAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CallbackRecord>> ListDispatchableAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);

    Task<bool> TryMarkReadyAsync(
        string callbackId,
        CompletionObservation observation,
        CancellationToken cancellationToken);

    Task<bool> TryBeginDispatchAsync(
        string callbackId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task CompleteDispatchAsync(
        string callbackId,
        CallbackDeliveryResult result,
        DateTimeOffset completedUtc,
        DateTimeOffset? nextAttemptUtc,
        CancellationToken cancellationToken);

    Task<bool> TryMarkFailedAsync(
        string callbackId,
        string error,
        CancellationToken cancellationToken);

    Task<bool> TryCancelAsync(string callbackId, CancellationToken cancellationToken);

    Task<bool> TryAcknowledgeAsync(string callbackId, CancellationToken cancellationToken);

    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
