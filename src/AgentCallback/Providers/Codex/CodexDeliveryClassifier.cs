namespace AgentCallback.Providers.Codex;

public static class CodexDeliveryClassifier
{
    public static bool IsDeterministicInactive(string? error) => ContainsAny(
        error,
        "SteerTurnInactiveError",
        "active turn already ended",
        "not being streamed",
        "Conversation state not found",
        "no active turn",
        "turn is not in progress");

    public static bool IsDeterministicActiveConflict(string? error) => ContainsAny(
        error,
        "already has an active turn",
        "active turn is in progress",
        "turn already in progress",
        "conversation is being streamed");

    public static bool IsRetryableOwnerUnavailable(string? error) => ContainsAny(
        error,
        "no-client-found",
        "native owner unavailable",
        "pipe has not been connected",
        "could not connect",
        "all pipe instances are busy",
        "the system cannot find the file specified");

    private static bool ContainsAny(string? value, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
