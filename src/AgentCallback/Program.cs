using AgentCallback.Application;
using AgentCallback.Cli;
using AgentCallback.Domain;
using AgentCallback.Host;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Security;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Providers.Codex;
using AgentCallback.Providers.Codex.DesktopIpc;
using AgentCallback.Providers.OpenCode;
using AgentCallback.Transport.NamedPipe;
using AgentCallback.Triggers.Process;

namespace AgentCallback;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var paths = AppPaths.Resolve();
        var client = new HostPipeClient(paths);
        var application = new CommandLineApplication(
            paths,
            client,
            token => CreateHost(paths).RunAsync(token));
        return await application.RunAsync(args, cancellation.Token);
    }

    private static CallbackHost CreateHost(AppPaths paths)
    {
        var stopSignal = new HostStopSignal();
        var processInspector = new WindowsProcessInspector();
        var secretProtector = new DpapiSecretProtector();
        ICallbackStore store = new SqliteCallbackStore(paths, secretProtector);
        ICodexConversationTransport transport = new CodexDesktopConversationTransport();
        var codexProvider = new CodexSmartProvider(transport);
        var openCodeConnections = new OpenCodeConnectionStore(paths, secretProtector);
        var openCodeProvider = new OpenCodeProvider(
            openCodeConnections,
            new OpenCodeHttpTransport());
        var providers = new AgentProviderRegistry([codexProvider, openCodeProvider]);
        var service = new CallbackService(store, processInspector, providers);
        var worker = new CallbackHostWorker(store, service, processInspector, providers);
        var handler = new CallbackHostRequestHandler(
            service,
            store,
            providers,
            openCodeConnections,
            paths,
            stopSignal);
        return new CallbackHost(paths, store, worker, handler, stopSignal);
    }
}
