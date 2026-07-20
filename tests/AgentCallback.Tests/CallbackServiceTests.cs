using System.Security.Cryptography;
using AgentCallback.Application;
using AgentCallback.Domain;
using AgentCallback.Triggers.Process;

namespace AgentCallback.Tests;

public sealed class CallbackServiceTests
{
    [Fact]
    public async Task Registration_RejectsUnknownProvider()
    {
        using var workspace = new TestWorkspace();
        var store = await workspace.CreateStoreAsync();
        var service = new CallbackService(
            store,
            new FakeProcessInspector(),
            CallbackTestData.Providers());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterAsync(
                new RegisterCallbackRequest
                {
                    Provider = "unknown-agent",
                    TargetThreadId = "conversation-test",
                    SourceKind = CallbackSourceKind.Event,
                    WorkingDirectory = workspace.DirectoryPath,
                    Instruction = "Continue after the event."
                },
                CancellationToken.None));

        Assert.Contains("Unsupported agent provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventTrigger_RequiresSecretAndIsIdempotent()
    {
        using var workspace = new TestWorkspace();
        var store = await workspace.CreateStoreAsync();
        var service = new CallbackService(
            store,
            new FakeProcessInspector(),
            CallbackTestData.Providers());
        var registration = await service.RegisterAsync(
            new RegisterCallbackRequest
            {
                TargetThreadId = "thread-test",
                SourceKind = CallbackSourceKind.Event,
                WorkingDirectory = workspace.DirectoryPath,
                Instruction = "Continue after the event."
            },
            CancellationToken.None);
        Assert.NotNull(registration.TriggerSecret);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TriggerAsync(
            new TriggerCallbackRequest
            {
                CallbackId = registration.Callback.CallbackId,
                TriggerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ReportedOutcome = "succeeded"
            },
            CancellationToken.None));

        var request = new TriggerCallbackRequest
        {
            CallbackId = registration.Callback.CallbackId,
            TriggerSecret = registration.TriggerSecret,
            ReportedOutcome = "succeeded",
            Summary = "event complete"
        };
        var first = await service.TriggerAsync(request, CancellationToken.None);
        var second = await service.TriggerAsync(request, CancellationToken.None);

        Assert.Equal(CallbackState.Ready, first.State);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal("event complete", first.Summary);
    }

    [Fact]
    public async Task ProcessRegistration_PinsPidCreationAndCommandMarker()
    {
        using var workspace = new TestWorkspace();
        var store = await workspace.CreateStoreAsync();
        var inspector = new FakeProcessInspector
        {
            Snapshot = new ProcessSnapshot(
                123,
                DateTimeOffset.UtcNow,
                Path.Combine(workspace.DirectoryPath, "worker.exe"),
                "worker.exe --job marker-42")
        };
        var service = new CallbackService(store, inspector, CallbackTestData.Providers());

        var registration = await service.RegisterAsync(
            new RegisterCallbackRequest
            {
                TargetThreadId = "thread-test",
                SourceKind = CallbackSourceKind.Process,
                ProcessId = 123,
                ExpectedCommandLineContains = "marker-42",
                WorkingDirectory = workspace.DirectoryPath,
                Instruction = "Continue after process exit."
            },
            CancellationToken.None);

        Assert.Equal(CallbackState.Watching, registration.Callback.State);
        Assert.Equal(inspector.Snapshot.CreationUtc, registration.Callback.ProcessCreationUtc);
        Assert.Null(registration.TriggerSecret);
    }
}
