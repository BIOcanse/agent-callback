# Changelog

## Unreleased

## 0.1.0-alpha.4 - 2026-07-20

- Added a native self-contained Linux x64 build with shared provider-neutral core behavior.
- Added Linux `/proc` process identity inspection, XDG state paths, AES-256-GCM secret protection with `0700`/`0600` filesystem boundaries, and a systemd user Host service.
- Added the experimental Codex CLI shared app-server adapter over a private Unix socket and the `agent-callback codex` launcher; ordinary private Codex TUI sessions remain intentionally unavailable.
- Extended the OpenCode plugin and loopback provider to Linux and passed a real one-shot delivery into an exact native Linux session.
- Added ownership-checked Linux install/uninstall scripts, dynamic Skill installation metadata, and a permission-preserving `tar.gz` release package.
- Reworked the English README around a plain-language quick start and added an English visual walkthrough.

## 0.1.0-alpha.3 - 2026-07-20

- Added an experimental OpenCode provider that uses the public loopback server API.
- Added an owned global OpenCode plugin that registers the current server securely and injects the exact instance/session callback target into shell tools.
- Protected OpenCode server credentials with Windows DPAPI and kept them out of command-line arguments and logs.
- Extended install metadata and ownership-checked uninstall to cover the OpenCode plugin.
- Investigated Claude Code CLI and Desktop transports; documented why neither currently meets the in-place callback contract.

## 0.1.0-alpha.2 - 2026-07-20

- Removed the Codex Desktop package-version allowlist and all preflight version rejection.
- Changed provider availability checks to use the actual Desktop IPC handshake, independent of the detected package version.
- Removed the Windows package-version detector; `System.Management` remains only for process callback identity inspection.
- Kept the Codex adapter experimental and retained fail-closed handling for ambiguous post-write transport failures.

## 0.1.0-alpha.1 - 2026-07-20

- Added provider-neutral callback state, Provider Registry, process and event sources, SQLite persistence, DPAPI instruction protection, and per-user named-pipe Host protocol.
- Added CLI, stdio MCP tools, bundled Skill, and an experimental version-gated Codex Desktop provider.
- Added fail-closed ambiguous-delivery handling, process identity pinning, event trigger credentials, bounded evidence references, and restart recovery.
- Added a self-contained Windows x64 package with per-user install, startup, Windows Installed Apps registration, dynamic Skill installation metadata, and ownership-checked clean uninstall.
- Validated one real active-turn callback on Codex Desktop package `26.715.4045.0`.
