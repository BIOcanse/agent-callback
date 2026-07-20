using System.Security.Cryptography;
using AgentCallback.Domain;
using AgentCallback.Infrastructure.Storage;
using AgentCallback.Triggers.Process;

namespace AgentCallback.Application;

public sealed class CallbackService
{
    private readonly ICallbackStore _store;
    private readonly IProcessInspector _processInspector;
    private readonly IAgentProviderRegistry _providers;

    public CallbackService(
        ICallbackStore store,
        IProcessInspector processInspector,
        IAgentProviderRegistry providers)
    {
        _store = store;
        _processInspector = processInspector;
        _providers = providers;
    }

    public async Task<RegisterCallbackResult> RegisterAsync(
        RegisterCallbackRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var providerName = RequireTrimmed(request.Provider, "Agent provider", 64).ToLowerInvariant();
        _providers.GetRequired(providerName);
        var targetThreadId = RequireTrimmed(request.TargetThreadId, "Target conversation id", 256);
        var instruction = RequireTrimmed(
            request.Instruction,
            "Continuation instruction",
            CallbackDefaults.MaxInstructionCharacters);
        var workingDirectory = NormalizeExistingDirectory(request.WorkingDirectory);
        var evidencePaths = NormalizeEvidencePaths(request.EvidencePaths);
        var label = OptionalTrimmed(request.Label, 128);
        var expectedCommandLine = OptionalTrimmed(request.ExpectedCommandLineContains, 512);
        var expiresUtc = request.ExpiresUtc ?? now.Add(CallbackDefaults.DefaultLifetime);
        if (expiresUtc <= now)
        {
            throw new InvalidOperationException("Callback expiration must be in the future.");
        }

        ProcessSnapshot? process = null;
        string? triggerSecret = null;
        byte[]? triggerHash = null;
        if (request.SourceKind == CallbackSourceKind.Process)
        {
            if (!request.ProcessId.HasValue || request.ProcessId.Value <= 0)
            {
                throw new InvalidOperationException("A positive process id is required for a process callback.");
            }

            process = _processInspector.TryGetSnapshot(request.ProcessId.Value) ??
                throw new InvalidOperationException($"Process {request.ProcessId.Value} is not running.");
            if (!string.IsNullOrWhiteSpace(expectedCommandLine) &&
                (string.IsNullOrWhiteSpace(process.CommandLine) ||
                 !process.CommandLine.Contains(expectedCommandLine, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Process {request.ProcessId.Value} command line does not contain the expected marker.");
            }
        }
        else
        {
            triggerSecret = CreateTriggerSecret();
            triggerHash = SHA256.HashData(Convert.FromBase64String(triggerSecret));
        }

        var callback = new CallbackRecord
        {
            CallbackId = $"acb_{Guid.NewGuid():N}",
            Label = label,
            Provider = providerName,
            TargetThreadId = targetThreadId,
            SourceKind = request.SourceKind,
            ProcessId = process?.ProcessId,
            ProcessCreationUtc = process?.CreationUtc,
            ProcessExecutablePath = process?.ExecutablePath,
            ProcessCommandLine = process?.CommandLine,
            ExpectedCommandLineContains = expectedCommandLine,
            WorkingDirectory = workingDirectory,
            Instruction = instruction,
            EvidencePaths = evidencePaths,
            DeliveryMode = CallbackDeliveryMode.Smart,
            State = request.SourceKind == CallbackSourceKind.Process
                ? CallbackState.Watching
                : CallbackState.Registered,
            ClientMessageId = Guid.NewGuid().ToString(),
            AttemptCount = 0,
            CreatedUtc = now,
            UpdatedUtc = now,
            ExpiresUtc = expiresUtc,
            Version = 1
        };

        await _store.CreateAsync(callback, triggerHash, cancellationToken);
        return new RegisterCallbackResult(callback, triggerSecret);
    }

    public async Task<CallbackRecord> TriggerAsync(
        TriggerCallbackRequest request,
        CancellationToken cancellationToken)
    {
        var callbackId = RequireTrimmed(request.CallbackId, "Callback id", 128);
        var callback = await RequireCallbackAsync(callbackId, cancellationToken);
        if (callback.SourceKind != CallbackSourceKind.Event)
        {
            throw new InvalidOperationException("Only event callbacks accept an explicit trigger.");
        }

        var expectedHash = await _store.GetTriggerSecretHashAsync(callbackId, cancellationToken) ??
            throw new InvalidOperationException("Callback has no trigger credential.");
        var actualHash = HashTriggerSecret(request.TriggerSecret);
        if (actualHash is null || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new UnauthorizedAccessException("Invalid callback trigger credential.");
        }

        if (callback.State is not (CallbackState.Registered or CallbackState.Watching))
        {
            return callback;
        }

        var observation = BuildObservation(request, callback);
        await _store.TryMarkReadyAsync(callbackId, observation, cancellationToken);
        return await RequireCallbackAsync(callbackId, cancellationToken);
    }

    public Task<CallbackRecord?> GetAsync(
        string callbackId,
        CancellationToken cancellationToken) =>
        _store.GetAsync(RequireTrimmed(callbackId, "Callback id", 128), cancellationToken);

    public Task<IReadOnlyList<CallbackRecord>> ListAsync(
        CallbackState? state,
        int limit,
        CancellationToken cancellationToken) =>
        _store.ListAsync(state, limit, cancellationToken);

    public async Task<CallbackRecord> CancelAsync(
        string callbackId,
        CancellationToken cancellationToken)
    {
        callbackId = RequireTrimmed(callbackId, "Callback id", 128);
        var current = await RequireCallbackAsync(callbackId, cancellationToken);
        if (current.State == CallbackState.Canceled)
        {
            return current;
        }

        if (!CallbackStates.CanCancel(current.State))
        {
            throw new InvalidOperationException(
                $"Callback {callbackId} cannot be canceled from state {current.State}.");
        }

        await _store.TryCancelAsync(callbackId, cancellationToken);
        return await RequireCallbackAsync(callbackId, cancellationToken);
    }

    public async Task<CallbackRecord> AcknowledgeAsync(
        string callbackId,
        CancellationToken cancellationToken)
    {
        callbackId = RequireTrimmed(callbackId, "Callback id", 128);
        var current = await RequireCallbackAsync(callbackId, cancellationToken);
        if (current.State == CallbackState.Acknowledged)
        {
            return current;
        }

        if (current.State != CallbackState.Delivered)
        {
            throw new InvalidOperationException(
                $"Callback {callbackId} can be acknowledged only after delivery.");
        }

        await _store.TryAcknowledgeAsync(callbackId, cancellationToken);
        return await RequireCallbackAsync(callbackId, cancellationToken);
    }

    public Task<bool> MarkProcessReadyAsync(
        CallbackRecord callback,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        if (callback.SourceKind != CallbackSourceKind.Process)
        {
            throw new InvalidOperationException("Only process callbacks can be completed by the process monitor.");
        }

        var outcome = exitCode switch
        {
            0 => "succeeded",
            null => "unknown",
            _ => "failed"
        };
        return _store.TryMarkReadyAsync(
            callback.CallbackId,
            new CompletionObservation(
                exitCode,
                outcome,
                "The registered process ended.",
                callback.EvidencePaths,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task<CallbackRecord> RequireCallbackAsync(
        string callbackId,
        CancellationToken cancellationToken) =>
        await _store.GetAsync(callbackId, cancellationToken) ??
        throw new KeyNotFoundException($"Callback not found: {callbackId}");

    private static CompletionObservation BuildObservation(
        TriggerCallbackRequest request,
        CallbackRecord callback)
    {
        var outcome = RequireTrimmed(request.ReportedOutcome, "Reported outcome", 32).ToLowerInvariant();
        if (outcome is not ("unknown" or "succeeded" or "failed" or "canceled"))
        {
            throw new InvalidOperationException(
                "Reported outcome must be unknown, succeeded, failed, or canceled.");
        }

        var summary = OptionalTrimmed(request.Summary, CallbackDefaults.MaxSummaryCharacters);
        var allowed = new HashSet<string>(callback.EvidencePaths, StringComparer.OrdinalIgnoreCase);
        var reportedEvidence = NormalizeEvidencePaths(request.EvidenceRefs);
        if (reportedEvidence.Any(path => !allowed.Contains(path)))
        {
            throw new InvalidOperationException(
                "Trigger evidence references must be declared when the callback is registered.");
        }

        return new CompletionObservation(
            request.ExitCode,
            outcome,
            summary,
            reportedEvidence,
            DateTimeOffset.UtcNow);
    }

    private static string CreateTriggerSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static byte[]? HashTriggerSecret(string value)
    {
        try
        {
            return SHA256.HashData(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string NormalizeExistingDirectory(string value)
    {
        var path = Path.GetFullPath(RequireTrimmed(value, "Working directory", 1024));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {path}");
        }

        return path;
    }

    private static IReadOnlyList<string> NormalizeEvidencePaths(IReadOnlyList<string> values)
    {
        if (values.Count > CallbackDefaults.MaxEvidencePaths)
        {
            throw new InvalidOperationException(
                $"At most {CallbackDefaults.MaxEvidencePaths} evidence paths are allowed.");
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RequireTrimmed(string value, string name, int maxLength)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new InvalidOperationException($"{name} exceeds {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? OptionalTrimmed(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new InvalidOperationException($"Value exceeds {maxLength} characters.");
        }

        return trimmed;
    }
}
