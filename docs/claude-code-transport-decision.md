# Claude Code transport decision

## Decision

Do not advertise a Claude Code provider yet.

Claude Code's official CLI can resume a local CLI session by ID, but `claude -p --resume` starts a separate headless client. It does not deliver a visible message to the existing terminal owner. Claude Desktop also keeps session history separate from the CLI, so the CLI cannot resume a Desktop conversation.

Writing transcript files, injecting terminal input, or using private Desktop IPC would violate Agent Callback's provider boundary and would be too fragile for durable delivery.

## Local verification

The official Windows Claude Code CLI `2.1.215` was installed through winget and tested in a disposable directory with an explicit session UUID and all tools disabled. The CLI accepted the session command shape, but the account returned an organization-level HTTP 403 before inference. This confirms that provider availability would also depend on independent Claude Code authorization; it does not change the transport decision.

## Revisit when

Add Claude Code only after Anthropic exposes an official API or hook that can append to the intended active conversation with an acknowledgement and an unambiguous session identity. A future CLI-only automation adapter must use a different product contract because it would continue work headlessly rather than merely deliver a callback.
