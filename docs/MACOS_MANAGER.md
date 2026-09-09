# CodexPro-Safe Manager for macOS

The macOS Manager is a native SwiftUI menu-bar lifecycle owner for the existing
cross-platform CodexPro-Safe connector. It is an additive peer to the Windows
Manager; it does not replace or weaken the Windows implementation.

## Phase 5 boundary

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

Local-only remains the default. A listening process or port alone is not
reported as ready; the Manager requires an HTTP 200 from the connector's
loopback `/healthz` endpoint, including bearer authentication when a Keychain
token is configured.

Phase 5 adds one deliberate remote adapter: **OpenAI Secure MCP Tunnel**. This is
an outbound-only private connection to OpenAI, not a public inbound tunnel. The
Manager does not create OpenAI tunnels, API keys, or local profiles. It accepts
an explicitly selected `tunnel-client` executable and safe profile name, stores
the runtime API key only in the user's Keychain, runs a bounded `doctor` check,
and starts the client in its own verified process group only after the local
connector is healthy. The connector continues to bind loopback and is always
launched with `--tunnel none`.

Tunnel readiness requires all of the following independently:

- loopback `/healthz` and `/readyz` return HTTP 200;
- bounded `/api/status` JSON identifies the same non-empty tunnel in both the
  control-plane and tunnel metadata fields;
- that identity exactly matches `tunnel_id` in the selected local profile;
- the `main` channel reports `probe_status: ok`.

Shutdown signals the tunnel group before the connector group. An unexpected
tunnel exit may restart only the tunnel while the local connector stays
available. Connector exit stops its dependent tunnel before bounded recovery.
Occupied tunnel health ports are refused and external tunnel processes are
never taken over. Failure of `doctor` or authenticated readiness leaves the
connector local and reports degraded status.

## Build and test

```bash
npm run manager:mac:test
npm run manager:mac:build
npm run manager:mac:package
npm run manager:mac:verify-bundle
npm run manager:mac:test-takeover
npm run manager:mac:test-autostart
npm run manager:mac:test-secure-tunnel
```

The secure-tunnel smoke uses a synthetic control plane and never contacts
OpenAI. It proves ordered dual-process lifecycle, exact authenticated-status
matching, independent tunnel crash recovery, and a real local MCP tool call.
A release-readiness record must additionally include a live tunnel and real
remote tool call; an open port or the synthetic gate alone is insufficient.

## Configure OpenAI Secure MCP Tunnel

Create the endpoint in [OpenAI Platform tunnel settings](https://platform.openai.com/settings/organization/tunnels)
and download `tunnel-client` from the latest official
[OpenAI release](https://github.com/openai/tunnel-client/releases/latest).
The runtime principal needs Tunnels Read + Use; creating or editing the endpoint
also needs Tunnels Read + Manage. Initialize a local HTTP profile while the
connector is intended to run on loopback:

```bash
tunnel-client init \
  --profile codexpro-safe-local \
  --tunnel-id tunnel_REPLACE_WITH_YOURS \
  --mcp-server-url http://127.0.0.1:8787/mcp \
  --health-listen-addr 127.0.0.1:8080
tunnel-client doctor --profile codexpro-safe-local --explain
```

In Manager Settings, select **OpenAI Secure MCP Tunnel**, choose the exact
client binary, enter the profile and health port, optionally enter the owning
`org-...` ID, and save the runtime key using **Save OpenAI Runtime Key**. Saving
settings or selecting the mode does not start services. **Start All** starts the
local connector first, then the tunnel after the checks above. Changing tunnel
identity interrupts remote MCP sessions; stop the Manager, initialize or update
the profile, save settings, and start again. Roll back by selecting **Local
only** and restarting; this leaves the OpenAI endpoint and profile untouched.

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

- Phase 6: obtain Developer ID Application and Installer identities, notarize the
  package, inspect the notary log, and prove clean-Mac install, update, rollback,
  and removal;
- Phase 7: design and prove a macOS-native diagnostic-helper trust boundary before
  enabling fixed-root diagnostic reads;
- Phase 8: complete accessibility, sleep/wake hardware, reboot, prolonged recovery,
  sanitized operations, and release-readiness proof.
