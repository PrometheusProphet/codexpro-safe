#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Installed-app verification requires macOS." >&2
  exit 1
fi

installed_app="${CODEXPRO_MAC_INSTALLED_APP:-$HOME/Applications/CodexPro-Safe Manager.app}"
manager_binary="$installed_app/Contents/MacOS/CodexProSafeManager"
settings_file="$HOME/Library/Application Support/CodexProSafe Manager/settings.json"

test -x "$manager_binary"
codesign --verify --deep --strict "$installed_app"
test "$("$manager_binary" --diagnostic-helper-status)" = "sealed"
test -f "$settings_file"
test "$(stat -f '%Lp' "$settings_file")" = "600"

initial_status="$("$manager_binary" --login-item-status)"
case "$initial_status" in
  enabled|requiresApproval)
    expected_status="$initial_status"
    ;;
  notRegistered|notFound)
    expected_status="notRegistered"
    ;;
  *)
    echo "Unexpected initial Launch at Login status: $initial_status" >&2
    exit 1
    ;;
esac

CODEXPRO_MAC_LOGIN_ITEM_APP="$installed_app" \
  bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/macos-manager-login-item-smoke.sh"

status="$("$manager_binary" --login-item-status)"
test "$status" = "$expected_status"
echo "Installed development app and state-preserving login-item lifecycle checks passed"
