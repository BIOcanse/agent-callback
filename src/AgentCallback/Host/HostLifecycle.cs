using System.Diagnostics;
using AgentCallback.Infrastructure;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Host;

public sealed record HostLifecycleResult(
    string Action,
    bool StartupEnabled,
    bool Running,
    int? ProcessId,
    string Command);

public interface IStartupRegistration
{
    bool IsEnabled();
    void Enable(HostLaunchCommand command);
    void Disable();
}

public sealed record HostLaunchCommand(
    string Executable,
    IReadOnlyList<string> PrefixArguments,
    string CommandLine);

public sealed class HostLifecycle
{
    private readonly AppPaths _paths;
    private readonly HostPipeClient _client;
    private readonly IStartupRegistration _startup;

    public HostLifecycle(
        AppPaths paths,
        HostPipeClient client,
        IStartupRegistration startup)
    {
        _paths = paths;
        _client = client;
        _startup = startup;
    }

    public async Task<HostLifecycleResult> StartAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        var current = await TryGetStatusAsync(cancellationToken);
        if (current is not null)
        {
            return Result("start", command, current);
        }

        _paths.EnsureDataDirectory();
        using var process = StartDetached(command);
        HostStatus? status = null;
        var attempt = 0;
        while (attempt < 15 && status is null)
        {
            attempt++;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            status = await TryGetStatusAsync(cancellationToken);
            if (process.HasExited && status is null)
            {
                throw new InvalidOperationException(
                    $"Agent Callback Host exited during startup with code {process.ExitCode}.");
            }
        }

        return status is not null
            ? Result("start", command, status)
            : throw new InvalidOperationException("Agent Callback Host did not become ready.");
    }

    public async Task<HostLifecycleResult> StopAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        var status = await TryGetStatusAsync(cancellationToken);
        if (status is null)
        {
            return new HostLifecycleResult(
                "stop",
                _startup.IsEnabled(),
                false,
                null,
                command.CommandLine);
        }

        await _client.CallAsync<object, StopHostResult>(
            "host.stop",
            new { },
            cancellationToken);
        return new HostLifecycleResult(
            "stop",
            _startup.IsEnabled(),
            false,
            status.ProcessId,
            command.CommandLine);
    }

    public async Task<HostLifecycleResult> EnableStartupAsync(
        CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        _startup.Enable(command);
        var started = await StartAsync(cancellationToken);
        return started with { Action = "enable-startup", StartupEnabled = true };
    }

    public async Task<HostLifecycleResult> DisableStartupAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = ResolveCurrentCommand();
        _startup.Disable();
        var status = await TryGetStatusAsync(cancellationToken);
        return new HostLifecycleResult(
            "disable-startup",
            false,
            status?.Running ?? false,
            status?.ProcessId,
            command.CommandLine);
    }

    public async Task<HostLifecycleResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        var status = await TryGetStatusAsync(cancellationToken);
        return status is null
            ? new HostLifecycleResult(
                "status",
                _startup.IsEnabled(),
                false,
                null,
                command.CommandLine)
            : Result("status", command, status);
    }

    private async Task<HostStatus?> TryGetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.CallAsync<object, HostStatus>(
                "host.status",
                new { },
                cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private HostLifecycleResult Result(
        string action,
        HostLaunchCommand command,
        HostStatus status) => new(
        action,
        _startup.IsEnabled(),
        status.Running,
        status.ProcessId,
        command.CommandLine);

    private Process StartDetached(HostLaunchCommand command)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _paths.DataDirectory
            };
            foreach (var prefixArgument in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            startInfo.ArgumentList.Add("host");
            startInfo.ArgumentList.Add("run");
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                WorkingDirectory = _paths.DataDirectory
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                "exec </dev/null >/dev/null 2>&1; executable=\"$1\"; shift; exec \"$executable\" \"$@\"");
            startInfo.ArgumentList.Add("agent-callback-host-launcher");
            startInfo.ArgumentList.Add(command.Executable);
            foreach (var prefixArgument in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            startInfo.ArgumentList.Add("host");
            startInfo.ArgumentList.Add("run");
        }

        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start Agent Callback Host.");
    }

    public static HostLaunchCommand ResolveCurrentCommand()
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The current executable path is unavailable.");
        var prefixArguments = new List<string>();
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var commandLineArguments = Environment.GetCommandLineArgs();
            var assemblyPath = commandLineArguments.Length > 0
                ? Path.GetFullPath(commandLineArguments[0])
                : null;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("The Agent Callback assembly path is unavailable.");
            }

            prefixArguments.Add(assemblyPath);
        }

        var parts = new List<string> { Quote(processPath) };
        parts.AddRange(prefixArguments.Select(Quote));
        parts.Add("host");
        parts.Add("run");
        return new HostLaunchCommand(processPath, prefixArguments, string.Join(" ", parts));
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record StopHostResult(bool StopRequested);
}
