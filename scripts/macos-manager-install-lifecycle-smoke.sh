#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS install lifecycle proof requires macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
source_app="$repo_root/artifacts/macos/CodexPro-Safe Manager.app"
test -d "$source_app"
codesign --verify --deep --strict "$source_app"

temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/codexpro-safe-manager-install-lifecycle.XXXXXX")"
trap 'rm -rf "$temporary_root"' EXIT
install_root="$temporary_root/Applications"
rollback_root="$temporary_root/Rollback"
trash_root="$temporary_root/Trash"
destination="$install_root/CodexPro-Safe Manager.app"
previous="$rollback_root/CodexPro-Safe Manager.app"
update="$temporary_root/update/CodexPro-Safe Manager.app"
mkdir -p "$install_root" "$rollback_root" "$(dirname "$update")" "$trash_root"

ditto "$source_app" "$destination"
codesign --verify --deep --strict "$destination"
original_hash="$(shasum -a 256 "$destination/Contents/MacOS/CodexProSafeManager" | awk '{print $1}')"

ditto "$source_app" "$update"
/usr/libexec/PlistBuddy -c 'Set :CFBundleVersion 2' "$update/Contents/Info.plist"
codesign --force --options runtime --sign - "$update/Contents/MacOS/CodexProSafeLauncher"
codesign --force --options runtime --sign - "$update"
codesign --verify --deep --strict "$update"

mv "$destination" "$previous"
if ! mv "$update" "$destination" || ! codesign --verify --deep --strict "$destination"; then
  rm -rf "$destination"
  mv "$previous" "$destination"
  echo "Atomic update failed and was rolled back." >&2
  exit 1
fi
test "$(defaults read "$destination/Contents/Info" CFBundleVersion)" = "2"

failed_update="$temporary_root/failed-update.app"
ditto "$source_app" "$failed_update"
printf 'invalid' >>"$failed_update/Contents/MacOS/CodexProSafeManager"
if codesign --verify --deep --strict "$failed_update" >/dev/null 2>&1; then
  echo "Tampered update unexpectedly passed signature validation." >&2
  exit 1
fi
test "$(defaults read "$destination/Contents/Info" CFBundleVersion)" = "2"

current="$rollback_root/current.app"
mv "$destination" "$current"
mv "$previous" "$destination"
codesign --verify --deep --strict "$destination"
test "$(shasum -a 256 "$destination/Contents/MacOS/CodexProSafeManager" | awk '{print $1}')" = "$original_hash"

mv "$destination" "$trash_root/CodexPro-Safe Manager.app"
test ! -e "$destination"
codesign --verify --deep --strict "$trash_root/CodexPro-Safe Manager.app"

echo "✓ isolated install, signed update, tamper refusal, rollback, and recoverable removal proof passed"
