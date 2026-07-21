#!/usr/bin/env bash
set -euo pipefail

remove_data=0
quiet=0
for argument in "$@"; do
  case "$argument" in
    --remove-data) remove_data=1 ;;
    --quiet) quiet=1 ;;
    *) printf 'Usage: uninstall.sh [--remove-data] [--quiet]\n' >&2; exit 2 ;;
  esac
done

user_home="${HOME:?HOME is required}"
install_root="$user_home/.local/lib/agent-callback"
expected_script="$install_root/uninstall.sh"
current_script="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)/$(basename -- "${BASH_SOURCE[0]}")"
[[ "$current_script" == "$expected_script" ]] || {
  printf 'Uninstaller is not running from the owned install directory.\n' >&2
  exit 1
}

install_id="$(tr -d '\r\n' < "$install_root/install-id")"
[[ "$install_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  printf 'Installation ownership marker is invalid.\n' >&2
  exit 1
}

bin_link="$user_home/.local/bin/agent-callback"
codex_skill="$user_home/.codex/skills/agent-callback"
opencode_root="$user_home/.config/opencode"
opencode_skill="$opencode_root/skills/agent-callback"
opencode_plugin="$opencode_root/plugins/agent-callback.js"
systemd_unit="$user_home/.config/systemd/user/agent-callback.service"
state_root="${XDG_STATE_HOME:-$user_home/.local/state}/agent-callback"

systemctl --user disable --now agent-callback.service >/dev/null 2>&1 || true
if [[ -f "$systemd_unit" ]]; then
  grep -Fq "$install_root/agent-callback" "$systemd_unit" || {
    printf 'systemd unit ownership does not match the installation.\n' >&2
    exit 1
  }
  rm -f -- "$systemd_unit"
fi
systemctl --user daemon-reload

if [[ -L "$bin_link" && "$(readlink -f -- "$bin_link")" == "$install_root/agent-callback" ]]; then
  rm -f -- "$bin_link"
fi

remove_owned_skill() {
  local directory="$1"
  local root="$2"
  local record="$directory/references/app-installation.json"
  [[ -f "$record" ]] || return 0
  grep -Fq "\"installId\": \"$install_id\"" "$record" || {
    printf 'Skill ownership does not match the installation: %s\n' "$directory" >&2
    exit 1
  }
  case "$directory" in
    "$root"/*) rm -rf -- "$directory" ;;
    *) printf 'Refusing to remove Skill outside its allowed root: %s\n' "$directory" >&2; exit 1 ;;
  esac
}

remove_owned_skill "$codex_skill" "$user_home/.codex/skills"
remove_owned_skill "$opencode_skill" "$opencode_root"

if [[ -f "$opencode_plugin" ]]; then
  grep -Fq "const managedInstallId = \"$install_id\"" "$opencode_plugin" || {
    printf 'OpenCode plugin ownership does not match the installation.\n' >&2
    exit 1
  }
  rm -f -- "$opencode_plugin"
fi

data_removed=false
if [[ "$remove_data" -eq 1 ]]; then
  owner_path="$state_root/install-owner.json"
  [[ -f "$owner_path" ]] && \
    grep -Fq "\"installId\": \"$install_id\"" "$owner_path" || {
      printf 'Callback data ownership could not be verified; data was preserved.\n' >&2
      exit 1
    }
  case "$state_root" in
    "$user_home/.local/state/agent-callback"|"${XDG_STATE_HOME:-}/agent-callback")
      rm -rf -- "$state_root"
      data_removed=true
      ;;
    *) printf 'Refusing to remove unexpected data directory: %s\n' "$state_root" >&2; exit 1 ;;
  esac
fi

case "$install_root" in
  "$user_home/.local/lib/agent-callback") rm -rf -- "$install_root" ;;
  *) printf 'Refusing to remove unexpected install directory.\n' >&2; exit 1 ;;
esac

if [[ "$quiet" -eq 0 ]]; then
  printf '{"uninstalled":true,"installDirectory":"%s","dataDirectory":"%s","dataRemoved":%s}\n' \
    "$install_root" "$state_root" "$data_removed"
fi
