using AgentCallback.Application;
using AgentCallback.Domain;

namespace AgentCallback.Providers.Codex;

public sealed class CodexSmartProvider : IAgentProvider
{
    private readonly ICodexConversationTransport _transport;
    private readonly ICodexOwnerRelocator? _ownerRelocator;
    // Relocation and its retry run one at a time, so concurrent deliveries do not take the owner
    // window from each other between relocating and retrying.
    private readonly SemaphoreSlim _relocationGate = new(1, 1);

    /// <param name="ownerRelocator">
    /// When set, a delivery rejected because no Codex window owns the conversation is retried once
    /// after relocating the owner; null disables relocation.
    /// </param>
    public CodexSmartProvider(
        ICodexConversationTransport transport,
        ICodexOwnerRelocator? ownerRelocator = null)
    {
        _transport = transport;
        _ownerRelocator = ownerRelocator;
    }

    public string Name => "codex";

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _transport.ProbeAsync(cancellationToken);
        return new ProviderStatus(
            "codex",
            status.Available,
            Experimental: true,
            _transport.Name,
            status.ClientId,
            status.Error);
    }

    public async Task<CallbackDeliveryResult> DeliverAsync(
        CallbackRecord callback,
        string message,
        CancellationToken cancellationToken)
    {
        var result = await DeliverOnceAsync(callback, message, cancellationToken);
        if (_ownerRelocator is null ||
            result.Kind != DeliveryOutcomeKind.Retryable ||
            !CodexDeliveryClassifier.IsOwnerMissing(result.Error))
        {
            return result;
        }

        // The rejection was deterministic, so nothing was written and one retry cannot duplicate.
        await _relocationGate.WaitAsync(cancellationToken);
        try
        {
            if (!await _ownerRelocator.RelocateAsync(callback.TargetThreadId, cancellationToken))
            {
                return result;
            }

            return await DeliverOnceAsync(callback, message, cancellationToken);
        }
        finally
        {
            _relocationGate.Release();
        }
    }

    private async Task<CallbackDeliveryResult> DeliverOnceAsync(
        CallbackRecord callback,
        string message,
        CancellationToken cancellationToken)
    {
        if (callback.DeliveryMode != CallbackDeliveryMode.Smart)
        {
            return CallbackDeliveryResult.Failed(
                _transport.Name,
                callback.ClientMessageId,
                $"Unsupported delivery mode: {callback.DeliveryMode}");
        }

        var steer = await _transport.SteerTurnAsync(
            callback.TargetThreadId,
            message,
            callback.WorkingDirectory,
            callback.ClientMessageId,
            cancellationToken);
        if (steer.Accepted)
        {
            return CallbackDeliveryResult.Accepted(
                _transport.Name,
                attachedToExisting: true,
                callback.ClientMessageId);
        }

        if (steer.DeliveryUncertain)
        {
            return CallbackDeliveryResult.Ambiguous(
                _transport.Name,
                callback.ClientMessageId,
                steer.Error ?? "Codex steer delivery is uncertain.");
        }

        if (!CodexDeliveryClassifier.IsDeterministicInactive(steer.Error))
        {
            return ClassifyDeterministicFailure(callback.ClientMessageId, steer.Error);
        }

        var followUp = await _transport.StartTurnAsync(
            callback.TargetThreadId,
            message,
            callback.ClientMessageId,
            cancellationToken);
        if (followUp.Accepted)
        {
            return CallbackDeliveryResult.Accepted(
                _transport.Name,
                attachedToExisting: false,
                callback.ClientMessageId);
        }

        if (followUp.DeliveryUncertain)
        {
            return CallbackDeliveryResult.Ambiguous(
                _transport.Name,
                callback.ClientMessageId,
                followUp.Error ?? "Codex follow-up delivery is uncertain.");
        }

        if (!CodexDeliveryClassifier.IsDeterministicActiveConflict(followUp.Error))
        {
            return ClassifyDeterministicFailure(callback.ClientMessageId, followUp.Error);
        }

        var finalSteer = await _transport.SteerTurnAsync(
            callback.TargetThreadId,
            message,
            callback.WorkingDirectory,
            callback.ClientMessageId,
            cancellationToken);
        if (finalSteer.Accepted)
        {
            return CallbackDeliveryResult.Accepted(
                _transport.Name,
                attachedToExisting: true,
                callback.ClientMessageId);
        }

        if (finalSteer.DeliveryUncertain)
        {
            return CallbackDeliveryResult.Ambiguous(
                _transport.Name,
                callback.ClientMessageId,
                finalSteer.Error ?? "Codex race-correction steer delivery is uncertain.");
        }

        return ClassifyDeterministicFailure(callback.ClientMessageId, finalSteer.Error);
    }

    private CallbackDeliveryResult ClassifyDeterministicFailure(
        string clientMessageId,
        string? error)
    {
        var reason = error ?? "Codex Desktop did not accept the callback message.";
        return CodexDeliveryClassifier.IsRetryableOwnerUnavailable(reason)
            ? CallbackDeliveryResult.Retryable(_transport.Name, clientMessageId, reason)
            : CallbackDeliveryResult.Failed(_transport.Name, clientMessageId, reason);
    }
}
