#!/usr/bin/env bash
set -euo pipefail

version="${AGENT_CALLBACK_VERSION:-0.1.0-alpha.5}"
start_host=1
if [[ "${1:-}" == "--do-not-start-host" ]]; then
  start_host=0
elif [[ $# -ne 0 ]]; then
  printf 'Usage: bash install.sh [--do-not-start-host]\n' >&2
  exit 2
fi

source_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
user_home="${HOME:?HOME is required}"
install_root="$user_home/.local/lib/agent-callback"
bin_root="$user_home/.local/bin"
bin_link="$bin_root/agent-callback"
codex_skill="$user_home/.codex/skills/agent-callback"
opencode_root="$user_home/.config/opencode"
opencode_skill="$opencode_root/skills/agent-callback"
opencode_plugin="$opencode_root/plugins/agent-callback.js"
systemd_root="$user_home/.config/systemd/user"
systemd_unit="$systemd_root/agent-callback.service"
state_root="${XDG_STATE_HOME:-$user_home/.local/state}/agent-callback"
owner_path="$state_root/install-owner.json"

for value in "$install_root" "$bin_link" "$codex_skill" "$opencode_skill" \
  "$opencode_plugin" "$systemd_unit" "$state_root"; do
  case "$value" in
    *$'\n'*|*$'\r'*|*'"'*|*'\'*|*'|'*)
      printf 'Unsupported character in installation path: %s\n' "$value" >&2
      exit 1
      ;;
  esac
done

source_binary="$source_root/agent-callback"
source_uninstaller="$source_root/uninstall.sh"
source_skill="$source_root/skill/agent-callback"
source_plugin="$source_root/opencode/agent-callback.js"
source_checksums="$source_root/SHA256SUMS.txt"
for required in "$source_binary" "$source_uninstaller" "$source_checksums" \
  "$source_skill/SKILL.md" "$source_skill/agents/openai.yaml" "$source_plugin"; do
  [[ -f "$required" ]] || {
    printf 'Release package is incomplete: %s\n' "$required" >&2
    exit 1
  }
done

(cd -- "$source_root" && sha256sum --check --strict SHA256SUMS.txt)

existing_id=""
if [[ -d "$install_root" && ! -f "$install_root/install-id" &&
      -n "$(find "$install_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  printf 'Existing install directory has no Agent Callback ownership marker: %s\n' \
    "$install_root" >&2
  exit 1
fi
if [[ -f "$install_root/install-id" ]]; then
  existing_id="$(tr -d '\r\n' < "$install_root/install-id")"
elif [[ -f "$owner_path" ]]; then
  existing_id="$(sed -n 's/.*"installId": "\([^"]*\)".*/\1/p' "$owner_path")"
fi
install_id="${existing_id:-$(tr -d '\r\n' < /proc/sys/kernel/random/uuid)}"
[[ "$install_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  printf 'Existing Agent Callback install ID is invalid.\n' >&2
  exit 1
}

assert_owned_skill() {
  local directory="$1"
  local record="$directory/references/app-installation.json"
  [[ -d "$directory" ]] || return 0
  [[ -z "$(find "$directory" -mindepth 1 -maxdepth 1 -print -quit)" ]] && return 0
  [[ -f "$record" ]] &&
    grep -Fq "\"installId\": \"$install_id\"" "$record" || {
      printf 'Existing Skill is not owned by this installation: %s\n' "$directory" >&2
      exit 1
    }
}

assert_owned_skill "$codex_skill"
assert_owned_skill "$opencode_skill"
if [[ -e "$bin_link" || -L "$bin_link" ]]; then
  [[ -L "$bin_link" && "$(readlink -- "$bin_link")" == "$install_root/agent-callback" ]] || {
    printf 'Existing command path is not owned by this installation: %s\n' "$bin_link" >&2
    exit 1
  }
fi
if [[ -f "$systemd_unit" ]]; then
  grep -Fq "$install_root/agent-callback" "$systemd_unit" || {
    printf 'Existing systemd unit is not owned by this installation: %s\n' \
      "$systemd_unit" >&2
    exit 1
  }
fi

if [[ -x "$install_root/agent-callback" ]]; then
  "$install_root/agent-callback" host stop >/dev/null 2>&1 || true
fi

if [[ -f "$opencode_plugin" ]]; then
  grep -Fq "const managedInstallId = \"$install_id\"" "$opencode_plugin" || {
    printf 'Existing OpenCode plugin is not owned by this installation: %s\n' \
      "$opencode_plugin" >&2
    exit 1
  }
fi

mkdir -p -- "$install_root" "$bin_root" "$codex_skill/agents" \
  "$codex_skill/references" "$opencode_skill/agents" \
  "$opencode_skill/references" "$(dirname -- "$opencode_plugin")" \
  "$systemd_root" "$state_root"
chmod 700 -- "$state_root"

data_owned=false
if [[ -f "$owner_path" ]]; then
  grep -Fq "\"installId\": \"$install_id\"" "$owner_path" || {
    printf 'Existing data ownership marker does not match this installation.\n' >&2
    exit 1
  }
  data_owned=true
elif [[ -z "$(find "$state_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  data_owned=true
  printf '{\n  "schemaVersion": 1,\n  "installId": "%s"\n}\n' \
    "$install_id" > "$owner_path"
  chmod 600 -- "$owner_path"
fi

install -m 0755 -- "$source_binary" "$install_root/agent-callback"
install -m 0755 -- "$source_uninstaller" "$install_root/uninstall.sh"
install -m 0644 -- "$source_root/LICENSE" "$install_root/LICENSE"
install -m 0644 -- "$source_root/README.md" "$install_root/README.md"
printf '%s\n' "$install_id" > "$install_root/install-id"
chmod 600 -- "$install_root/install-id"
ln -sfn -- "$install_root/agent-callback" "$bin_link"

install -m 0644 -- "$source_skill/SKILL.md" "$codex_skill/SKILL.md"
install -m 0644 -- "$source_skill/agents/openai.yaml" \
  "$codex_skill/agents/openai.yaml"
install -m 0644 -- "$source_skill/SKILL.md" "$opencode_skill/SKILL.md"
install -m 0644 -- "$source_skill/agents/openai.yaml" \
  "$opencode_skill/agents/openai.yaml"
if [[ -f "$source_skill/references/app-installation.example.json" ]]; then
  install -m 0644 -- "$source_skill/references/app-installation.example.json" \
    "$codex_skill/references/app-installation.example.json"
  install -m 0644 -- "$source_skill/references/app-installation.example.json" \
    "$opencode_skill/references/app-installation.example.json"
fi

sed -e "s|__AGENT_CALLBACK_EXECUTABLE_JSON__|\"$install_root/agent-callback\"|g" \
  -e "s|__AGENT_CALLBACK_INSTALL_ID_JSON__|\"$install_id\"|g" \
  "$source_plugin" > "$opencode_plugin"
chmod 600 -- "$opencode_plugin"

cat > "$systemd_unit" <<EOF
# AgentCallbackInstallId=$install_id
[Unit]
Description=Agent Callback Host
After=default.target

[Service]
Type=simple
Environment="AGENT_CALLBACK_DATA_DIR=$state_root"
ExecStart="$install_root/agent-callback" host run
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
EOF
chmod 644 -- "$systemd_unit"

uninstall_command="$install_root/uninstall.sh"
write_installation_record() {
  local destination="$1"
  cat > "$destination" <<EOF
{
  "schemaVersion": 1,
  "installId": "$install_id",
  "displayName": "Agent Callback",
  "version": "$version",
  "platform": "linux-x64",
  "executablePath": "$install_root/agent-callback",
  "commandPath": "$bin_link",
  "installDirectory": "$install_root",
  "dataDirectory": "$state_root",
  "dataOwned": $data_owned,
  "skillDirectory": "$codex_skill",
  "openCodeSkillDirectory": "$opencode_skill",
  "openCodePluginPath": "$opencode_plugin",
  "startupUnitPath": "$systemd_unit",
  "startupUnitName": "agent-callback.service",
  "uninstallCommand": "$uninstall_command",
  "installedProviders": ["codex", "opencode"],
  "providerNotes": {
    "codex": "Experimental native Linux app-server adapter; launch a callback-capable shared session with agent-callback codex.",
    "opencode": "Experimental public plugin and loopback HTTP adapter; restart OpenCode after installation."
  },
  "dataRemovalPolicy": "Preserved by default; pass --remove-data only with explicit approval."
}
EOF
  chmod 600 -- "$destination"
}

write_installation_record "$install_root/install-state.json"
write_installation_record "$codex_skill/references/app-installation.json"
write_installation_record "$opencode_skill/references/app-installation.json"

systemctl --user daemon-reload
if [[ "$start_host" -eq 1 ]]; then
  systemctl --user enable --now agent-callback.service
else
  systemctl --user enable agent-callback.service
fi

host_running=false
if systemctl --user is-active --quiet agent-callback.service; then
  host_running=true
fi
printf '{"installed":true,"version":"%s","platform":"linux-x64","executablePath":"%s","skillDirectory":"%s","openCodePluginPath":"%s","dataDirectory":"%s","hostRunning":%s,"uninstallCommand":"%s"}\n' \
  "$version" "$install_root/agent-callback" "$codex_skill" "$opencode_plugin" \
  "$state_root" "$host_running" "$uninstall_command"
