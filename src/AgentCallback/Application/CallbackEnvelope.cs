using AgentCallback.Domain;

namespace AgentCallback.Application;

public static class CallbackEnvelope
{
    public static string Build(CallbackRecord callback)
    {
        return $"""
            [agent-callback:v1:{callback.CallbackId}]
            A registered one-shot callback is ready. Use the agent-callback skill, load this callback record, verify its evidence, and continue this existing task.
            Stored continuation: {callback.Instruction}
            """;
    }
}
