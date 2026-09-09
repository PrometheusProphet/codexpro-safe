# CodexPro-Safe Manager for macOS

The macOS Manager is a native SwiftUI menu-bar lifecycle owner for the existing
cross-platform CodexPro-Safe connector. It is an additive peer to the Windows
Manager; it does not replace or weaken the Windows implementation.

## Current boundary

The first implementation provides direct shell-free connector launch, Safe
access profiles, process-group stop/restart, loopback authenticated health
verification, bounded sanitized status output, Keychain bearer-token storage,
protected non-secret settings, optional restart after unexpected exit, and
login-item registration when running from an installed app bundle.

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
```

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

- build a signed `.app` installer and verify login-item behavior from that bundle;
- prove controlled takeover of an exact matching externally started process;
- add public-channel readiness and rollback exercises for each tunnel adapter;
- add a macOS-native diagnostic-helper trust design before enabling diagnostics;
- complete signing, notarization, update, rollback, and accessibility proof.
