using System.Security.Cryptography;
using System.Text;

namespace AgentCallback.Infrastructure;

public sealed record AppPaths(string DataDirectory, string DatabasePath, string PipeName)
{
    public string ProviderConnectionsPath =>
        Path.Combine(DataDirectory, "provider-connections.json");

    public static AppPaths Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_CALLBACK_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentCallback")
            : Path.GetFullPath(configured);

        var userIdentity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity)))[..16]
            .ToLowerInvariant();
        return new AppPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "callbacks.db"),
            $"agent-callback-{userHash}");
    }

    public void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
