# Contributing

Agent Callback keeps a deliberately narrow scope: durable one-shot callbacks into an already registered agent conversation.

Before submitting a change:

```powershell
dotnet format AgentCallback.slnx --verify-no-changes --no-restore
dotnet test AgentCallback.slnx
```

Provider contributions should implement `IAgentProvider`, declare a stable provider name, distinguish accepted/retryable/ambiguous/failed outcomes, and never retry a possible write through another provider or owner path.

Do not add scheduling, recurring jobs, arbitrary message sending, UI input injection, transcript mutation, or default network listeners to the core app.
