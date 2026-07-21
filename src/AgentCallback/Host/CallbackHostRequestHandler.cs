using System.Text.Json;
using AgentCallback.Application;
using AgentCallback.Domain;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Providers.Codex.AppServer;
using AgentCallback.Providers.OpenCode;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Host;

public sealed class CallbackHostRequestHandler
{
    private readonly CallbackService _service;
    private readonly ICallbackStore _store;
    private readonly IAgentProviderRegistry _providers;
    private readonly IOpenCodeConnectionStore _openCodeConnections;
    private readonly ICodexAppServerConnectionStore? _codexConnections;
    private readonly AppPaths _paths;
    private readonly HostStopSignal _stopSignal;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public CallbackHostRequestHandler(
        CallbackService service,
        ICallbackStore store,
        IAgentProviderRegistry providers,
        IOpenCodeConnectionStore openCodeConnections,
        ICodexAppServerConnectionStore? codexConnections,
        AppPaths paths,
        HostStopSignal stopSignal)
    {
        _service = service;
        _store = store;
        _providers = providers;
        _openCodeConnections = openCodeConnections;
        _codexConnections = codexConnections;
        _paths = paths;
        _stopSignal = stopSignal;
    }

    public async Task<HostResponse> HandleAsync(
        HostRequest request,
        CancellationToken cancellationToken)
    {
        return request.Operation switch
        {
            "host.status" => HostResponse.Success(new HostStatus(
                true,
                Environment.ProcessId,
                HostProtocol.Version,
                _startedUtc,
                _paths.DatabasePath)),
            "host.stop" => StopHost(),
            "provider.status" => HostResponse.Success(
                await ProviderStatusAsync(request.Payload, cancellationToken)),
            "provider.connect.opencode" => HostResponse.Success(
                await ConnectOpenCodeAsync(request.Payload, cancellationToken)),
            "provider.connect.codex" => HostResponse.Success(
                await ConnectCodexAsync(request.Payload, cancellationToken)),
            "callback.register" => HostResponse.Success(
                await _service.RegisterAsync(
                    Deserialize<RegisterCallbackRequest>(request.Payload),
                    cancellationToken)),
            "callback.trigger" => HostResponse.Success(
                await _service.TriggerAsync(
                    Deserialize<TriggerCallbackRequest>(request.Payload),
                    cancellationToken)),
            "callback.get" => HostResponse.Success(
                await GetRequiredAsync(
                    Deserialize<CallbackIdRequest>(request.Payload).CallbackId,
                    cancellationToken)),
            "callback.list" => HostResponse.Success(
                await ListAsync(Deserialize<CallbackListRequest>(request.Payload), cancellationToken)),
            "callback.cancel" => HostResponse.Success(
                await _service.CancelAsync(
                    Deserialize<CallbackIdRequest>(request.Payload).CallbackId,
                    cancellationToken)),
            "callback.acknowledge" => HostResponse.Success(
                await _service.AcknowledgeAsync(
                    Deserialize<CallbackIdRequest>(request.Payload).CallbackId,
                    cancellationToken)),
            _ => HostResponse.Failure($"Unknown host operation: {request.Operation}")
        };
    }

    private Task<ProviderStatus> ProviderStatusAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var request = Deserialize<ProviderStatusRequest>(payload);
        return _providers.GetRequired(request.Provider).GetStatusAsync(cancellationToken);
    }

    private Task<OpenCodeConnectionResult> ConnectOpenCodeAsync(
        JsonElement payload,
        CancellationToken cancellationToken) =>
        _openCodeConnections.UpsertAsync(
            Deserialize<OpenCodeConnectionRegistration>(payload),
            cancellationToken);

    private Task<CodexAppServerConnectionResult> ConnectCodexAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (_codexConnections is null)
        {
            throw new PlatformNotSupportedException(
                "Codex app-server connection registration is available only on Linux.");
        }

        return _codexConnections.UpsertAsync(
            Deserialize<CodexAppServerConnectionRegistration>(payload),
            cancellationToken);
    }

    private HostResponse StopHost()
    {
        _stopSignal.RequestAfterResponse();
        return HostResponse.Success(new { stopRequested = true });
    }

    private async Task<CallbackRecord> GetRequiredAsync(
        string callbackId,
        CancellationToken cancellationToken) =>
        await _service.GetAsync(callbackId, cancellationToken) ??
        throw new KeyNotFoundException($"Callback not found: {callbackId}");

    private Task<IReadOnlyList<CallbackRecord>> ListAsync(
        CallbackListRequest request,
        CancellationToken cancellationToken)
    {
        CallbackState? state = null;
        if (!string.IsNullOrWhiteSpace(request.State))
        {
            if (!Enum.TryParse<CallbackState>(request.State, true, out var parsedState))
            {
                throw new InvalidOperationException($"Unknown callback state: {request.State}");
            }

            state = parsedState;
        }

        return _store.ListAsync(state, request.Limit, cancellationToken);
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(HostJson.Options) ??
        throw new InvalidDataException("Host request payload is invalid.");
}
