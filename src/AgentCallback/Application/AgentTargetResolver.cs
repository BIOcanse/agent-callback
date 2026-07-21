namespace AgentCallback.Application;

using AgentCallback.Providers.Codex.AppServer;

public static class AgentTargetResolver
{
    public static string Resolve(string provider, string? explicitTarget)
    {
        string? target = null;
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            target = explicitTarget;
        }

        target ??= Environment.GetEnvironmentVariable("AGENT_CALLBACK_TARGET_ID");
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            target ??= Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if (!string.IsNullOrWhiteSpace(target) && OperatingSystem.IsLinux())
            {
                if (CodexAppServerTarget.TryParse(target, out _, out _))
                {
                    return target;
                }

                var connectionId = Environment.GetEnvironmentVariable(
                    "AGENT_CALLBACK_CODEX_CONNECTION_ID");
                if (!string.IsNullOrWhiteSpace(connectionId))
                {
                    var composite = CodexAppServerTarget.Format(connectionId, target);
                    if (CodexAppServerTarget.TryParse(composite, out _, out _))
                    {
                        return composite;
                    }
                }

                throw new InvalidOperationException(
                    "This Linux Codex session is not attached to a registered shared app-server. " +
                    "Launch it with 'agent-callback codex'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            return target;
        }

        throw new InvalidOperationException(
            "--thread is required when the selected provider does not expose a callback target.");
    }
}
