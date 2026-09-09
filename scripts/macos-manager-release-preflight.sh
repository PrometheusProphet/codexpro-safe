#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS release preflight requires macOS." >&2
  exit 1
fi

app_identity="${CODEXPRO_MAC_APP_SIGN_IDENTITY:-}"
installer_identity="${CODEXPRO_MAC_INSTALLER_SIGN_IDENTITY:-}"
notary_profile="${CODEXPRO_MAC_NOTARY_PROFILE:-}"

if [[ -z "$app_identity" || -z "$installer_identity" || -z "$notary_profile" ]]; then
  echo "Release preflight requires Application identity, Installer identity, and notarytool Keychain profile environment names." >&2
  exit 1
fi
if ! security find-identity -v -p codesigning | grep -Fq "\"$app_identity\""; then
  echo "Developer ID Application identity unavailable." >&2
  exit 1
fi
if ! security find-certificate -c "$installer_identity" -Z >/dev/null 2>&1; then
  echo "Developer ID Installer certificate unavailable." >&2
  exit 1
fi
if ! xcrun notarytool history --keychain-profile "$notary_profile" >/dev/null; then
  echo "notarytool Keychain profile unavailable or invalid." >&2
  exit 1
fi

echo "Developer ID Application identity, Installer certificate, and notarytool Keychain profile are available."
