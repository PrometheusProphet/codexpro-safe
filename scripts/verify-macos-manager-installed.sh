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

CODEXPRO_MAC_LOGIN_ITEM_APP="$installed_app" \
  bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/macos-manager-login-item-smoke.sh"

status="$("$manager_binary" --login-item-status)"
test "$status" = "notRegistered"
echo "Installed development app and reversible login-item lifecycle checks passed"
