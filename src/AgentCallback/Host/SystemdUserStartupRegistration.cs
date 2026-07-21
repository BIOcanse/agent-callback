using System.Diagnostics;

namespace AgentCallback.Host;

public sealed class SystemdUserStartupRegistration : IStartupRegistration
{
    private const string ServiceName = "agent-callback.service";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var result = RunSystemctl(["is-enabled", "--quiet", ServiceName], throwOnFailure: false);
        return result.ExitCode == 0;
    }

    public void Enable(HostLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureLinux();
        RunSystemctl(["daemon-reload"], throwOnFailure: true);
        RunSystemctl(["enable", ServiceName], throwOnFailure: true);
    }

    public void Disable()
    {
        EnsureLinux();
        RunSystemctl(["disable", ServiceName], throwOnFailure: false);
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "systemd user startup is available only on Linux.");
        }
    }

    private static SystemctlResult RunSystemctl(
        IReadOnlyList<string> arguments,
        bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "systemctl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--user");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start systemctl.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the timeout and cancellation attempt.
                }

                throw new TimeoutException("systemctl --user did not finish within 10 seconds.");
            }

            Task.WaitAll([standardOutput, standardError], CommandTimeout);
            var result = new SystemctlResult(
                process.ExitCode,
                standardOutput.Result.Trim(),
                standardError.Result.Trim());
            if (throwOnFailure && result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    result.StandardError.Length > 0
                        ? result.StandardError
                        : $"systemctl --user exited with code {result.ExitCode}.");
            }

            return result;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "systemctl --user is unavailable; Host startup cannot be enabled automatically.",
                exception);
        }
    }

    private sealed record SystemctlResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
