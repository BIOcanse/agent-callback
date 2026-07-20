# Agent Callback Progress

## Current Goal

Publish the provider-neutral `0.1.0-alpha.1` repository and Windows release package.

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
- Added thirteen passing unit tests covering state transitions, restart recovery, trigger credentials and idempotency, PID identity capture, provider selection, MCP framing, compatibility gating, and smart-delivery race handling.
- Replaced the vulnerable SQLite native bundle with `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 while retaining `Microsoft.Data.Sqlite.Core` 10.0.9.
- Added and Windows PowerShell 5.1-validated `packaging/windows/publish.ps1`; it emits a self-contained `win-x64` single-file App, ZIP, executable digest, and release-side ZIP digest.
- Added per-user `host start|stop|status|enable-startup|disable-startup` lifecycle commands; the installer owns App installation and clean uninstallation.
- Passed the official Skill and Plugin validators.
- Passed Host/CLI smoke tests for registration, read, cancellation, lifecycle control, MCP initialize/tools-list, and a read-only Codex provider probe. No callback was triggered and no message was sent.
- Added a fail-closed Codex Desktop package-version gate with verified default version `26.715.4045.0`.
- Completed a real callback into the current active conversation: one event callback moved through `registered -> ready -> delivered -> acknowledged`, used the experimental Desktop IPC steer path once, and produced no duplicate or error.
- Removed the core hard-coded provider choice. Registration and dispatch now resolve named providers through `IAgentProviderRegistry`; Codex remains only the default bundled adapter.
- Added per-user installer/uninstaller packaging, Windows startup and Installed Apps registry entries, generated Skill installation metadata, checksum verification, and ownership-checked optional data removal.
- Verified install, upgrade with stable ownership ID, default uninstall with data preservation, full uninstall with explicit data removal, and final reinstall.
- Passed the final installed-package callback gate through the provider registry; the installed Skill resolved the App from its dynamic installation record, delivery attached to the existing task, and the callback was acknowledged.

## In Progress

- Complete final repository hygiene, package verification, and public GitHub release.

## Next

1. Create the public repository under the verified BIOcanse developer account.
2. Publish `v0.1.0-alpha.1` with the self-contained Windows x64 ZIP and checksums.
3. Add signing and broader provider/version compatibility in later releases.
4. Re-test per-user login startup in a disposable Windows user profile before promoting beyond alpha.

## Decisions

- The project is independent from Codex Thread Automation and must not depend on its 8787 API, runtime, global tasks, scheduler, Team system, rule system, or Bridge.
- Programs trigger a previously registered callback; they do not receive a general arbitrary-message endpoint.
- The default transport is local-only and must not expose a TCP listener.
- A transport timeout or disconnect after a possible write is ambiguous and must not be retried automatically.
- Provider-specific behavior is isolated behind `IAgentProviderRegistry`; v0.1 ships Codex only, while the public name remains Agent Callback.
- The project license is Apache-2.0.
- V0.1 selects the experimental Codex Desktop IPC adapter on Windows; a separate public app-server process is not a write fallback for Desktop-owned conversations.

## Blockers

- No implementation blocker.
- Code signing and a second provider are outside the alpha release boundary.
