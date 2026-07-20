using AgentCallback.Domain;
using AgentCallback.Providers.OpenCode;

namespace AgentCallback.Tests;

public sealed class OpenCodeProviderTests
{
    [Fact]
    public async Task DeliversToIdleSession()
    {
        var transport = new FakeOpenCodeTransport();
        var provider = CreateProvider(transport);

        var result = await provider.DeliverAsync(
            Callback(),
            "continue",
            CancellationToken.None);

        Assert.Equal(DeliveryOutcomeKind.Accepted, result.Kind);
        Assert.False(result.AttachedToExisting);
        Assert.Equal("ses_test", transport.PromptedSessionId);
        Assert.Equal("continue", transport.PromptedMessage);
    }

    [Fact]
    public async Task BusySessionIsRetryableWithoutPrompt()
    {
        var transport = new FakeOpenCodeTransport
        {
            Session = new OpenCodeSessionProbe(true, true, false, null)
        };
        var provider = CreateProvider(transport);

        var result = await provider.DeliverAsync(
            Callback(),
            "continue",
            CancellationToken.None);

        Assert.Equal(DeliveryOutcomeKind.Retryable, result.Kind);
        Assert.Null(transport.PromptedMessage);
    }

    [Fact]
    public async Task PromptTimeoutIsAmbiguous()
    {
        var transport = new FakeOpenCodeTransport
        {
            Prompt = new OpenCodePromptResult(false, false, true, "timeout")
        };
        var provider = CreateProvider(transport);

        var result = await provider.DeliverAsync(
            Callback(),
            "continue",
            CancellationToken.None);

        Assert.Equal(DeliveryOutcomeKind.Ambiguous, result.Kind);
        Assert.Equal("timeout", result.Error);
    }

    private static OpenCodeProvider CreateProvider(FakeOpenCodeTransport transport)
    {
        var connection = new OpenCodeConnection(
            "oc_0123456789abcdef",
            "http://127.0.0.1:4096",
            "opencode",
            "secret",
            "test",
            42,
            DateTimeOffset.UtcNow);
        return new OpenCodeProvider(
            new FakeOpenCodeConnectionStore(connection),
            transport);
    }

    private static CallbackRecord Callback() =>
        CallbackTestData.Event() with
        {
            Provider = "opencode",
            TargetThreadId = "oc_0123456789abcdef:ses_test"
        };

    private sealed class FakeOpenCodeConnectionStore : IOpenCodeConnectionStore
    {
        private readonly OpenCodeConnection _connection;

        public FakeOpenCodeConnectionStore(OpenCodeConnection connection)
        {
            _connection = connection;
        }

        public Task<OpenCodeConnectionResult> UpsertAsync(
            OpenCodeConnectionRegistration registration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OpenCodeConnection?> GetAsync(
            string connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenCodeConnection?>(
                connectionId == _connection.ConnectionId ? _connection : null);

        public Task<IReadOnlyList<OpenCodeConnection>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenCodeConnection>>([_connection]);
    }

    private sealed class FakeOpenCodeTransport : IOpenCodeConversationTransport
    {
        public string Name => "test-opencode";
        public OpenCodeProbe Probe { get; set; } = new(true, null);
        public OpenCodeSessionProbe Session { get; set; } = new(true, false, false, null);
        public OpenCodePromptResult Prompt { get; set; } = new(true, false, false, null);
        public string? PromptedSessionId { get; private set; }
        public string? PromptedMessage { get; private set; }

        public Task<OpenCodeProbe> ProbeAsync(
            OpenCodeConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(Probe);

        public Task<OpenCodeSessionProbe> ProbeSessionAsync(
            OpenCodeConnection connection,
            string sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Session);

        public Task<OpenCodePromptResult> PromptAsync(
            OpenCodeConnection connection,
            string sessionId,
            string message,
            CancellationToken cancellationToken)
        {
            PromptedSessionId = sessionId;
            PromptedMessage = message;
            return Task.FromResult(Prompt);
        }
    }
}
