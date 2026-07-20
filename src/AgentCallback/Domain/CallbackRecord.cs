namespace AgentCallback.Domain;

public sealed record CallbackRecord
{
    public required string CallbackId { get; init; }
    public string? Label { get; init; }
    public required string Provider { get; init; }
    public required string TargetThreadId { get; init; }
    public required CallbackSourceKind SourceKind { get; init; }
    public int? ProcessId { get; init; }
    public DateTimeOffset? ProcessCreationUtc { get; init; }
    public string? ProcessExecutablePath { get; init; }
    public string? ProcessCommandLine { get; init; }
    public string? ExpectedCommandLineContains { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Instruction { get; init; }
    public IReadOnlyList<string> EvidencePaths { get; init; } = [];
    public CallbackDeliveryMode DeliveryMode { get; init; } = CallbackDeliveryMode.Smart;
    public required CallbackState State { get; init; }
    public required string ClientMessageId { get; init; }
    public string? ReportedOutcome { get; init; }
    public int? ExitCode { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> ReportedEvidenceRefs { get; init; } = [];
    public int AttemptCount { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public DateTimeOffset? CompletionObservedUtc { get; init; }
    public DateTimeOffset? DeliveredUtc { get; init; }
    public DateTimeOffset? AcknowledgedUtc { get; init; }
    public string? DeliveryTransport { get; init; }
    public bool? AttachedToExisting { get; init; }
    public string? LastError { get; init; }
    public long Version { get; init; }
}

public sealed record RegisterCallbackRequest
{
    public string? Label { get; init; }
    public string Provider { get; init; } = "codex";
    public required string TargetThreadId { get; init; }
    public required CallbackSourceKind SourceKind { get; init; }
    public int? ProcessId { get; init; }
    public string? ExpectedCommandLineContains { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Instruction { get; init; }
    public IReadOnlyList<string> EvidencePaths { get; init; } = [];
    public DateTimeOffset? ExpiresUtc { get; init; }
}

public sealed record RegisterCallbackResult(
    CallbackRecord Callback,
    string? TriggerSecret);

public sealed record TriggerCallbackRequest
{
    public required string CallbackId { get; init; }
    public required string TriggerSecret { get; init; }
    public string ReportedOutcome { get; init; } = "unknown";
    public int? ExitCode { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
}

public sealed record CompletionObservation(
    int? ExitCode,
    string ReportedOutcome,
    string? Summary,
    IReadOnlyList<string> EvidenceRefs,
    DateTimeOffset ObservedUtc);
