# Agent Callback Progress

## Current State

Provider-neutral `0.1.0-alpha.4` is released (superseded by `0.1.0-alpha.5`, 2026-10-03) for Windows x64 and Linux x64 and installed on both Windows and WSL 2. Windows Codex Desktop, native Linux Codex app-server, and native Linux OpenCode provider gates have passed; compatibility uses direct capability probing with no package-version rejection.

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
- Maintained 21 passing unit tests on both Windows and native Linux covering state transitions, restart recovery, trigger credentials and idempotency, PID identity capture, provider selection, MCP framing, smart-delivery race handling, provider connection protection, target parsing, busy-session retry, ambiguous prompt handling, Linux key permissions, and `/proc` identity capture.
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
- Reworked the public README into an agent-first English quick start and added a deterministic English demo graphic under `docs/assets`.
- Added a portable .NET 10 target, Linux `/proc` process inspector, case-sensitive path semantics, XDG state paths, Unix file-key AES-256-GCM secret protection, portable Host lifecycle, and a systemd user startup adapter.
- Added a native Linux Codex adapter using the official app-server protocol over a private Unix socket. The `agent-callback codex` launcher starts/probes the shared daemon, verifies the protocol endpoint, and launches the native TUI with an exact connection/thread target.
- Passed native WSL real-delivery gates for OpenCode event callbacks, Codex idle follow-ups, and process callbacks surviving a Host restart. Each accepted callback used one attempt; both provider markers were independently verified in the exact target conversation.
- Added ownership-checked Linux install/uninstall, dynamic Skill records, systemd startup, permission-preserving `tar.gz` packaging, default data preservation, explicit data removal, clean reinstall, and ownership-ID recovery from preserved data.
- Installed the final alpha.4 Windows package locally and revalidated the Host, HKCU startup registration, dynamic Skill metadata, and Codex Desktop IPC probe.
- Published prerelease `v0.1.0-alpha.4` with self-contained Windows x64 and Linux x64 archives, SHA-256 sidecars, permission-preserving Linux packaging, and remotely verified release assets.

- 2026-10-02 `0.1.0-alpha.5` (local Windows build and install): idle Codex threads were never reached. Two causes, both shown by an event callback that retried 412 times with `no-client-found`: (1) `thread-follower-start-turn` still used version 1 with `turnStartParams`, which Codex Desktop 26.901+ rejects; it now uses version 2 with `turnStart.request` and `inheritThreadSettings`, the same envelope Codex Thread Automation adopted in its 2026-09-06 repair; (2) no Desktop renderer owned the idle thread. Opening `codex://threads/<id>` let Desktop load it, and the next retry was accepted as a new turn (`attachedToExisting: false`). Active-thread steering was unaffected, which is why callbacks registered by a running Codex turn kept working.
- 2026-10-03 owner relocation (user approved): on `no-client-found`/`client-disconnected` the Codex provider opens `codex://threads/<id>`, waits five seconds and retries the delivery once, serialized; off with `AGENT_CALLBACK_CODEX_OWNER_RELOCATION=off`. 25 tests pass; both target frameworks build. Published as `v0.1.0-alpha.5`.

## Follow-up

1. Add signing, Linux ARM64, and broader provider compatibility in later releases.
2. Re-test per-user login startup in a disposable Windows user profile before promoting beyond alpha.
3. Re-check owner relocation after Codex Desktop updates: it depends on the `codex://threads/<id>` link and on the window registering as owner within five seconds.

## Decisions

- The project is independent from Codex Thread Automation and must not depend on its 8787 API, runtime, global tasks, scheduler, Team system, rule system, or Bridge.
- Programs trigger a previously registered callback; they do not receive a general arbitrary-message endpoint.
- The default transport is local-only and must not expose a TCP listener.
- A transport timeout or disconnect after a possible write is ambiguous and must not be retried automatically.
- Provider-specific behavior is isolated behind `IAgentProviderRegistry`; alpha.3 ships Codex and OpenCode while the public name remains Agent Callback.
- The project license is Apache-2.0.
- V0.1 selects the experimental Codex Desktop IPC adapter on Windows; a separate public app-server process is not a write fallback for Desktop-owned conversations.
- Provider compatibility is determined by the current IPC probe and operation result, not by a package-version allowlist. Users may run the probe or an explicit test callback after an update.
- On Linux, Codex callbacks require an exact shared app-server connection launched with `agent-callback codex`; private in-process TUI sessions are never guessed from recent transcripts.
- Linux state is per-user under XDG state storage, startup is a systemd user unit, and normal uninstall preserves callback history unless explicit ownership-checked removal is requested.

## Blockers

- No implementation blocker for alpha.4.
- OpenCode support is complete on Windows and Linux for the alpha scope: an owned global plugin discovers the exact instance/session and a provider adapter uses the public loopback HTTP API.
- Claude Code was investigated separately. Its CLI resume path is a second headless conversation owner and Claude Desktop history is separate, so it is not advertised as a callback provider.
- Code signing remains outside the alpha release boundary.
