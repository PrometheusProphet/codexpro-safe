#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Launch at Login verification requires macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
source_app="$repo_root/artifacts/macos/CodexPro-Safe Manager.app"
applications_root="$HOME/Applications"
mkdir -p "$applications_root"
probe_root=""
if [[ -n "${CODEXPRO_MAC_LOGIN_ITEM_APP:-}" ]]; then
  probe_app="$CODEXPRO_MAC_LOGIN_ITEM_APP"
else
  probe_root="$(mktemp -d "$applications_root/.codexpro-safe-login-item-probe.XXXXXX")"
  probe_app="$probe_root/CodexPro-Safe Manager.app"
  ditto "$source_app" "$probe_app"
fi
probe_binary="$probe_app/Contents/MacOS/CodexProSafeManager"
cleanup_registration=false

cleanup() {
  if [[ "$cleanup_registration" == "true" && -x "$probe_binary" ]]; then
    "$probe_binary" --login-item-unregister >/dev/null 2>&1 || true
  fi
  if [[ -n "$probe_root" ]]; then rm -rf "$probe_root"; fi
}
trap cleanup EXIT

test -d "$probe_app"

initial_status="$("$probe_binary" --login-item-status)"
if [[ "$initial_status" == "enabled" || "$initial_status" == "requiresApproval" ]]; then
  echo "Launch at Login was already $initial_status; existing registration preserved and mutation skipped."
  exit 0
fi
if [[ "$initial_status" != "notRegistered" && "$initial_status" != "notFound" ]]; then
  echo "Unexpected initial Launch at Login status: $initial_status" >&2
  exit 1
fi

cleanup_registration=true
registered_status="$("$probe_binary" --login-item-register)"
if [[ "$registered_status" != "enabled" && "$registered_status" != "requiresApproval" ]]; then
  echo "Unexpected status after registration: $registered_status" >&2
  exit 1
fi

"$probe_binary" --login-item-unregister >/dev/null
cleanup_registration=false
final_status="$("$probe_binary" --login-item-status)"
test "$final_status" = "notRegistered"
echo "Launch at Login register/status/unregister proof passed; final state is not registered"
