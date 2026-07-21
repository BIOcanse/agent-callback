using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using AgentCallback.Application;
using AgentCallback.Domain;
using AgentCallback.Host;
using AgentCallback.Infrastructure;
using AgentCallback.Mcp;
using AgentCallback.Providers.Codex.AppServer;
using AgentCallback.Providers.OpenCode;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Cli;

public sealed class CommandLineApplication
{
    private static readonly JsonSerializerOptions OutputJson = CreateOutputJson();

    private readonly AppPaths _paths;
    private readonly HostPipeClient _client;
    private readonly HostLifecycle _hostLifecycle;
    private readonly Func<CancellationToken, Task> _runHost;

    private static JsonSerializerOptions CreateOutputJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public CommandLineApplication(
        AppPaths paths,
        HostPipeClient client,
        HostLifecycle hostLifecycle,
        Func<CancellationToken, Task> runHost)
    {
        _paths = paths;
        _client = client;
        _hostLifecycle = hostLifecycle;
        _runHost = runHost;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var result = args[0].ToLowerInvariant() switch
            {
                "host" => await RunHostCommandAsync(args[1..], cancellationToken),
                "codex" => await new CodexSessionLauncher(_hostLifecycle, _client)
                    .RunAsync(args[1..], cancellationToken),
                "provider" => await RunProviderCommandAsync(args[1..], cancellationToken),
                "register" => await RunRegisterCommandAsync(args[1..], cancellationToken),
                "trigger" => await RunTriggerCommandAsync(args[1..], cancellationToken),
                "get" => await RunGetCommandAsync(args[1..], cancellationToken),
                "list" => await RunListCommandAsync(args[1..], cancellationToken),
                "cancel" => await RunIdCommandAsync("callback.cancel", args[1..], cancellationToken),
                "acknowledge" => await RunIdCommandAsync(
                    "callback.acknowledge",
                    args[1..],
                    cancellationToken),
                "mcp" => await RunMcpAsync(cancellationToken),
                "version" or "--version" => RunVersion(),
                _ => throw new InvalidOperationException($"Unknown command: {args[0]}")
            };
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private async Task<int> RunHostCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var command = args.Length == 0 ? "run" : args[0].ToLowerInvariant();
        if (command == "run")
        {
            await _runHost(cancellationToken);
            return 0;
        }

        if (command == "start")
        {
            WriteJson(await _hostLifecycle.StartAsync(cancellationToken));
            return 0;
        }

        if (command == "stop")
        {
            WriteJson(await _hostLifecycle.StopAsync(cancellationToken));
            return 0;
        }

        if (command == "enable-startup")
        {
            WriteJson(await _hostLifecycle.EnableStartupAsync(cancellationToken));
            return 0;
        }

        if (command == "disable-startup")
        {
            WriteJson(await _hostLifecycle.DisableStartupAsync(cancellationToken));
            return 0;
        }

        if (command == "status")
        {
            WriteJson(await _hostLifecycle.GetStatusAsync(cancellationToken));
            return 0;
        }

        throw new InvalidOperationException(
            "Usage: agent-callback host run|start|stop|status|enable-startup|disable-startup");
    }

