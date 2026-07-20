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

    private static async Task<CallbackDeliveryResult> DeliverAsync(FakeCodexTransport transport)
    {
        var provider = new CodexSmartProvider(transport);
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
}
