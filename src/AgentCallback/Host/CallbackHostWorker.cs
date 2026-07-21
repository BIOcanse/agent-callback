using AgentCallback.Application;
using AgentCallback.Domain;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Triggers.Process;

namespace AgentCallback.Host;

public sealed class CallbackHostWorker : IAsyncDisposable
{
    private const int BatchSize = 100;
    private readonly ICallbackStore _store;
    private readonly CallbackService _service;
    private readonly IProcessInspector _processInspector;
    private readonly IAgentProviderRegistry _providers;
    private readonly Dictionary<string, System.Diagnostics.Process> _processHandles = [];

    public CallbackHostWorker(
        ICallbackStore store,
        CallbackService service,
        IProcessInspector processInspector,
        IAgentProviderRegistry providers)
    {
        _store = store;
        _service = service;
        _processInspector = processInspector;
        _providers = providers;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await _store.ExpireDueAsync(now, cancellationToken);
                await ObserveProcessesAsync(cancellationToken);
                await DispatchAsync(now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Agent Callback worker iteration failed: {exception}");
            }

            try
            {
                await Task.Delay(CallbackDefaults.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ObserveProcessesAsync(CancellationToken cancellationToken)
    {
        var callbacks = await _store.ListWatchingAsync(BatchSize, cancellationToken);
        foreach (var callback in callbacks)
        {
            if (!callback.ProcessId.HasValue || !callback.ProcessCreationUtc.HasValue)
            {
                await _store.TryMarkFailedAsync(
                    callback.CallbackId,
                    "Process callback is missing its registered process identity.",
                    cancellationToken);
                DisposeHandle(callback.CallbackId);
                continue;
            }

            var snapshot = _processInspector.TryGetSnapshot(callback.ProcessId.Value);
            if (snapshot is null)
            {
                var exitCode = TryReadExitCode(callback.CallbackId);
                await _service.MarkProcessReadyAsync(callback, exitCode, cancellationToken);
                DisposeHandle(callback.CallbackId);
                continue;
            }

            if (!IdentityMatches(callback, snapshot))
            {
                await _store.TryMarkFailedAsync(
                    callback.CallbackId,
                    $"PID {callback.ProcessId.Value} was reused or no longer matches the registered process.",
                    cancellationToken);
                DisposeHandle(callback.CallbackId);
                continue;
            }

            AttachHandle(callback);
            if (HasExited(callback.CallbackId))
            {
                var exitCode = TryReadExitCode(callback.CallbackId);
                await _service.MarkProcessReadyAsync(callback, exitCode, cancellationToken);
                DisposeHandle(callback.CallbackId);
            }
        }
    }

    private async Task DispatchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var callbacks = await _store.ListDispatchableAsync(now, BatchSize, cancellationToken);
        foreach (var candidate in callbacks)
        {
            if (!await _store.TryBeginDispatchAsync(candidate.CallbackId, now, cancellationToken))
            {
                continue;
            }

            var callback = await _store.GetAsync(candidate.CallbackId, cancellationToken) ??
                throw new InvalidOperationException(
                    $"Callback disappeared after dispatch lease: {candidate.CallbackId}");
            CallbackDeliveryResult result;
            try
            {
                var provider = _providers.GetRequired(callback.Provider);
                result = await provider.DeliverAsync(
                    callback,
                    CallbackEnvelope.Build(callback),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = CallbackDeliveryResult.Ambiguous(
                    "provider-exception",
                    callback.ClientMessageId,
                    $"Provider threw while delivery state was unknown: {exception.Message}");
            }

            var completedUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? nextAttemptUtc = null;
            if (result.Kind == DeliveryOutcomeKind.Retryable)
            {
                var cannotRetry = callback.AttemptCount >= CallbackDefaults.MaxDispatchAttempts ||
                    (callback.ExpiresUtc.HasValue && callback.ExpiresUtc.Value <= completedUtc);
                if (cannotRetry)
                {
                    result = CallbackDeliveryResult.Failed(
                        result.Transport,
                        callback.ClientMessageId,
                        result.Error ?? "Callback delivery retry limit was reached.");
                }
                else
                {
                    nextAttemptUtc = completedUtc.Add(
                        CallbackDefaults.RetryDelay(callback.AttemptCount));
                }
            }

            await _store.CompleteDispatchAsync(
                callback.CallbackId,
                result,
                completedUtc,
                nextAttemptUtc,
                cancellationToken);
        }
    }

    private static bool IdentityMatches(CallbackRecord callback, ProcessSnapshot snapshot)
    {
        var creationDelta = (snapshot.CreationUtc - callback.ProcessCreationUtc!.Value).Duration();
        if (creationDelta > TimeSpan.FromSeconds(1))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(callback.ProcessExecutablePath) &&
            !string.IsNullOrWhiteSpace(snapshot.ExecutablePath) &&
            !string.Equals(
                Path.GetFullPath(callback.ProcessExecutablePath),
                Path.GetFullPath(snapshot.ExecutablePath),
                PathSemantics.Comparison))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(callback.ExpectedCommandLineContains) ||
            (!string.IsNullOrWhiteSpace(snapshot.CommandLine) &&
             snapshot.CommandLine.Contains(
                 callback.ExpectedCommandLineContains,
                 PathSemantics.Comparison));
    }

    private void AttachHandle(CallbackRecord callback)
    {
        if (_processHandles.ContainsKey(callback.CallbackId) || !callback.ProcessId.HasValue)
        {
            return;
        }

        try
        {
            _processHandles.Add(
                callback.CallbackId,
                System.Diagnostics.Process.GetProcessById(callback.ProcessId.Value));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The next poll reconciles the process through IProcessInspector.
        }
    }

    private bool HasExited(string callbackId)
    {
        try
        {
            return _processHandles.TryGetValue(callbackId, out var process) && process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private int? TryReadExitCode(string callbackId)
    {
        try
        {
            return _processHandles.TryGetValue(callbackId, out var process) && process.HasExited
                ? process.ExitCode
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void DisposeHandle(string callbackId)
    {
        if (_processHandles.Remove(callbackId, out var process))
        {
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var process in _processHandles.Values)
        {
            process.Dispose();
        }

        _processHandles.Clear();
        return ValueTask.CompletedTask;
    }
}
