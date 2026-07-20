using AgentCallback.Domain;

namespace AgentCallback.Tests;

public sealed class SqliteCallbackStoreTests
{
    [Fact]
    public async Task ReadyDispatchDeliveredAcknowledged_IsIdempotentAtBoundaries()
    {
        using var workspace = new TestWorkspace();
        var store = await workspace.CreateStoreAsync();
        var callback = CallbackTestData.Event();
        await store.CreateAsync(callback, [1, 2, 3], CancellationToken.None);

        var observation = new CompletionObservation(
            0,
            "succeeded",
            "done",
            [],
            DateTimeOffset.UtcNow);
        Assert.True(await store.TryMarkReadyAsync(
            callback.CallbackId,
            observation,
            CancellationToken.None));
        Assert.False(await store.TryMarkReadyAsync(
            callback.CallbackId,
            observation,
            CancellationToken.None));
        Assert.True(await store.TryBeginDispatchAsync(
            callback.CallbackId,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.False(await store.TryBeginDispatchAsync(
            callback.CallbackId,
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        await store.CompleteDispatchAsync(
            callback.CallbackId,
            CallbackDeliveryResult.Accepted("fake", false, callback.ClientMessageId),
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None);
        Assert.True(await store.TryAcknowledgeAsync(callback.CallbackId, CancellationToken.None));
        Assert.False(await store.TryAcknowledgeAsync(callback.CallbackId, CancellationToken.None));

        var saved = await store.GetAsync(callback.CallbackId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(CallbackState.Acknowledged, saved.State);
        Assert.Equal("Inspect the result and continue.", saved.Instruction);
        Assert.Equal(1, saved.AttemptCount);
    }

    [Fact]
    public async Task Initialize_ConvertsInterruptedDispatchToAmbiguous()
    {
        using var workspace = new TestWorkspace();
        var store = await workspace.CreateStoreAsync();
        var callback = CallbackTestData.Event(CallbackState.Ready);
        await store.CreateAsync(callback, null, CancellationToken.None);
        Assert.True(await store.TryBeginDispatchAsync(
            callback.CallbackId,
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        var reopened = await workspace.CreateStoreAsync();
        var saved = await reopened.GetAsync(callback.CallbackId, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(CallbackState.Ambiguous, saved.State);
        Assert.Contains("restarted", saved.LastError, StringComparison.OrdinalIgnoreCase);
    }
}
