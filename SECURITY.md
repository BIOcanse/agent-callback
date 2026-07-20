# Security Policy

## Supported version

Only the latest published alpha is supported. Provider adapters may have a narrower compatibility allowlist than the provider's own supported versions.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository. Please do not open a public issue for trigger-secret exposure, local privilege boundary failures, path traversal, unsafe uninstall behavior, or callback delivery ambiguity.

Include the Agent Callback version, Windows version, provider and provider version, reproduction steps, and whether a callback may have been delivered.

## Security model

- Local named pipes are restricted to the current Windows user.
- Continuation instructions are encrypted with current-user DPAPI.
- Event trigger credentials are stored only as hashes.
- Provider writes fail closed after ambiguous transport outcomes.
- Uninstall data deletion requires an explicit option plus matching ownership and bounded-path checks.
