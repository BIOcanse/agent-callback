#if WINDOWS
using System.Diagnostics;
using System.Management;

namespace AgentCallback.Triggers.Process;

public sealed class WindowsProcessInspector : IProcessInspector
{
    public ProcessSnapshot? TryGetSnapshot(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        System.Diagnostics.Process? process = null;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
            var creationUtc = process.StartTime.ToUniversalTime();
            var executablePath = TryGetExecutablePath(process);
            var commandLine = TryGetCommandLine(processId, out var managementExecutablePath);
            return new ProcessSnapshot(
                processId,
                creationUtc,
                executablePath ?? managementExecutablePath,
                commandLine);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? TryGetExecutablePath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return process.ProcessName;
        }
        catch (NotSupportedException)
        {
            return process.ProcessName;
        }
    }

    private static string? TryGetCommandLine(int processId, out string? executablePath)
    {
        executablePath = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    executablePath = result["ExecutablePath"] as string;
                    return result["CommandLine"] as string;
                }
            }
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
#endif
