# CodexPro-Safe Manager

A small Windows notification-area application that owns the local CodexPro-Safe
connector and the OpenAI tunnel client as one lifecycle.

For installation, tunnel, credential, ChatGPT plugin, migration, and
troubleshooting instructions, see
[CodexPro-Safe Manager for Windows](../../docs/WINDOWS_MANAGER.md).

It preserves the verified connector launch contract:

```text
node scripts/codexpro.mjs --root <workspace-root> --allow-root <allowed-root> --tunnel none --mode handoff --bash off --write handoff --codex-diagnostic-read <off|read>
```

The tunnel client uses the existing `codexpro-safe-local` profile. Its API key
remains an `env:CONTROL_PLANE_API_KEY` reference in the profile. The manager asks
for the key once and encrypts the complete settings payload with Windows DPAPI
for the current Windows user under:

```text
%LOCALAPPDATA%\CodexProSafe Manager\settings.dat
```

Secrets are redacted from the UI and file logs. Logs are stored under:

```text
%LOCALAPPDATA%\CodexProSafe Manager\logs
```

## Build

No SDK or package download is required. Windows' installed .NET Framework
compiler is used:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\build.ps1
```

The executable is generated at:

```text
tools\CodexProSafe.Manager\bin\CodexProSafe.Manager.exe
```

The same build explicitly compiles and self-tests the app-local diagnostic
companion at `bin\CodexProSafe.DiagnosticHelper.exe`, then writes its protocol
and SHA-256 manifest. The helper is a narrow fixed-root Win32 boundary; it is
not installed globally and is never resolved through `PATH`.

## First run

1. Open **Settings**.
2. Confirm the detected repository, workspace root, Node.js, tunnel client, and
   `codexpro-safe-local` profile.
3. Enter the runtime Control Plane API key once and the `org-...` organization
   ID that owns the tunnel.
4. Save.
5. If legacy externally launched services are still running, choose **Restart
   All** and approve the one-time exact-process takeover.

The takeover is fail-closed. The manager refuses to stop an external process
unless the listener, executable/profile, command line, workspace root, allowed
root, tunnel mode, handoff mode, and saved Codex diagnostic mode match.

Codex diagnostics default to **Off**. **Read-only** enables only fixed-root,
metadata-only diagnostics and does not grant generic home/runtime access or any
maintenance action. The installed Manager seals and verifies its companion
helper in DPAPI-protected settings and refuses unverified external connectors
in this mode. Its one-shot gate authenticates the exact launcher PID and sends a
random per-instance capability through private pipes, never environment or
argv. The fixed Manager proof client must return that capability to the real
Manager pipe server before supplemental job-membership and ancestry checks run
and the helper contract is released. Environment, command-line values, and
spoofed parent metadata alone cannot enable the profile. A setting change takes
effect through **Restart All**.

After takeover, use **Start All**, **Restart All**, and **Stop All** from either
the main window or the notification-area menu. Closing the window minimizes it.
The app asks you to stop manager-owned services before exiting so redirected
logs and child shutdown remain deterministic. Externally started services are
never changed merely because the manager exits.

Tunnel readiness requires authenticated control-plane metadata, a tunnel ID
matching the configured profile, and an `ok` main-channel probe. A listening
local port by itself is not reported as ready.

Create the corresponding ChatGPT plugin as **CodexPro-Safe Manager**, select the
tunnel used by the profile, and use **No Auth**. Restart Codex after adding or
replacing the plugin so its MCP tool registry is refreshed.

## Install a Desktop shortcut

The installer copies the built executable to a stable per-user location and
creates **CodexPro-Safe Manager** on the Windows Desktop:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\install.ps1
```

The installed application lives under:

```text
%LOCALAPPDATA%\Programs\CodexProSafe Manager
```

That directory also contains the exact diagnostic helper and build manifest.
Installation seals their contract but does not start diagnostics, restart live
services, inspect `~/.codex`, or establish plugin callability by itself.

The installed Manager executable is also the connector's native launch-proof
client. It authenticates the lifecycle Manager pipe server by PID and fixed
image path; the server requires the private gate capability and then checks the
proof client's job membership, real PID, creation time, and ancestry through
the Manager-created launcher and HTTP process. Pipe and gate locators are
inherited environment data but are not themselves credentials or proof.
