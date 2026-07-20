# Agent Callback Progress

## Current State

Provider-neutral `0.1.0-alpha.3` is installed locally and has passed the OpenCode release gate. Codex compatibility still uses direct runtime probing with no package-version rejection.

## Completed

- Created the independent program root at `products/agent-callback`.
- Moved the authoritative product plan into `docs/product-plan.md`.
- Fixed the v0.1 boundary to durable one-shot callbacks only.
- Defined process-exit and explicit-event sources, pre-registration, smart Codex delivery, idempotency, ambiguous-delivery handling, and the App/Plugin/Skill split.
- Defined the planned source, test, plugin, packaging, artifact, and documentation layout.
- Selected Apache-2.0 for the open-source project and added the license at the project root.
- Completed the read-only M0 transport gate and recorded the v0.1 decision in `docs/transport-decision.md`.
- Implemented the .NET 10 Windows Host, per-user named-pipe protocol, SQLite persistence, DPAPI-protected instructions, process and event sources, dispatch leases, retries, and ambiguous-delivery recovery.
- Implemented the CLI, thin stdio MCP server, experimental Codex Desktop IPC provider, Plugin manifest, and callback Skill.
- Maintained 18 passing unit tests covering state transitions, restart recovery, trigger credentials and idempotency, PID identity capture, provider selection, MCP framing, smart-delivery race handling, OpenCode connection protection, target parsing, busy-session retry, and ambiguous prompt handling.
- Replaced the vulnerable SQLite native bundle with `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 while retaining `Microsoft.Data.Sqlite.Core` 10.0.9.
- Added and Windows PowerShell 5.1-validated `packaging/windows/publish.ps1`; it emits a self-contained `win-x64` single-file App, ZIP, executable digest, and release-side ZIP digest.
- Added per-user `host start|stop|status|enable-startup|disable-startup` lifecycle commands; the installer owns App installation and clean uninstallation.
- Passed the official Skill and Plugin validators.
- Passed Host/CLI smoke tests for registration, read, cancellation, lifecycle control, MCP initialize/tools-list, and a read-only Codex provider probe. No callback was triggered and no message was sent.
- Removed the alpha.1 Codex Desktop package-version gate, version detector, environment allowlist, and preflight delivery rejection. Provider status now probes the current IPC endpoint directly.
- Completed a real callback into the current active conversation: one event callback moved through `registered -> ready -> delivered -> acknowledged`, used the experimental Desktop IPC steer path once, and produced no duplicate or error.
- Removed the core hard-coded provider choice. Registration and dispatch now resolve named providers through `IAgentProviderRegistry`; Codex remains only the default bundled adapter.
- Added per-user installer/uninstaller packaging, Windows startup and Installed Apps registry entries, generated Skill installation metadata, checksum verification, and ownership-checked optional data removal.
- Verified install, upgrade with stable ownership ID, default uninstall with data preservation, full uninstall with explicit data removal, and final reinstall.
- Passed the final installed-package callback gate through the provider registry; the installed Skill resolved the App from its dynamic installation record, delivery attached to the existing task, and the callback was acknowledged.
- Published the public repository at `https://github.com/BIOcanse/agent-callback` and prerelease `v0.1.0-alpha.1` with the Windows x64 ZIP and SHA-256 sidecar.
- Rebuilt alpha.2 from commit `79b28d1`, upgraded the installed App and Skill while preserving their install ID and data, and verified the exact installed binary hash against the package.
- Completed an alpha.2 process callback through the version-independent provider path: the process identity marker matched, the idle follow-up delivered in one attempt with no transport error, and the callback was acknowledged. The record correctly reported `unknown` outcome and no exit code because an external process watcher only observes termination.
- Published and remotely verified prerelease `v0.1.0-alpha.2`; its tag points to `79b28d1` and the GitHub ZIP digest matches the local SHA-256 sidecar.
- Added the experimental OpenCode provider, DPAPI-protected connection registry, owned global plugin, exact environment target discovery, and a second installed Skill copy for OpenCode.
- Passed a real OpenCode Desktop callback through `opencode-http-plugin` in one attempt, verified its marker in the exact disposable session, acknowledged the callback, and deleted the test session.
- Verified plugin `shell.env` injection exposes the exact provider and instance/session target without model inference.
- Verified default uninstall removes the App, both Skill copies, the OpenCode plugin, startup value, and uninstall entry while preserving callback data and install ownership; reinstallation restored all assets with the same `installId`.
- Tested Claude Code CLI `2.1.215` in a disposable session. `--resume` retained the session ID, but the account returned HTTP 403, and the CLI/Desktop ownership model still does not provide in-place callback delivery.

## Follow-up

1. Add signing and broader provider/version compatibility in later releases.
2. Re-test per-user login startup in a disposable Windows user profile before promoting beyond alpha.

## Decisions

- The project is independent from Codex Thread Automation and must not depend on its 8787 API, runtime, global tasks, scheduler, Team system, rule system, or Bridge.
- Programs trigger a previously registered callback; they do not receive a general arbitrary-message endpoint.
- The default transport is local-only and must not expose a TCP listener.
- A transport timeout or disconnect after a possible write is ambiguous and must not be retried automatically.
- Provider-specific behavior is isolated behind `IAgentProviderRegistry`; alpha.3 ships Codex and OpenCode while the public name remains Agent Callback.
- The project license is Apache-2.0.
- V0.1 selects the experimental Codex Desktop IPC adapter on Windows; a separate public app-server process is not a write fallback for Desktop-owned conversations.
- Provider compatibility is determined by the current IPC probe and operation result, not by a package-version allowlist. Users may run the probe or an explicit test callback after an update.

## Blockers

- No implementation blocker.
- OpenCode support is complete for the alpha.3 scope: an owned global plugin discovers the exact instance/session and a provider adapter uses the public loopback HTTP API.
- Claude Code was investigated separately. Its CLI resume path is a second headless conversation owner and Claude Desktop history is separate, so it is not advertised as a callback provider.
- Code signing remains outside the alpha release boundary.
