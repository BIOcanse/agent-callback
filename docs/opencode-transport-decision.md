# OpenCode transport decision

## Decision

Ship OpenCode as the second Agent Callback provider on Windows. Use OpenCode's public plugin hooks to discover the current session and its public HTTP server API to append the callback prompt. Do not inspect Electron memory, renderer IPC, terminal buffers, or transcript databases.

## Installed integration

The Agent Callback installer places one owned global plugin at `~/.config/opencode/plugins/agent-callback.js`. At OpenCode startup the plugin:

1. receives the current server URL from the documented plugin context;
2. passes the loopback URL and Basic-auth credential to Agent Callback over stdin;
3. lets the Host protect the credential with Windows DPAPI;
4. injects `AGENT_CALLBACK_PROVIDER=opencode` and an opaque instance/session target into shell tools through the documented `shell.env` hook.

The plugin never sends a callback itself. It only registers the current OpenCode connection and exposes the exact callback target to commands executed for that session.

## Delivery

The provider resolves the registered connection, verifies the target session through the public server API, and posts the stored callback marker to `POST /session/:id/prompt_async`. It only targets loopback HTTP servers registered by the plugin.

If the session is busy, the provider waits through the existing durable retry path instead of injecting a competing prompt. A timeout or connection loss after a possible POST is ambiguous and is never retried automatically.

## Ownership and uninstall

The generated plugin contains the installation ID. Upgrade and uninstall mutate it only when that ID matches the App's installation record. Uninstall removes the owned plugin file and leaves unrelated OpenCode configuration intact.

## Compatibility boundary

The adapter uses public OpenCode plugin and server contracts. OpenCode Desktop's random server password is accepted only from the plugin process; Agent Callback does not scrape or infer it. A newly installed plugin takes effect after OpenCode restarts.
