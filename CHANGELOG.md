# Changelog

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
