using AgentCallback.Application;
using AgentCallback.Domain;

namespace AgentCallback.Providers.Codex;

public sealed class CodexSmartProvider : IAgentProvider
{
    private readonly ICodexConversationTransport _transport;

    public CodexSmartProvider(ICodexConversationTransport transport)
    {
        _transport = transport;
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
