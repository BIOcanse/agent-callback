using AgentCallback.Domain;
using AgentCallback.Providers.Codex;

namespace AgentCallback.Tests;

public sealed class CodexSmartProviderTests
{
    [Fact]
    public async Task ActiveTask_SteersWithoutStartingTurn()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Accepted()]
        };
        var result = await DeliverAsync(transport);

        Assert.Equal(DeliveryOutcomeKind.Accepted, result.Kind);
        Assert.True(result.AttachedToExisting);
        Assert.Equal(1, transport.SteerCalls);
        Assert.Equal(0, transport.StartCalls);
    }

    [Fact]
    public async Task InactiveTask_StartsFollowUp()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("No active turn for conversation")],
            StartResults = [Accepted()]
        };
        var result = await DeliverAsync(transport);

        Assert.Equal(DeliveryOutcomeKind.Accepted, result.Kind);
        Assert.False(result.AttachedToExisting);
        Assert.Equal(1, transport.SteerCalls);
        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task UncertainSteer_DoesNotCrossToFollowUpPath()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("response timed out", uncertain: true)]
        };
        var result = await DeliverAsync(transport);

        Assert.Equal(DeliveryOutcomeKind.Ambiguous, result.Kind);
        Assert.Equal(0, transport.StartCalls);
    }

    [Fact]
    public async Task IdleToActiveRace_CorrectsOnceWithSteer()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("No active turn"), Accepted()],
            StartResults = [Rejected("conversation already has an active turn")]
        };
        var result = await DeliverAsync(transport);

        Assert.Equal(DeliveryOutcomeKind.Accepted, result.Kind);
        Assert.True(result.AttachedToExisting);
        Assert.Equal(2, transport.SteerCalls);
        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task MissingOwner_RelocatesAndRetriesOnce()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("no-client-found"), Rejected("No active turn")],
            StartResults = [Accepted()]
        };
        var relocator = new FakeOwnerRelocator(succeeds: true);
        var result = await DeliverAsync(transport, relocator);

        Assert.Equal(DeliveryOutcomeKind.Accepted, result.Kind);
        Assert.False(result.AttachedToExisting);
        Assert.Equal(1, relocator.Calls);
        Assert.Equal(2, transport.SteerCalls);
        Assert.Equal(1, transport.StartCalls);
    }

    [Fact]
    public async Task MissingOwner_StaysRetryableWhenRelocationFails()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("no-client-found")]
        };
        var relocator = new FakeOwnerRelocator(succeeds: false);
        var result = await DeliverAsync(transport, relocator);

        Assert.Equal(DeliveryOutcomeKind.Retryable, result.Kind);
        Assert.Equal(1, relocator.Calls);
        Assert.Equal(1, transport.SteerCalls);
    }

    [Fact]
    public async Task MissingOwner_RetriesOnlyOnce()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("no-client-found"), Rejected("no-client-found")]
        };
        var relocator = new FakeOwnerRelocator(succeeds: true);
        var result = await DeliverAsync(transport, relocator);

        Assert.Equal(DeliveryOutcomeKind.Retryable, result.Kind);
        Assert.Equal(1, relocator.Calls);
        Assert.Equal(2, transport.SteerCalls);
    }

    [Fact]
    public async Task OtherFailures_DoNotRelocate()
    {
        var transport = new FakeCodexTransport
        {
            SteerResults = [Rejected("response timed out", uncertain: true)]
        };
        var relocator = new FakeOwnerRelocator(succeeds: true);
        var result = await DeliverAsync(transport, relocator);

        Assert.Equal(DeliveryOutcomeKind.Ambiguous, result.Kind);
        Assert.Equal(0, relocator.Calls);
    }

    private static async Task<CallbackDeliveryResult> DeliverAsync(
        FakeCodexTransport transport,
        ICodexOwnerRelocator? relocator = null)
    {
        var provider = new CodexSmartProvider(transport, relocator);
        return await provider.DeliverAsync(
            CallbackTestData.Event(CallbackState.Ready),
            "callback envelope",
            CancellationToken.None);
    }

    private static CodexOperationResult Accepted() =>
        new(true, "client-message", "client", null, false);

    private static CodexOperationResult Rejected(string error, bool uncertain = false) =>
        new(false, "client-message", null, error, uncertain);

    private sealed class FakeCodexTransport : ICodexConversationTransport
    {
        private int _steerIndex;
        private int _startIndex;

        public string Name => "fake";
        public IReadOnlyList<CodexOperationResult> SteerResults { get; init; } = [];
        public IReadOnlyList<CodexOperationResult> StartResults { get; init; } = [];
        public int SteerCalls => _steerIndex;
        public int StartCalls => _startIndex;

        public Task<CodexTransportStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexTransportStatus(true, "client", null));

        public Task<CodexOperationResult> StartTurnAsync(
            string threadId,
            string message,
            string clientMessageId,
            CancellationToken cancellationToken) =>
            Task.FromResult(StartResults[_startIndex++]);

        public Task<CodexOperationResult> SteerTurnAsync(
            string threadId,
            string message,
            string workingDirectory,
            string clientMessageId,
            CancellationToken cancellationToken) =>
            Task.FromResult(SteerResults[_steerIndex++]);
    }

    private sealed class FakeOwnerRelocator(bool succeeds) : ICodexOwnerRelocator
    {
        public int Calls { get; private set; }

        public Task<bool> RelocateAsync(string threadId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(succeeds);
        }
    }
}
