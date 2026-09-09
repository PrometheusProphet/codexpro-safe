#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS release readiness requires macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
cd "$repo_root"

npm run build
npm run manager:mac:test
npm run manager:mac:package
npm run manager:mac:verify-bundle
npm run test:diagnostic-native-boundary
npm run manager:mac:test-takeover
npm run manager:mac:test-autostart
npm run manager:mac:test-secure-tunnel
npm run manager:mac:test-install-lifecycle
npm run manager:mac:test-soak
npm run test:documentation-contract

if [[ "${CODEXPRO_MAC_CERTIFY_RELEASE:-0}" == "1" ]]; then
  npm run manager:mac:release-preflight
  echo "macOS engineering and Apple credential preflight gates passed."
else
  echo "macOS engineering gates passed. Apple notarization, clean-Mac, sleep/wake, and reboot evidence remain external certification gates."
fi
