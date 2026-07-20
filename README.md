# Agent Callback

Agent Callback is a small, provider-neutral Windows app for durable, one-shot callbacks into an existing agent conversation.

Register a continuation before a local process or program finishes. The per-user Host observes completion and asks the selected provider to return a short callback marker to the original conversation. A trigger cannot replace the stored instruction or choose a new target.

## Status

The current public release is `0.1.0-alpha.2` for Windows x64. The core, storage, Host, CLI, MCP protocol, process/event triggers, installer, and Skill are provider-neutral. The bundled Codex Desktop provider is the first adapter and remains explicitly experimental.

This project is not affiliated with or endorsed by OpenAI or any agent vendor.

## Install

Download `agent-callback-0.1.0-alpha.2-win-x64.zip` and its `.sha256` sidecar from [GitHub Releases](https://github.com/BIOcanse/agent-callback/releases), verify the archive, extract it, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

The installer requires no administrator privileges. It installs:

- App: `%LOCALAPPDATA%\Programs\AgentCallback`
- callback data: `%LOCALAPPDATA%\AgentCallback`
- Codex Skill: `%USERPROFILE%\.codex\skills\agent-callback`
- per-user startup value: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AgentCallback`
- Windows uninstall entry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentCallback`

The installed Skill receives a generated `references/app-installation.json` containing the exact executable, data, registry, provider, and uninstall locations for that machine. This lets an agent diagnose or cleanly uninstall the App without guessing paths.

After installation, restart Codex so it discovers the Skill. The Host starts automatically for the current session and at the next user login.

## Uninstall

Use Windows **Installed apps**, or run the exact `uninstallCommand` recorded by the installed Skill.

Normal uninstall removes the App, Skill, startup entry, and uninstall entry but preserves callback history. To explicitly erase callback data too:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\AgentCallback\uninstall.ps1" -RemoveData
```

Data removal is ownership-checked, path-bounded, and never implied by a normal uninstall.

## Providers

| Provider | Status | Delivery |
| --- | --- | --- |
| Codex Desktop on Windows | Experimental | Active-turn steer or idle follow-up through a directly probed Desktop IPC adapter |

The adapter does not reject a Codex Desktop package based on its version number. It attempts the current IPC handshake and operation directly, then reports the actual runtime result. Run `agent-callback provider status codex` for a no-message connectivity probe; if stronger assurance is needed after an update, explicitly register a disposable one-shot callback and verify its delivery.

Additional agent providers should implement the narrow `IAgentProvider` boundary and register by name. Callback persistence and trigger handling do not depend on Codex.

## Safety boundary

- One callback, one stored continuation, one registered conversation.
- Process callbacks pin PID plus creation time to reject PID reuse.
- Event triggers use a per-callback secret and may report only bounded result metadata.
- A possible-write timeout is terminally ambiguous and is not retried through another path.
- No arbitrary send-message API, scheduler, recurring jobs, agent teams, UI automation, transcript mutation, or TCP listener.
- Stored continuation instructions are protected with Windows DPAPI for the current user.

## CLI

```powershell
agent-callback host start
agent-callback provider status codex
agent-callback register process --provider codex --pid 1234 --instruction "Verify the result and continue."
agent-callback register event --provider codex --instruction "Verify the result and continue."
agent-callback get <callback-id>
agent-callback list
agent-callback cancel <callback-id>
agent-callback acknowledge <callback-id>
agent-callback version
```

`CODEX_THREAD_ID` supplies the default conversation target for the bundled Codex Skill. Other providers can define their own target discovery in their integration layer.

## Build and test

Requires .NET SDK 10 on Windows:

```powershell
dotnet test AgentCallback.slnx
dotnet format AgentCallback.slnx --verify-no-changes --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File packaging\windows\publish.ps1
```

The publish script creates a self-contained release directory and ZIP under `artifacts/`, including the App, installer, uninstaller, Skill, license, README, executable checksum, and a release-side ZIP checksum.

Architecture and transport details are in [docs/product-plan.md](docs/product-plan.md) and [docs/transport-decision.md](docs/transport-decision.md).

## License

Apache-2.0. See [LICENSE](LICENSE).
