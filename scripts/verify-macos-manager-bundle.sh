#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS Manager bundle verification requires macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
output_root="${CODEXPRO_MAC_OUTPUT_DIR:-$repo_root/artifacts/macos}"
version="$(cd "$repo_root" && node -p "require('./package.json').version")"
app_path="$output_root/CodexPro-Safe Manager.app"
pkg_path="$output_root/CodexPro-Safe-Manager-$version-macOS-universal.pkg"
checksum_path="$output_root/SHA256SUMS"

test -x "$app_path/Contents/MacOS/CodexProSafeManager"
test -x "$app_path/Contents/MacOS/CodexProSafeLauncher"
test -x "$app_path/Contents/MacOS/CodexProSafeDiagnosticHelper"
test -f "$app_path/Contents/Resources/CodexProSafeDiagnosticHelper.json"
test -f "$pkg_path"
test -f "$checksum_path"
plutil -lint "$app_path/Contents/Info.plist"
test "$(defaults read "$app_path/Contents/Info" CFBundleIdentifier)" = "com.prometheusprophet.codexpro-safe-manager"
test "$(defaults read "$app_path/Contents/Info" LSUIElement)" = "1"
test "$(defaults read "$app_path/Contents/Info" LSMinimumSystemVersion)" = "14.0"

for executable in CodexProSafeManager CodexProSafeLauncher CodexProSafeDiagnosticHelper; do
  architectures="$(lipo -archs "$app_path/Contents/MacOS/$executable")"
  [[ " $architectures " == *" arm64 "* ]]
  [[ " $architectures " == *" x86_64 "* ]]
done

codesign --verify --deep --strict --verbose=2 "$app_path"
"$app_path/Contents/MacOS/CodexProSafeDiagnosticHelper" --self-test
helper_manifest_hash="$(plutil -extract sha256 raw "$app_path/Contents/Resources/CodexProSafeDiagnosticHelper.json")"
helper_actual_hash="$(shasum -a 256 "$app_path/Contents/MacOS/CodexProSafeDiagnosticHelper" | awk '{print $1}')"
test "$helper_manifest_hash" = "$helper_actual_hash"
code_signature="$(codesign --display --verbose=4 "$app_path" 2>&1)"
grep -Fq 'Runtime Version=' <<<"$code_signature"
package_signature="$(pkgutil --check-signature "$pkg_path" 2>&1 || true)"
if [[ -n "${CODEXPRO_MAC_INSTALLER_SIGN_IDENTITY:-}" ]]; then
  [[ "$package_signature" != *"Status: no signature"* ]]
else
  [[ "$package_signature" == *"Status: no signature"* ]]
fi
payload_files="$(pkgutil --payload-files "$pkg_path")"
grep -Fq 'CodexPro-Safe Manager.app/Contents/MacOS/CodexProSafeManager' <<<"$payload_files"
grep -Fq 'CodexPro-Safe Manager.app/Contents/MacOS/CodexProSafeDiagnosticHelper' <<<"$payload_files"
expanded_package="$(mktemp -d "${TMPDIR:-/tmp}/codexpro-safe-manager-verify.XXXXXX")"
trap 'rm -rf "$expanded_package"' EXIT
pkgutil --expand "$pkg_path" "$expanded_package/package"
if [[ -n "$(find "$expanded_package/package" -name Scripts -print -quit)" ]]; then
  echo "Installer unexpectedly contains install scripts." >&2
  exit 1
fi
(
  cd "$output_root"
  shasum -a 256 -c "$(basename "$checksum_path")"
)

if rg -n '/Users/[^/[:space:]]+|Authorization: Bearer|github_pat_|ghp_|sk-[A-Za-z0-9]' "$app_path/Contents/Info.plist"; then
  echo "Bundle metadata contains a credential or machine-specific path." >&2
  exit 1
fi

echo "macOS Manager universal app, signature, and installer checks passed"
