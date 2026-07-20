using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Host;

public sealed class CallbackHost
{
    private readonly AppPaths _paths;
    private readonly ICallbackStore _store;
    private readonly CallbackHostWorker _worker;
    private readonly CallbackHostRequestHandler _handler;
    private readonly HostStopSignal _stopSignal;

    public CallbackHost(
        AppPaths paths,
        ICallbackStore store,
        CallbackHostWorker worker,
        CallbackHostRequestHandler handler,
        HostStopSignal stopSignal)
    {
        _paths = paths;
        _store = store;
        _worker = worker;
        _handler = handler;
        _stopSignal = stopSignal;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\{_paths.PipeName}-host",
            out var ownsMutex);
        if (!ownsMutex)
        {
            throw new InvalidOperationException("Agent Callback Host is already running for this user.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopSignal.Token);
        var hostToken = linkedCancellation.Token;
        await _store.InitializeAsync(hostToken);
        await using var worker = _worker;
        using var stopSignal = _stopSignal;
        var server = new HostPipeServer(_paths, _handler.HandleAsync);
        Console.Error.WriteLine(
            $"Agent Callback Host {HostProtocol.Version} running as PID {Environment.ProcessId}.");
        await Task.WhenAll(
            server.RunAsync(hostToken),
            worker.RunAsync(hostToken));
    }
}
