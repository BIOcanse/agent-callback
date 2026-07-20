namespace AgentCallback.Application;

public static class CallbackDefaults
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public const int MaxInstructionCharacters = 64 * 1024;
    public const int MaxSummaryCharacters = 2 * 1024;
    public const int MaxEvidencePaths = 32;
    public const int MaxDispatchAttempts = 1440;

    public static TimeSpan RetryDelay(int attemptCount)
    {
        var seconds = attemptCount switch
        {
            <= 1 => 2,
            2 => 5,
            3 => 15,
            4 => 30,
            _ => 60
        };
        return TimeSpan.FromSeconds(seconds);
    }
}
