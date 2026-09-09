# CodexPro-Safe Manager for macOS

The macOS Manager is a native SwiftUI menu-bar lifecycle owner for the existing
cross-platform CodexPro-Safe connector. It is an additive peer to the Windows
Manager; it does not replace or weaken the Windows implementation.

## Phase 4.5 boundary

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

Phase 4 adds confirmed exact-process takeover for a local connector started
outside the Manager. The Manager first requires authenticated loopback health,
then resolves exactly one `127.0.0.1` listener using the fixed system `lsof`,
reads native process identity through `libproc`/`sysctl`, and requires all of the
following to match:

- listener and direct owner PIDs and immutable start identities;
- the configured Node executable for both processes;
- the repository working directory and canonical connector/HTTP script paths;
- the complete root, allowed-root, port, local tunnel, diagnostics, access,
  write, Bash, and no-copy argument contract with no duplicates or extras;
- a dedicated process group containing exactly the owner and listener, with no
  third process present.

The confirmation dialog is shown only after that proof. Identity is read and
compared again immediately before signaling. Cancellation and every mismatch
send no signal. Confirming takeover interrupts active local MCP sessions, stops
only the verified isolated group, requires the old authenticated health endpoint
to disappear, and then starts a fresh Manager-owned connector. If relaunch fails,
the Manager reports degraded status and leaves the external process stopped;
correct the setting and choose **Start All** to retry. Unrelated listeners and
process groups remain untouched.

Phase 4.5 closes the local daily-driver gap. Saved settings now load before the
UI is published, existing Phase 4 settings migrate with automatic startup off,
and directory pickers provide a bounded first-run path. **Start connector when
Manager opens** is separate from **Launch Manager at login**; both are opt-in.
When automatic startup is enabled, the Manager starts only its local connector,
refuses an occupied port, restarts after an unexpected connector exit only when
recovery is selected, and rechecks authenticated health after system wake. A
development installer copies the verified app into `~/Applications`, initializes
only missing settings with the Planning profile, preserves existing settings,
and does not register a login item.

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
npm run manager:mac:test-takeover
npm run manager:mac:test-autostart
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

For a user-local development installation on the current Mac:

```bash
npm run manager:mac:install-development
npm run manager:mac:verify-installed
```

This path installs the ad-hoc-signed development app in `~/Applications`,
initializes missing settings for this checkout and Node executable, and opens the
menu-bar app. It is not a notarized public installer. Verification briefly
registers the exact installed app as a login item, confirms its status, then
unregisters it and leaves the final state `notRegistered`.

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

- Phase 5: add public-channel readiness and bounded rollback exercises for each
  deliberately supported tunnel adapter;
- Phase 6: obtain Developer ID Application and Installer identities, notarize the
  package, inspect the notary log, and prove clean-Mac install, update, rollback,
  and removal;
- Phase 7: design and prove a macOS-native diagnostic-helper trust boundary before
  enabling fixed-root diagnostic reads;
- Phase 8: complete accessibility, sleep/wake hardware, reboot, prolonged recovery,
  sanitized operations, and release-readiness proof.
