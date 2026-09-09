# CodexPro-Safe Manager for macOS

The macOS Manager is a native SwiftUI menu-bar lifecycle owner for the existing
cross-platform CodexPro-Safe connector. It is an additive peer to the Windows
Manager; it does not replace or weaken the Windows implementation.

## Phase 3 boundary

The first implementation provides direct shell-free connector launch, Safe
access profiles, process-group stop/restart, loopback authenticated health
verification, bounded sanitized status output, Keychain bearer-token storage,
protected non-secret settings, optional restart after unexpected exit, and
login-item registration when running from an installed app bundle.

Phase 3 adds a universal `arm64` + `x86_64` app bundle, hardened-runtime code
signing, a component installer, repeatable bundle/payload checks, and a reversible
Launch at Login register/status/unregister proof. The default build uses an
ad-hoc app signature and an unsigned installer for local development only. It is
not a public release artifact.

Codex diagnostic read and maintenance filesystem features remain unavailable
and effectively off. Do not add the home directory, `~/.codex`, generic runtime
reads, or a substitute helper until a separate macOS-native trust proof passes
its own security review.

This first Manager slice is intentionally local-only. A listening process or
port alone is not reported as ready; the Manager requires an HTTP 200 from the
connector's loopback `/healthz` endpoint, including bearer authentication when
a Keychain token is configured. Public tunnel selection will remain unavailable
until each adapter has separate public-channel readiness and rollback proof.

## Build and test

```bash
npm run manager:mac:test
npm run manager:mac:build
npm run manager:mac:package
npm run manager:mac:verify-bundle
```

Packaging writes ignored development artifacts to `artifacts/macos/`: the app,
a ZIP archive, a SHA-256 checksum manifest, and a component package that installs
only the app into `/Applications`. The package has no install scripts and does
not register a login item. Installing it replaces the exact app bundle at that
destination and may require administrator approval.

To exercise Launch at Login without leaving it enabled:

```bash
npm run manager:mac:test-login-item
```

The probe preserves an existing enabled or approval-pending registration. When
no registration exists, it copies a temporary app into `~/Applications`, calls
the native Service Management API, verifies the resulting status, unregisters,
confirms `notRegistered`, and removes the temporary copy.

For a distribution build, provide the exact Developer ID identities already
installed in the signing keychain and a `notarytool` Keychain profile:

```bash
CODEXPRO_MAC_APP_SIGN_IDENTITY="Developer ID Application: Example (TEAMID)" \
CODEXPRO_MAC_INSTALLER_SIGN_IDENTITY="Developer ID Installer: Example (TEAMID)" \
CODEXPRO_MAC_NOTARY_PROFILE="codexpro-safe-notary" \
npm run manager:mac:package
```

The packaging script enables the hardened runtime, requests a secure timestamp,
submits the installer with `notarytool`, staples the ticket, and validates it.
It fails closed if notarization is requested without both Developer ID
identities. Credentials remain in Apple's Keychain and are never accepted as
script arguments or repository files.

Run the development executable with explicit paths so no shell profile is
required:

```bash
CODEXPRO_MANAGER_REPOSITORY="$PWD" \
CODEXPRO_MANAGER_NODE="$(command -v node)" \
swift run --package-path tools/CodexProSafe.Manager.Mac CodexProSafeManager
```

The Planning profile is the default: handoff writes, command execution off,
local-only tunnel, diagnostics off. Repository edit, develop, and full remain
explicit selections. Full uses the current macOS user's permissions and is not
an OS sandbox.

## Remaining gates

- obtain Developer ID Application and Installer identities, notarize the package,
  inspect the notary log, and test the stapled installer on a clean second Mac;
- prove controlled takeover of an exact matching externally started process;
- add public-channel readiness and rollback exercises for each tunnel adapter;
- add a macOS-native diagnostic-helper trust design before enabling diagnostics;
- complete update, installed-app rollback, and accessibility proof.
