using System.Text;
using AgentCallback.Domain;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Security;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Triggers.Process;

namespace AgentCallback.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "agent-callback-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        Paths = new AppPaths(
            DirectoryPath,
            Path.Combine(DirectoryPath, "callbacks.db"),
            $"agent-callback-test-{Guid.NewGuid():N}");
    }

    public string DirectoryPath { get; }
    public AppPaths Paths { get; }

    public async Task<SqliteCallbackStore> CreateStoreAsync()
    {
        var store = new SqliteCallbackStore(Paths, new PlaintextTestProtector());
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(DirectoryPath))
        {
            File.Delete(file);
        }

        Directory.Delete(DirectoryPath);
    }
}

internal sealed class PlaintextTestProtector : ISecretProtector
{
    public byte[] Protect(string value) => Encoding.UTF8.GetBytes(value);

    public string Unprotect(byte[] value) => Encoding.UTF8.GetString(value);
}

internal sealed class FakeProcessInspector : IProcessInspector
{
    public ProcessSnapshot? Snapshot { get; set; }

    public ProcessSnapshot? TryGetSnapshot(int processId) =>
        Snapshot is { ProcessId: var snapshotId } && snapshotId == processId ? Snapshot : null;
}

internal static class CallbackTestData
{
    public static IAgentProviderRegistry Providers() =>
        new AgentProviderRegistry([new NoopAgentProvider()]);

    public static CallbackRecord Event(
        CallbackState state = CallbackState.Registered,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new CallbackRecord
        {
            CallbackId = $"acb_{Guid.NewGuid():N}",
            Provider = "codex",
            TargetThreadId = "thread-test",
            SourceKind = CallbackSourceKind.Event,
            WorkingDirectory = Path.GetTempPath(),
            Instruction = "Inspect the result and continue.",
            State = state,
            ClientMessageId = Guid.NewGuid().ToString(),
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp,
            ExpiresUtc = timestamp.AddHours(1),
            Version = 1
        };
    }

    private sealed class NoopAgentProvider : IAgentProvider
    {
        public string Name => "codex";

        public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderStatus(Name, true, true, "test", "test", null));

        public Task<CallbackDeliveryResult> DeliverAsync(
            CallbackRecord callback,
            string message,
            CancellationToken cancellationToken) =>
            Task.FromResult(CallbackDeliveryResult.Accepted(
                "test",
                false,
                callback.ClientMessageId));
    }
}
