using System.Diagnostics;
using AgentCallback.Transport.NamedPipe;
using Microsoft.Win32;

namespace AgentCallback.Host;

public sealed record HostLifecycleResult(
    string Action,
    bool StartupEnabled,
    bool Running,
    int? ProcessId,
    string Command);

public sealed class HostLifecycle
{
    private const string StartupValueName = "AgentCallback";
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly HostPipeClient _client;

    public HostLifecycle(HostPipeClient client)
    {
        _client = client;
    }

    public async Task<HostLifecycleResult> StartAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        var current = await TryGetStatusAsync(cancellationToken);
        if (current is not null)
        {
            return Result("start", command, current);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var prefixArgument in command.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        startInfo.ArgumentList.Add("host");
        startInfo.ArgumentList.Add("run");
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start Agent Callback Host.");

        HostStatus? status = null;
        for (var attempt = 0; attempt < 5 && status is null; attempt++)
        {
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
                IsStartupEnabled(),
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
            IsStartupEnabled(),
            false,
            status.ProcessId,
            command.CommandLine);
    }

    public async Task<HostLifecycleResult> EnableStartupAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCurrentCommand();
        using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true) ??
            throw new InvalidOperationException("Could not open the current-user startup registry key.");
        key.SetValue(StartupValueName, command.CommandLine, RegistryValueKind.String);
        var started = await StartAsync(cancellationToken);
        return started with { Action = "enable-startup", StartupEnabled = true };
    }

    public async Task<HostLifecycleResult> DisableStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = ResolveCurrentCommand();
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true);
        key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
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
                IsStartupEnabled(),
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
        HostCommand command,
        HostStatus status) => new(
        action,
        IsStartupEnabled(),
        status.Running,
        status.ProcessId,
        command.CommandLine);

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
        return key?.GetValue(StartupValueName) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static HostCommand ResolveCurrentCommand()
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The current executable path is unavailable.");
        var prefixArguments = new List<string>();
        if (string.Equals(
                Path.GetFileName(processPath),
                "dotnet.exe",
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
        return new HostCommand(processPath, prefixArguments, string.Join(" ", parts));
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record HostCommand(
        string Executable,
        IReadOnlyList<string> PrefixArguments,
        string CommandLine);

    private sealed record StopHostResult(bool StopRequested);
}
