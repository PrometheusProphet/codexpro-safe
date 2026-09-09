#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS Manager packaging requires macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
package_root="$repo_root/tools/CodexProSafe.Manager.Mac"
output_root="${CODEXPRO_MAC_OUTPUT_DIR:-$repo_root/artifacts/macos}"
version="$(cd "$repo_root" && node -p "require('./package.json').version")"
build_number="${CODEXPRO_MAC_BUILD_NUMBER:-1}"
app_identity="${CODEXPRO_MAC_APP_SIGN_IDENTITY:--}"
installer_identity="${CODEXPRO_MAC_INSTALLER_SIGN_IDENTITY:-}"
notary_profile="${CODEXPRO_MAC_NOTARY_PROFILE:-}"
app_name="CodexPro-Safe Manager.app"
app_path="$output_root/$app_name"
zip_path="$output_root/CodexPro-Safe-Manager-$version-macOS-universal.zip"
pkg_path="$output_root/CodexPro-Safe-Manager-$version-macOS-universal.pkg"
checksum_path="$output_root/SHA256SUMS"
plist_source="$package_root/Resources/Info.plist"

if [[ ! "$build_number" =~ ^[1-9][0-9]*$ ]]; then
  echo "CODEXPRO_MAC_BUILD_NUMBER must be a positive integer." >&2
  exit 1
fi
if [[ -n "$notary_profile" && ("$app_identity" == "-" || -z "$installer_identity") ]]; then
  echo "Notarization requires Developer ID Application and Installer identities." >&2
  exit 1
fi
if [[ "$app_identity" != "-" ]] && ! security find-identity -v -p codesigning | grep -Fq "\"$app_identity\""; then
  echo "The configured Developer ID Application identity is unavailable." >&2
  exit 1
fi
if [[ -n "$installer_identity" ]] && ! security find-certificate -c "$installer_identity" -Z >/dev/null 2>&1; then
  echo "The configured Developer ID Installer certificate is unavailable." >&2
  exit 1
fi
if [[ -n "$notary_profile" ]] && ! xcrun notarytool history --keychain-profile "$notary_profile" >/dev/null; then
  echo "The configured notarytool Keychain profile is unavailable or invalid." >&2
  exit 1
fi

mkdir -p "$output_root"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/codexpro-safe-manager-package.XXXXXX")"
trap 'rm -rf "$temporary_root"' EXIT
staged_app="$temporary_root/$app_name"

swift build --package-path "$package_root" -c release --arch arm64 --arch x86_64
binary_root="$(swift build --package-path "$package_root" -c release --arch arm64 --arch x86_64 --show-bin-path)"

mkdir -p "$staged_app/Contents/MacOS" "$staged_app/Contents/Resources"
cp "$plist_source" "$staged_app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$staged_app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $build_number" "$staged_app/Contents/Info.plist"
install -m 0755 "$binary_root/CodexProSafeManager" "$staged_app/Contents/MacOS/CodexProSafeManager"
install -m 0755 "$binary_root/CodexProSafeLauncher" "$staged_app/Contents/MacOS/CodexProSafeLauncher"
install -m 0755 "$binary_root/CodexProSafeDiagnosticHelper" "$staged_app/Contents/MacOS/CodexProSafeDiagnosticHelper"

sign_options=(--force --options runtime --sign "$app_identity")
if [[ "$app_identity" != "-" ]]; then sign_options+=(--timestamp); fi
codesign "${sign_options[@]}" "$staged_app/Contents/MacOS/CodexProSafeLauncher"
codesign "${sign_options[@]}" "$staged_app/Contents/MacOS/CodexProSafeDiagnosticHelper"
helper_sha="$(shasum -a 256 "$staged_app/Contents/MacOS/CodexProSafeDiagnosticHelper" | awk '{print $1}')"
printf '{"protocolVersion":"codexpro-diagnostic-v1","executable":"CodexProSafeDiagnosticHelper","sha256":"%s"}\n' "$helper_sha" \
  >"$staged_app/Contents/Resources/CodexProSafeDiagnosticHelper.json"
codesign "${sign_options[@]}" "$staged_app"
codesign --verify --deep --strict --verbose=2 "$staged_app"

rm -rf "$app_path"
rm -f "$zip_path" "$pkg_path" "$checksum_path"
ditto "$staged_app" "$app_path"
ditto -c -k --sequesterRsrc --keepParent "$app_path" "$zip_path"

if [[ -n "$notary_profile" ]]; then
  xcrun notarytool submit "$zip_path" --keychain-profile "$notary_profile" --wait
  xcrun stapler staple "$app_path"
  xcrun stapler validate "$app_path"
  rm -f "$zip_path"
  ditto -c -k --sequesterRsrc --keepParent "$app_path" "$zip_path"
fi

pkg_arguments=(--component "$app_path" --install-location /Applications
  --identifier com.prometheusprophet.codexpro-safe-manager.pkg --version "$version")
if [[ -n "$installer_identity" ]]; then pkg_arguments+=(--sign "$installer_identity"); fi
pkgbuild "${pkg_arguments[@]}" "$pkg_path"

if [[ -n "$notary_profile" ]]; then
  xcrun notarytool submit "$pkg_path" --keychain-profile "$notary_profile" --wait
  xcrun stapler staple "$pkg_path"
  xcrun stapler validate "$pkg_path"
fi

(
  cd "$output_root"
  shasum -a 256 "$(basename "$zip_path")" "$(basename "$pkg_path")" >"$(basename "$checksum_path")"
)

echo "$app_path"
echo "$zip_path"
echo "$pkg_path"
echo "$checksum_path"
