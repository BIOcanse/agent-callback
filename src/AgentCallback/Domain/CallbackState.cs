namespace AgentCallback.Domain;

public enum CallbackState
{
    Registered,
    Watching,
    Ready,
    Dispatching,
    Retryable,
    Delivered,
    Acknowledged,
    Ambiguous,
    Failed,
    Canceled,
    Expired
}
public enum CallbackSourceKind
{
    Process,
    Event
}

public enum CallbackDeliveryMode
{
    Smart
}

public static class CallbackStates
{
    public static bool IsTerminal(CallbackState state) => state is
        CallbackState.Acknowledged or
        CallbackState.Ambiguous or
        CallbackState.Failed or
        CallbackState.Canceled or
        CallbackState.Expired;

    public static bool CanCancel(CallbackState state) => state is
        CallbackState.Registered or
        CallbackState.Watching or
        CallbackState.Ready or
        CallbackState.Retryable;
}
