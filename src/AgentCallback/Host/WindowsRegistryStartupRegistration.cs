#if WINDOWS
using Microsoft.Win32;

namespace AgentCallback.Host;

public sealed class WindowsRegistryStartupRegistration : IStartupRegistration
{
    private const string StartupValueName = "AgentCallback";
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
        return key?.GetValue(StartupValueName) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    public void Enable(HostLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true) ??
            throw new InvalidOperationException(
                "Could not open the current-user startup registry key.");
        key.SetValue(StartupValueName, command.CommandLine, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true);
        key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
    }
}
#endif
