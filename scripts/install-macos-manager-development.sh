#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "The development Manager can be installed only on macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
source_app="$repo_root/artifacts/macos/CodexPro-Safe Manager.app"
install_root="${CODEXPRO_MAC_INSTALL_ROOT:-$HOME/Applications}"
destination="$install_root/CodexPro-Safe Manager.app"
manager_binary="$destination/Contents/MacOS/CodexProSafeManager"
node_path="${CODEXPRO_MANAGER_NODE:-$(command -v node)}"

test -d "$source_app"
test -x "$node_path"
codesign --verify --deep --strict "$source_app"

if pgrep -f -x "$destination/Contents/MacOS/CodexProSafeManager" >/dev/null 2>&1; then
  echo "Quit the installed CodexPro-Safe Manager before replacing it." >&2
  exit 1
fi

mkdir -p "$install_root"
temporary_root="$(mktemp -d "$install_root/.codexpro-safe-development-install.XXXXXX")"
staged_app="$temporary_root/CodexPro-Safe Manager.app"
backup_app="$temporary_root/previous.app"
installed=false

cleanup() {
  if [[ "$installed" != "true" && -d "$backup_app" && ! -e "$destination" ]]; then
    mv "$backup_app" "$destination"
  fi
  rm -rf "$temporary_root"
}
trap cleanup EXIT

ditto "$source_app" "$staged_app"
if [[ -e "$destination" ]]; then mv "$destination" "$backup_app"; fi
mv "$staged_app" "$destination"
codesign --verify --deep --strict "$destination"
"$manager_binary" --initialize-local-settings "$repo_root" "$node_path"
installed=true

if [[ "${CODEXPRO_MAC_OPEN_AFTER_INSTALL:-1}" == "1" ]]; then open "$destination"; fi

echo "$destination"
echo "Development app installed with Planning defaults; connector auto-start and Launch at Login remain off."