    private async Task<int> RunProviderCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length is >= 1 and <= 2 &&
            string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(await _client.CallAsync<ProviderStatusRequest, ProviderStatus>(
                "provider.status",
                new ProviderStatusRequest(args.Length == 2 ? args[1] : "codex"),
                cancellationToken));
            return 0;
        }

        if (args.Length == 3 &&
            string.Equals(args[0], "connect", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "opencode", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[2], "--stdin", StringComparison.OrdinalIgnoreCase))
        {
            var input = await Console.In.ReadToEndAsync(cancellationToken);
            if (input.Length > 16 * 1024)
            {
                throw new InvalidDataException("OpenCode connection payload is too large.");
            }

            var registration = JsonSerializer.Deserialize<OpenCodeConnectionRegistration>(
                input,
                OutputJson) ?? throw new InvalidDataException(
                    "OpenCode connection payload is invalid.");
            WriteJson(await _client.CallAsync<
                OpenCodeConnectionRegistration,
                OpenCodeConnectionResult>(
                "provider.connect.opencode",
                registration,
                cancellationToken));
            return 0;
        }

        if (args.Length == 3 &&
            string.Equals(args[0], "connect", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "codex", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[2], "--stdin", StringComparison.OrdinalIgnoreCase))
        {
            var input = await Console.In.ReadToEndAsync(cancellationToken);
            if (input.Length > 16 * 1024)
            {
                throw new InvalidDataException("Codex connection payload is too large.");
            }

            var registration = JsonSerializer.Deserialize<CodexAppServerConnectionRegistration>(
                input,
                OutputJson) ?? throw new InvalidDataException(
                "Codex connection payload is invalid.");
            WriteJson(await _client.CallAsync<
                CodexAppServerConnectionRegistration,
                CodexAppServerConnectionResult>(
                "provider.connect.codex",
                registration,
                cancellationToken));
            return 0;
        }

        throw new InvalidOperationException(
            "Usage: agent-callback provider status [provider] | " +
            "provider connect opencode|codex --stdin");
    }

    private async Task<int> RunRegisterCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("Usage: agent-callback register process|event [options]");
        }

        var source = args[0].ToLowerInvariant() switch
        {
            "process" => CallbackSourceKind.Process,
            "event" => CallbackSourceKind.Event,
            _ => throw new InvalidOperationException("Registration type must be process or event.")
        };
        var reader = new ArgumentReader(args[1..]);
        var provider = reader.Optional("provider") ??
            Environment.GetEnvironmentVariable("AGENT_CALLBACK_PROVIDER") ??
            "codex";
        var threadId = AgentTargetResolver.Resolve(provider, reader.Optional("thread"));
        var request = new RegisterCallbackRequest
        {
            Label = reader.Optional("label"),
            Provider = provider,
            TargetThreadId = threadId,
            SourceKind = source,
            ProcessId = source == CallbackSourceKind.Process
                ? reader.OptionalInt32("pid") ??
                  throw new InvalidOperationException("--pid is required for a process callback.")
                : null,
            ExpectedCommandLineContains = reader.Optional("expect-command"),
            WorkingDirectory = reader.Optional("cwd") ?? Environment.CurrentDirectory,
            Instruction = reader.Required("instruction"),
            EvidencePaths = reader.All("evidence"),
            ExpiresUtc = reader.OptionalDateTimeOffset("expires")
        };
        WriteJson(await _client.CallAsync<RegisterCallbackRequest, RegisterCallbackResult>(
            "callback.register",
            request,
            cancellationToken));
        return 0;
    }

    private async Task<int> RunTriggerCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        var request = new TriggerCallbackRequest
        {
            CallbackId = reader.Positional(0, "Callback id"),
            TriggerSecret = reader.Required("secret"),
            ReportedOutcome = reader.Optional("outcome") ?? "unknown",
            ExitCode = reader.OptionalInt32("exit-code"),
            Summary = reader.Optional("summary"),
            EvidenceRefs = reader.All("evidence")
        };
        WriteJson(await _client.CallAsync<TriggerCallbackRequest, CallbackRecord>(
            "callback.trigger",
            request,
            cancellationToken));
        return 0;
    }

    private async Task<int> RunGetCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        WriteJson(await _client.CallAsync<CallbackIdRequest, CallbackRecord>(
            "callback.get",
            new CallbackIdRequest(reader.Positional(0, "Callback id")),
            cancellationToken));
        return 0;
    }

    private async Task<int> RunListCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        WriteJson(await _client.CallAsync<CallbackListRequest, IReadOnlyList<CallbackRecord>>(
            "callback.list",
            new CallbackListRequest(reader.Optional("state"), reader.OptionalInt32("limit") ?? 100),
            cancellationToken));
        return 0;
    }

    private async Task<int> RunIdCommandAsync(
        string operation,
        string[] args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        WriteJson(await _client.CallAsync<CallbackIdRequest, CallbackRecord>(
            operation,
            new CallbackIdRequest(reader.Positional(0, "Callback id")),
            cancellationToken));
        return 0;
    }

    private async Task<int> RunMcpAsync(CancellationToken cancellationToken)
    {
        var server = new McpServer(_client);
        await server.RunAsync(Console.In, Console.Out, cancellationToken);
        return 0;
    }

    private static int RunVersion()
    {
        var version = typeof(CommandLineApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        WriteJson(new
        {
            name = "agent-callback",
            version,
            providers = new[] { "codex", "opencode" },
            coreIsProviderNeutral = true
        });
        return 0;
    }

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, OutputJson));

    private static bool IsHelp(string value) =>
        value is "help" or "--help" or "-h";

    private void PrintHelp()
    {
        Console.Out.WriteLine($"""
            Agent Callback v0.1 (Windows/Linux, provider transports may be experimental)

            Data directory: {_paths.DataDirectory}

            Commands:
              agent-callback host run|start|stop|status|enable-startup|disable-startup
              agent-callback codex [Codex CLI arguments] (Linux shared-session launcher)
              agent-callback provider status [provider]
              agent-callback provider connect opencode|codex --stdin
              agent-callback register process --pid <pid> --instruction <text> [--thread <id>]
              agent-callback register event --instruction <text> [--thread <id>]
              agent-callback trigger <id> --secret <secret> [--outcome succeeded|failed|canceled|unknown]
              agent-callback get <id>
              agent-callback list [--state <state>] [--limit <n>]
              agent-callback cancel <id>
              agent-callback acknowledge <id>
              agent-callback mcp
              agent-callback version

            Registration options:
              --provider <name> --cwd <path> --label <text> --evidence <path> --expires <ISO-8601>
              --expect-command <marker> (process callbacks only)
            """);
    }
}
