using System.Security.Cryptography;
using System.Text;
using AgentCallback.Infrastructure.Security;
using AgentCallback.Providers.Codex.AppServer;
using AgentCallback.Triggers.Process;

namespace AgentCallback.Tests;

public sealed class LinuxPlatformTests
{
    [Fact]
    public void ProcessInspectorPinsCurrentProcessIdentityOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var snapshot = new LinuxProcessInspector().TryGetSnapshot(Environment.ProcessId);

        Assert.NotNull(snapshot);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.InRange(
            snapshot.CreationUtc,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CommandLine));
    }

    [Fact]
    public void FileKeyProtectorRoundTripsAndUsesPrivatePermissionsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        byte[] protectedValue;
        using (var protector = new FileKeySecretProtector(workspace.Paths))
        {
            protectedValue = protector.Protect("test-secret");
            Assert.Equal("test-secret", protector.Unprotect(protectedValue));
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(workspace.Paths.MasterKeyPath));
        Assert.DoesNotContain(
            "test-secret",
            Encoding.UTF8.GetString(protectedValue),
            StringComparison.Ordinal);

        using var reopened = new FileKeySecretProtector(workspace.Paths);
        Assert.Equal("test-secret", reopened.Unprotect(protectedValue));
        CryptographicOperations.ZeroMemory(protectedValue);
    }

    [Fact]
    public async Task CodexConnectionRegistryRoundTripsExactUnixSocketOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var store = new CodexAppServerConnectionStore(workspace.Paths);
        var socketPath = Path.Combine(workspace.DirectoryPath, "codex.sock");

        var result = await store.UpsertAsync(
            new CodexAppServerConnectionRegistration(
                socketPath,
                "test",
                Environment.ProcessId),
            CancellationToken.None);
        var connection = await store.GetAsync(
            result.ConnectionId,
            CancellationToken.None);

        Assert.NotNull(connection);
        Assert.Equal("codex", result.Provider);
        Assert.StartsWith("cx_", result.ConnectionId, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(socketPath), connection.SocketPath);
        Assert.Equal(Environment.ProcessId, connection.ProcessId);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(workspace.Paths.CodexConnectionsPath));

        var target = CodexAppServerTarget.Format(
            result.ConnectionId,
            "019f807b-3566-7bf0-a822-b155036b026c");
        Assert.True(CodexAppServerTarget.TryParse(
            target,
            out var connectionId,
            out var threadId));
        Assert.Equal(result.ConnectionId, connectionId);
        Assert.Equal("019f807b-3566-7bf0-a822-b155036b026c", threadId);
    }
}
