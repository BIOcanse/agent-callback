namespace AgentCallback.Triggers.Process;

public sealed record ProcessSnapshot(
    int ProcessId,
    DateTimeOffset CreationUtc,
    string? ExecutablePath,
    string? CommandLine);

public interface IProcessInspector
{
    ProcessSnapshot? TryGetSnapshot(int processId);
}
