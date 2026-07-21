using System.Diagnostics;
using System.Text.Json;
using AgentCallback.Domain;
using AgentCallback.Host;
using AgentCallback.Providers.Codex.AppServer;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Cli;

public sealed class CodexSessionLauncher
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private readonly HostLifecycle _hostLifecycle;
    private readonly HostPipeClient _client;

    public CodexSessionLauncher(HostLifecycle hostLifecycle, HostPipeClient client)
    {
        _hostLifecycle = hostLifecycle;
        _client = client;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The shared Codex app-server launcher is currently available only on Linux.");
        }

        await _hostLifecycle.StartAsync(cancellationToken);
        var codexExecutable = Environment.GetEnvironmentVariable(
            "AGENT_CALLBACK_CODEX_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(codexExecutable))
        {
            codexExecutable = "codex";
        }

        var daemonOutput = await StartRemoteControlAsync(
            codexExecutable,
            cancellationToken);
        var socketPath = ResolveSocketPath(daemonOutput);
        await WaitForSocketAsync(socketPath, cancellationToken);
        var connection = await _client.CallAsync<
            CodexAppServerConnectionRegistration,
            CodexAppServerConnectionResult>(
            "provider.connect.codex",
            new CodexAppServerConnectionRegistration(
                socketPath,
                "agent-callback-launcher",
                Environment.ProcessId),
            cancellationToken);
        var providerStatus = await _client.CallAsync<ProviderStatusRequest, ProviderStatus>(
            "provider.status",
            new ProviderStatusRequest("codex"),
            cancellationToken);
        if (!providerStatus.Available ||
            !string.Equals(
                providerStatus.ClientId,
                connection.ConnectionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                providerStatus.Error ??
                "The registered Codex app-server socket did not pass its protocol probe.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = codexExecutable,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--remote");
        startInfo.ArgumentList.Add("unix://");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["AGENT_CALLBACK_PROVIDER"] = "codex";
        startInfo.Environment["AGENT_CALLBACK_CODEX_CONNECTION_ID"] =
            connection.ConnectionId;
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Codex CLI.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    internal static string ResolveSocketPath(string daemonOutput)
    {
        if (!string.IsNullOrWhiteSpace(daemonOutput))
        {
            try
            {
                using var document = JsonDocument.Parse(daemonOutput);
                var discovered = FindStringProperty(
                    document.RootElement,
                    "socketPath");
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    return Path.GetFullPath(discovered);
                }
            }
            catch (JsonException)
            {
                // Fall through to the documented default socket location.
            }
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.GetFullPath(Path.Combine(
            codexHome,
            "app-server-control",
            "app-server-control.sock"));
    }

    private static async Task<string> StartRemoteControlAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("remote-control");
        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--json");

        try
        {
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start Codex remote control.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(StartTimeout);
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
            {
                // `remote-control start` also reports cloud pairing/auth state. The
                // local shared app-server may still be healthy and is the only
                // capability Agent Callback needs.
                if (File.Exists(ResolveSocketPath(output)))
                {
                    return output.Trim();
                }

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Codex remote-control start exited with code {process.ExitCode}."
                        : error.Trim());
            }

            return output.Trim();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Codex CLI was not found. Install its native Linux build or set " +
                "AGENT_CALLBACK_CODEX_EXECUTABLE.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Codex remote-control start did not finish within 30 seconds.",
                exception);
        }
    }

    private static async Task WaitForSocketAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(socketPath))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new FileNotFoundException(
            "Codex remote control started but its Unix socket was not found.",
            socketPath);
    }

    private static string? FindStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindStringProperty(property.Value, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
