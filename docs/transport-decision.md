# Codex Transport Decision

Date: 2026-07-20
Decision status: accepted for v0.1 alpha

## Decision

Use a narrowly extracted Codex Desktop IPC adapter for v0.1 on Windows and label it experimental. Keep the provider boundary small enough to replace this adapter when a supported shared Codex app-server route exists.

Do not use a separately launched public app-server process as a write fallback for a Codex Desktop-owned conversation.

## Evidence

- The installed Codex CLI is `0.140.0` and exposes the public app-server protocol, including `thread/resume`, `turn/start`, and `turn/steer`.
- The installed CLI exposes `app-server daemon` and `app-server proxy`, which would be the natural shared-server route.
- On this Windows installation, `codex app-server daemon version` fails with `codex app-server daemon lifecycle is only supported on Unix platforms`.
- A separate stdio app-server can own or resume Codex threads, but current evidence does not establish that it can safely share a Desktop-owned conversation, preserve Desktop-visible message synchronization, and avoid competing writers.
- The existing local Codex Thread Automation implementation has already verified the Desktop owner-client pipe for visible follow-up and steer delivery, including deterministic active/inactive correction and fail-closed ambiguous transport behavior.

## Consequences

- The first package target is Windows x64.
- The provider reports `experimental: true`, the selected transport, and compatibility diagnostics.
- The provider verifies the running Codex Desktop package version before every probe or write. The alpha allowlist starts with the locally verified package `26.715.4045.0`; unknown or undetectable versions fail closed.
- Only the Codex Desktop named-pipe write path is implemented. Bridge, UI automation, transcript mutation, terminal input, and a separate app-server writer are prohibited fallbacks.
- A transport timeout, disconnect, malformed response, or unknown post-write state is ambiguous and is never followed by a different write path.
- Public app-server support remains a future adapter or replacement, not an automatic retry route.
- Alpha release notes must state that a Codex Desktop update may break the experimental adapter.

## Recheck Conditions

Re-run this decision when any of the following changes:

- Codex publishes a supported Windows shared app-server daemon/control socket;
- Codex documents safe multi-client access to the Desktop-owned conversation;
- Codex exposes a public Desktop plugin/tool gateway for follow-up and steer;
- the private Desktop IPC probe fails after a Codex Desktop update.

## Verification Boundary

This decision used read-only CLI help, version, named-pipe availability, public protocol documentation, prior local delivery evidence, and an independent read-only app-server transport audit. The audit confirmed that public `resume/start/steer` primitives do not provide a supported shared owner endpoint for the Desktop-owned stdio app-server on Windows. No test message was injected into the current user task during this decision.

After explicit approval, `0.1.0-alpha.1` completed one real event callback into the current active conversation. The callback was accepted once through `thread-follower-steer-turn`, recorded `attachedToExisting: true`, reached `delivered`, was visibly received, and was then acknowledged. This validates the active-turn path for Desktop package `26.715.4045.0`; the idle follow-up path remains covered by unit tests rather than a second real message test.
