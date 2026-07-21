using System.Security.Cryptography;
using System.Text;

namespace AgentCallback.Infrastructure;

public sealed record AppPaths(string DataDirectory, string DatabasePath, string PipeName)
{
    public string ProviderConnectionsPath =>
        Path.Combine(DataDirectory, "provider-connections.json");

    public string CodexConnectionsPath =>
        Path.Combine(DataDirectory, "codex-connections.json");

    public string MasterKeyPath => Path.Combine(DataDirectory, "master.key");

    public string HostMutexName => OperatingSystem.IsWindows()
        ? $"Local\\{PipeName}-host"
        : $"{PipeName}-host";

    public static AppPaths Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_CALLBACK_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(configured)
            ? ResolveDefaultDataDirectory()
            : Path.GetFullPath(configured);

        var userIdentity = OperatingSystem.IsWindows()
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : $"{Environment.UserName}|{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        userIdentity += $"|{Path.GetFullPath(dataDirectory)}";
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity)))[..16]
            .ToLowerInvariant();
        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "callbacks.db"),
            $"agent-callback-{userHash}");
    }

    public void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                DataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string ResolveDefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentCallback");
        }

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException(
                    "HOME is required when XDG_STATE_HOME is not configured.");
            }

            stateHome = Path.Combine(home, ".local", "state");
        }

        return Path.GetFullPath(Path.Combine(stateHome, "agent-callback"));
    }
}
