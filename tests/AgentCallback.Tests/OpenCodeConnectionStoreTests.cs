using AgentCallback.Providers.OpenCode;

namespace AgentCallback.Tests;

public sealed class OpenCodeConnectionStoreTests
{
    [Fact]
    public async Task UpsertNormalizesAndProtectsConnection()
    {
        using var workspace = new TestWorkspace();
        using var store = new OpenCodeConnectionStore(
            workspace.Paths,
            new PlaintextTestProtector());

        var result = await store.UpsertAsync(
            new OpenCodeConnectionRegistration(
                "http://127.0.0.1:4096/",
                null,
                "super-secret",
                "test",
                42),
            CancellationToken.None);
        var connection = await store.GetAsync(result.ConnectionId, CancellationToken.None);

        Assert.NotNull(connection);
        Assert.Equal("opencode", result.Provider);
        Assert.StartsWith("oc_", result.ConnectionId, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:4096", connection.ServerUrl);
        Assert.Equal("opencode", connection.Username);
        Assert.Equal("super-secret", connection.Password);
        Assert.Equal(42, connection.ProcessId);

        var stored = await File.ReadAllTextAsync(workspace.Paths.ProviderConnectionsPath);
        Assert.DoesNotContain("super-secret", stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com:4096")]
    [InlineData("file:///tmp/opencode")]
    [InlineData("http://127.0.0.1:4096/path")]
    public async Task UpsertRejectsNonLoopbackOrigin(string serverUrl)
    {
        using var workspace = new TestWorkspace();
        using var store = new OpenCodeConnectionStore(
            workspace.Paths,
            new PlaintextTestProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(
            new OpenCodeConnectionRegistration(serverUrl, null, "", "test", null),
            CancellationToken.None));
    }

    [Fact]
    public void TargetRoundTrips()
    {
        var value = OpenCodeTarget.Format(
            "oc_0123456789abcdef",
            "ses_0123456789abcdef");

        var parsed = OpenCodeTarget.TryParse(value, out var connectionId, out var sessionId);

        Assert.True(parsed);
        Assert.Equal("oc_0123456789abcdef", connectionId);
        Assert.Equal("ses_0123456789abcdef", sessionId);
    }
}
