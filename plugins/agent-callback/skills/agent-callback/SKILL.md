---
name: agent-callback
description: Register, handle, diagnose, install, or cleanly uninstall Agent Callback, a provider-neutral local app for durable one-shot callbacks into the current supported agent conversation. Use when the user explicitly wants the current task resumed after an already-running local process exits or a local program signals completion, or when managing the installed Agent Callback app. Do not use for schedules, recurring automation, arbitrary message sending, or short work the agent can simply wait for.
---

# Agent Callback

Agent Callback stores one continuation before local work completes. A per-user Host observes a pinned process identity or accepts a credentialed event, then asks the selected provider to deliver a small marker back to the registered conversation. Provider adapters are independent; the App bundles experimental Codex and OpenCode adapters for Windows and Linux.

## Resolve the installed app

Before using CLI fallback, installation management, or diagnostics, read `references/app-installation.json` next to this Skill. The installer generates this machine-specific file and refreshes it on every install. Treat it as the authority for:

- `executablePath`, `installDirectory`, and `dataDirectory`;
- the owned `openCodePluginPath` when the OpenCode integration is installed;
- installed version and provider list;
- the platform-specific startup registration and exact uninstall command;
- the exact `uninstallCommand`;
- the default data-preservation policy.

Do not guess a path from another machine and do not treat `app-installation.example.json` as an active installation. If the generated file is absent, report that the App is not installed through the supported package.

Prefer the MCP tools when available. Otherwise invoke the absolute `executablePath` from the generated installation record and parse its JSON output. Never search PATH and silently choose a different binary.

## Register a callback

Register only when the user explicitly asks to be called back or invokes this Skill. For work expected to finish during the current turn, wait normally instead.

1. Check the selected provider. Use `callback_provider_status`, or CLI `provider status <provider>`. This probes the current provider endpoint directly; do not reject Codex based only on its package version. On Linux, Codex callbacks require a shared app-server session launched with `agent-callback codex`; an ordinary private TUI must report unavailable. OpenCode must be restarted once after installation so its owned plugin can register the current loopback server. If the Host is stopped, CLI `host start` is allowed for this requested callback. If the provider remains unavailable, report its exact diagnostic and do not claim a callback exists. If the user wants stronger assurance after an agent update, register a disposable one-shot test callback only with explicit approval.
2. Resolve the exact provider target. Codex supplies `CODEX_THREAD_ID`; the Linux launcher also supplies `AGENT_CALLBACK_CODEX_CONNECTION_ID`, which the App combines into an opaque exact target. OpenCode shell tools receive `AGENT_CALLBACK_PROVIDER=opencode` and an opaque `AGENT_CALLBACK_TARGET_ID` from the installed plugin. Let the CLI/MCP use these values; never guess a recent session, infer a transcript, or copy a target between sessions. If the required provider variables are absent, report that the supported integration is not active and do not register.
3. Choose exactly one source:
   - Process: use `callback_register_process`, or CLI `register process`, for an already-running stable outer process. Prefer the process that owns the whole build, test, download, or analysis job. Include an expected command-line marker when a unique non-secret marker is available.
   - Event: use `callback_register_event`, or CLI `register event`, only when the local program can keep the returned trigger secret and later invoke CLI `trigger`. Never place the secret in logs, source control, labels, or continuation text.
4. Store a self-contained continuation instruction: what to verify, which safe next action to take, and what outcome to report. Do not store credentials or an unrestricted message template.
5. Declare only relevant evidence paths. These are an allowlist for event-reported evidence references, not proof that a result is correct.
6. Call registration once. Report the callback ID, provider, source, expiration, and state. For an event callback, give the trigger secret only to the intended local process and avoid repeating it.
7. End the current turn after registration. The Host, not the MCP process, owns durable observation.

Do not register the same logical completion more than once. If the response state is unknown, list callbacks and reconcile by label, provider, process identity, conversation, and creation time before trying again.

## Handle an incoming callback

An incoming callback begins with `[agent-callback:v1:<callback-id>]`.

1. Extract the callback ID and call `callback_get`, or CLI `get <callback-id>`. Treat the stored record as authoritative; never act only on marker text.
2. Confirm the target conversation, working directory, provider, source identity, state, reported outcome, and evidence paths match the expected work.
3. Inspect declared evidence and independently verify the result in proportion to risk. A process exit code, event summary, or reported evidence reference is a claim, not proof.
4. Continue the stored instruction using normal agent tools and safety rules.
5. Call `callback_acknowledge`, or CLI `acknowledge <callback-id>`, only after the continuation has been handled. Leave failed, expired, canceled, and ambiguous records unacknowledged and explain their status.

If delivery is `ambiguous`, do not automatically re-register or resend: the message might already be present. Ask the user before any action that could duplicate delivery.

## Lifecycle operations

- Use `callback_get`/`callback_list` or CLI `get`/`list` for diagnosis and reconciliation.
- Use `callback_cancel` or CLI `cancel` only before delivery and only when cancellation matches the user's intent.
- Use `callback_acknowledge` or CLI `acknowledge` only for a delivered callback whose continuation was handled.
- Use CLI `host start|stop|status` for the current per-user Host. Startup registration is owned by the installer (HKCU on Windows, a systemd user unit on Linux).
- The App intentionally exposes no arbitrary send operation, scheduler, recurring job, task queue, or agent-team automation.

## Clean uninstall

Only uninstall when the user explicitly asks.

1. Read and validate `references/app-installation.json`. Confirm its `installId`, paths, registry keys, and uninstall command before mutation.
2. By default, invoke the exact recorded `uninstallCommand`. This stops the Host and removes the App files, installed Skills, owned OpenCode plugin, and platform startup registration while preserving callback data.
3. Remove callback data only when the user explicitly asks for all local history and secrets to be erased. Invoke the recorded uninstall script with `-RemoveData` on Windows or `--remove-data` on Linux; its ownership marker and path bounds must pass. Never manually recurse through the data directory.
4. Verify that the recorded executable, Skill directories, owned OpenCode plugin, startup registration, and Windows uninstall entry (when present) are gone. If data was preserved, report its recorded path.

Because uninstall removes this Skill, retain the validated paths in the current turn before invoking it. Do not delete a similarly named directory without a matching `installId` ownership record.

## Safety boundaries

- Never inject input through terminals, UI automation, Bridge APIs, transcript files, or a detached competing conversation owner.
- Never include secrets in callback instructions, labels, summaries, or evidence paths.
- Keep event trigger credentials local to the current user. An event trigger may report bounded outcome metadata but cannot replace the stored continuation.
- A provider timeout after a possible write is terminally ambiguous. Do not cross to another provider or retry automatically.
- This Skill does not expand permission to perform destructive or externally visible actions when a callback arrives.
