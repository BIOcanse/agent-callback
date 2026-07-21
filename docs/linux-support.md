# Linux Support Contract

## Decision

Ship Agent Callback as a native Linux application, beginning with self-contained `linux-x64` packages tested in WSL 2. Linux must not invoke the Windows executable or depend on a Windows-side Host. The provider-neutral callback core, SQLite state, CLI, stdio MCP server, process/event sources, and OpenCode provider remain shared with Windows.

Codex Desktop delivery remains Windows-only because that adapter targets a Desktop-owned private Windows named-pipe endpoint. Linux Codex CLI delivery uses the official Codex app-server protocol instead: a Codex TUI connected to a shared local app-server exposes the same thread through `thread/resume`, `turn/start`, and `turn/steer`. Agent Callback never probes an unrelated Unix pipe or launches a competing `codex exec resume` owner.

The Linux package includes an explicit Codex callback launch path that starts/probes Codex's local remote-control app-server and launches the TUI against its default user-private Unix socket. Commands created inside that session receive the official `CODEX_THREAD_ID`; registration also records the exact app-server connection. A normally launched, private Codex TUI remains unsupported because no external client can safely steer its in-process owner. Provider status must distinguish these modes clearly.

## Platform boundaries

The application targets portable .NET 10. Platform selection happens once during composition and is isolated behind narrow contracts:

| Area | Windows | Linux |
| --- | --- | --- |
| Process identity | `Process.StartTime`, executable metadata, and WMI command line | `/proc/<pid>/stat` boot-relative start ticks, `/proc/<pid>/exe`, and `/proc/<pid>/cmdline` |
| Secret protection | Current-user Windows DPAPI | AES-256-GCM with a random application key stored as `0600` inside a `0700` data directory |
| Per-user IPC | .NET named pipe with `CurrentUserOnly` | .NET Unix named-pipe transport with `CurrentUserOnly` |
| Single Host guard | Per-user named mutex | Per-user named mutex without a Windows namespace prefix |
| Session start/stop | Detached process plus optional HKCU Run startup | Detached process plus optional systemd user service |
| Codex provider | Experimental Desktop IPC adapter | Official app-server over a user-private Unix socket when the TUI uses shared-server mode |
| OpenCode provider | Owned plugin and public loopback HTTP API | Same owned plugin and public loopback HTTP API |

The Linux file-key protector does not claim to defend against a process already running as the same Unix user. Its boundary matches the rest of the per-user application: it prevents disclosure to other users through filesystem permissions and provides authenticated encryption at rest. The key is never printed, placed in install metadata, or passed on a command line.

## Linux filesystem layout

Default locations follow XDG conventions and remain configurable:

```text
~/.local/lib/agent-callback/                 # owned application and uninstaller
~/.local/bin/agent-callback                  # owned launcher symlink
${XDG_STATE_HOME:-~/.local/state}/agent-callback/
  callbacks.db
  provider-connections.json
  codex-connections.json
  master.key                                 # mode 0600
~/.config/systemd/user/agent-callback.service
~/.codex/skills/agent-callback/
~/.config/opencode/skills/agent-callback/
~/.config/opencode/plugins/agent-callback.js
```

The installer records the resolved paths, platform, installed providers, startup mechanism, service unit, launcher, and exact uninstall command in each installed Skill's generated `references/app-installation.json`. Normal uninstall removes only files carrying the matching install ownership ID and preserves callback data. Data removal remains an explicit separate option.

## Host lifecycle

`host start`, `stop`, and `status` work without systemd. A Linux package also installs a per-user systemd unit when `systemctl --user` is usable. `host enable-startup` and `disable-startup` manage that owned unit; an environment without a functioning user manager still supports on-demand and installer-started Hosts and reports startup as unavailable rather than editing shell profiles.

The installer must not use `sudo`, install a system-wide service, change `/etc`, or expose a public listener.

## Codex CLI callback mode

Codex CLI is native on Linux, but executable availability alone is not proof that an external callback can reach the active owner. The supported mode is:

1. start or probe the official Codex remote-control/app-server daemon (local socket health is checked separately from optional cloud remote-control pairing state);
2. connect the interactive TUI with `codex --remote unix://`;
3. preserve the injected `CODEX_THREAD_ID` and register the exact app-server connection with Agent Callback;
4. on completion, initialize an app-server client, resume the exact thread, steer if a turn is active, or start a follow-up if it is idle;
5. apply the same ambiguous-write rule as every other provider transport.

Agent Callback may provide a small launcher command for steps 1-2. It must not replace the user's normal Codex installation, copy authentication, read or mutate transcript files, select a recent session heuristically, or fall back to a second headless `codex exec` process.

## Packaging

`packaging/linux/publish.ps1` produces:

- a self-contained `linux-x64` executable;
- `install.sh` and `uninstall.sh`;
- the provider-neutral Skill and OpenCode plugin template;
- license, README, changelog, security policy, and executable checksum;
- `agent-callback-<version>-linux-x64.tar.gz` plus a SHA-256 sidecar.

The archive preserves executable bits. Publishing and installation use bounded explicit paths and do not recursively follow symbolic links.

## Acceptance criteria

Linux support is complete only when all of the following pass:

1. The solution builds and unit tests pass on both Windows and Linux from the same portable target framework.
2. A native Linux self-contained executable reports the expected version and platform without a Windows runtime dependency.
3. Host start/status/stop works through the per-user IPC transport in WSL.
4. A Linux process callback pins process identity, becomes ready only after the exact process exits, and survives a Host restart.
5. A Linux event callback authenticates its secret, treats an exact replay idempotently without redelivery, and preserves bounded outcome metadata.
6. The Linux key file is `0600`, the data directory is inaccessible to group/other users, and encrypted instructions round-trip after Host restart.
7. OpenCode connection registration, exact target resolution, busy-session handling, and one-shot delivery pass through the public loopback API on Linux.
8. A Codex CLI TUI launched in shared app-server mode receives a process/event callback in the exact thread; a private TUI returns an intentional unavailable diagnostic and never probes a Unix pipe named `codex-ipc`.
9. Default uninstall removes the App, launcher, owned Skills, owned plugin, and service unit while preserving data; explicit data removal is ownership-checked and path-bounded.
10. Existing Windows tests, packaging, install metadata, and Codex/OpenCode behavior remain valid.

## Initial release boundary

The first Linux artifact is `linux-x64`, validated on the installed WSL 2 distribution. Native ARM64 packaging, `.deb`/`.rpm`, system-wide installation, and GUI installers are later compatibility work, not hidden fallbacks in the first package.
