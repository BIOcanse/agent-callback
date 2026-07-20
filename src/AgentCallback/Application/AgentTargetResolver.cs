namespace AgentCallback.Application;

public static class AgentTargetResolver
{
    public static string Resolve(string provider, string? explicitTarget)
    {
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            return explicitTarget;
        }

        var genericTarget = Environment.GetEnvironmentVariable("AGENT_CALLBACK_TARGET_ID");
        if (!string.IsNullOrWhiteSpace(genericTarget))
        {
            return genericTarget;
        }

        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var codexTarget = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if (!string.IsNullOrWhiteSpace(codexTarget))
            {
                return codexTarget;
            }
        }

        throw new InvalidOperationException(
            "--thread is required when the selected provider does not expose a callback target.");
    }
}
