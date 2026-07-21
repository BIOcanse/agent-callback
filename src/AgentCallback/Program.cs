using AgentCallback.Application;
using AgentCallback.Cli;
using AgentCallback.Domain;
using AgentCallback.Host;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Security;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Providers.Codex;
using AgentCallback.Providers.Codex.AppServer;
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
        var startup = CreateStartupRegistration();
        var lifecycle = new HostLifecycle(paths, client, startup);
        var application = new CommandLineApplication(
            paths,
            client,
            lifecycle,
            token => CreateHost(paths).RunAsync(token));
        return await application.RunAsync(args, cancellation.Token);
    }

    private static CallbackHost CreateHost(AppPaths paths)
    {
        var stopSignal = new HostStopSignal();
        var processInspector = CreateProcessInspector();
        var secretProtector = CreateSecretProtector(paths);
        ICallbackStore store = new SqliteCallbackStore(paths, secretProtector);
        ICodexAppServerConnectionStore? codexConnections = null;
        ICodexConversationTransport transport;
        if (OperatingSystem.IsLinux())
        {
            codexConnections = new CodexAppServerConnectionStore(paths);
            transport = new CodexAppServerConversationTransport(codexConnections);
        }
        else
        {
#if WINDOWS
            transport = new CodexDesktopConversationTransport();
#else
            throw new PlatformNotSupportedException(
                "This build does not include the Windows Codex Desktop transport.");
#endif
        }

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
            codexConnections,
            paths,
            stopSignal);
        return new CallbackHost(paths, store, worker, handler, stopSignal);
    }

    private static IStartupRegistration CreateStartupRegistration()
    {
        if (OperatingSystem.IsLinux())
        {
            return new SystemdUserStartupRegistration();
        }

#if WINDOWS
        return new WindowsRegistryStartupRegistration();
#else
        throw new PlatformNotSupportedException(
            "Agent Callback currently supports Windows and Linux.");
#endif
    }

    private static IProcessInspector CreateProcessInspector()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessInspector();
        }

#if WINDOWS
        return new WindowsProcessInspector();
#else
        throw new PlatformNotSupportedException(
            "Agent Callback currently supports Windows and Linux.");
#endif
    }

    private static ISecretProtector CreateSecretProtector(AppPaths paths)
    {
        if (OperatingSystem.IsLinux())
        {
            return new FileKeySecretProtector(paths);
        }

#if WINDOWS
        return new DpapiSecretProtector();
#else
        throw new PlatformNotSupportedException(
            "Agent Callback currently supports Windows and Linux.");
#endif
    }
}
