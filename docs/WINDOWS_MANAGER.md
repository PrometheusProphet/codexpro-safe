# CodexPro-Safe Manager for Windows

CodexPro-Safe Manager is the optional Windows notification-area application for
running the CodexPro-Safe connector and the OpenAI tunnel client as one
lifecycle. It provides **Start All**, **Restart All**, and **Stop All** controls,
automatic startup at Windows sign-in, supervised recovery, and combined logs.

The names have different scopes:

- **CodexPro-Safe** is the connector, repository, and package.
- **CodexPro-Safe Manager** is the Windows lifecycle application and the
  corresponding ChatGPT plugin name.
- `codexpro-safe-local` is the local tunnel profile name. It may remain unchanged
  even when its OpenAI tunnel is named `codexpro-safe-manager`.

The normal `codexpro start` workflow remains supported on every platform. The
Manager is a convenience and reliability layer for the Windows deployment.

## What the Manager owns

The Manager starts and supervises:

1. the local CodexPro-Safe connector; and
2. the OpenAI tunnel client profile that publishes that connector.

It only takes over externally started processes after their listener,
executable or profile, command line, workspace root, allowed root, tunnel mode,
and handoff mode match the saved settings. A mismatch fails closed.

## Prerequisites

- A working CodexPro-Safe source checkout with `npm ci` and `npm run build`
  already completed.
- Node.js.
- The OpenAI tunnel client for Windows.
- An OpenAI tunnel and a local tunnel-client profile for it.
- A runtime OpenAI API key whose project can use that tunnel.

Do not put an API key, organization ID, or tunnel ID in the repository.

## Build and install

From the CodexPro-Safe repository:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\build.ps1
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\install.ps1
```

Exit a running Manager before installation. The installer refuses to replace or
reseal the helper package while that lifecycle owner is active.

The installer creates **CodexPro-Safe Manager** on the Windows Desktop and
installs the executable under:

```text
%LOCALAPPDATA%\Programs\CodexProSafe Manager
```

For a separately authorized diagnostic activation, the installer accepts only
the fixed optional mode parameter:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\install.ps1 -CodexDiagnostics read
```

The only accepted values are `off` and `read`. Omitting the parameter preserves
the saved mode (and a new settings file defaults to `off`). The installer does
not accept settings JSON, credentials, arbitrary field names, commands, or
process-control options.

## Noninteractive operational boundary

The installed Manager provides two narrow commands before GUI and singleton
startup. Neither starts, stops, restarts, attaches to, or takes over a service,
and neither reads Manager logs. The mode command performs no process discovery;
safe status performs only bounded read-only local health and saved-mode checks.

```powershell
& "$env:LOCALAPPDATA\Programs\CodexProSafe Manager\CodexProSafe.Manager.exe" --set-codex-diagnostics read
& "$env:LOCALAPPDATA\Programs\CodexProSafe Manager\CodexProSafe.Manager.exe" --set-codex-diagnostics off
& "$env:LOCALAPPDATA\Programs\CodexProSafe Manager\CodexProSafe.Manager.exe" --safe-status
```

The mode command accepts exactly `read` or `off`, changes only
`CodexDiagnosticReadMode` inside the existing current-user DPAPI payload, and
returns a fixed JSON envelope containing only schema, command, status, mode,
and `restartRequired`. It uses an encrypted same-directory atomic replacement,
preserves the existing file owner and access rules, and leaves unrelated and
unknown settings semantically unchanged. `read` fails closed unless the
installed helper trust contract is sealed.

`--safe-status` returns only this fixed field set:

- schema version;
- saved diagnostic mode (`off`, `read`, or `unavailable`);
- helper trust (`sealed`, `unavailable`, or `invalid`);
- connector local health;
- tunnel local process health;
- tunnel authenticated readiness;
- whether a controlled connector restart is required;
- fixed `overall` and `limitation` enums.

It does not return settings values, paths, process data, endpoints, profiles,
provider identifiers, or provider payloads. Exit code `0` means the bounded
status was produced (including a truthful degraded state), `2` means invalid
fixed command usage, `3` means the mode update was unavailable, and `4` means
status or required helper trust was unsafe/unavailable.

Restart detection does not trust process ancestry. The connector exposes one
fixed loopback-only status document containing only its effective diagnostic
mode after Manager launch proof has completed. Safe status reads that document
with a small response bound and conservatively requires restart when it is
missing, malformed, or differs from the saved mode.

A future authorized no-UI activation therefore uses this bounded sequence:

1. Exit the Manager safely after stopping only its owned services.
2. Build and install/seal the accepted package, optionally with
   `-CodexDiagnostics read`.
3. If the installer did not set the mode, invoke the fixed mode command.
4. Launch the Manager normally and perform the separately authorized controlled
   restart.
5. Use `--safe-status` and live connector diagnostic calls for verification.

Installation or a mode update alone does not restart services or prove plugin
callability.

## Configure the Manager

Open **Settings** and confirm:

- **Repository** — the CodexPro-Safe source checkout.
- **Workspace root** — the folder the connector exposes.
- **Allowed root** — the broadest folder the connector may access.
- **Node.js** — the `node.exe` used to launch the connector.
- **Tunnel client** — the downloaded `tunnel-client.exe`.
- **Tunnel profile** — normally `codexpro-safe-local`.
- **Control Plane API key** — a runtime OpenAI API key copied when it was
  created.
- **Organization ID** — the `org-...` identifier that owns the tunnel.
- **Codex diagnostics** — **Off** by default. **Read-only** enables only the
  fixed-root, metadata-only Codex diagnostic profile; it does not add the home
  directory to workspace access or enable generic runtime reads. Readable
  results require the exact Manager-built diagnostic helper sealed during an
  approved installation; otherwise the diagnostic tools fail closed.

For least privilege, create a restricted runtime API key and enable only
**Tunnels → All selected**. The secret value is shown only once by OpenAI. The
dots displayed later in Manager Settings are masking; the Manager does not
replace a saved key with dots when the field is left unchanged.

The organization ID must identify the organization that owns the tunnel. It is
not a project ID, workspace ID, tunnel ID, or API-key tracking ID.

The Manager encrypts its complete settings payload for the current Windows
account with DPAPI and stores it at:

```text
%LOCALAPPDATA%\CodexProSafe Manager\settings.dat
```

Changing Codex diagnostics takes effect only after the existing controlled
connector restart. The Manager includes its saved `off`/`read` value in the
connector arguments and refuses external-process takeover when it does not
match. In `read` mode it also refuses to use or take over an external connector
because that process instance cannot satisfy the Manager-owned launch proof.

The build produces `CodexProSafe.DiagnosticHelper.exe` and an exact SHA-256
manifest beside the Manager. The manifest preserves the fixed diagnostic
protocol and additively declares the source-only
`codexpro-maintenance-fs-v1` capability. That second mode binds a caller-owned
local NTFS root through private stdin and is not a Manager setting, connector
tool, live activation, or arbitrary-root MCP surface. See
[Maintenance filesystem provider](MAINTENANCE_FILESYSTEM_PROVIDER.md).
The installer copies both files to the same
per-user application directory and runs the Manager's noninteractive sealing
mode, which persists the helper path, protocol, and fingerprint inside the
existing DPAPI-protected settings. On connector start, the Manager verifies the
saved values and file bytes while retaining a no-delete lock, then supplies the
contract only after a one-shot Windows named-pipe proof. The environment carries
random pipe and startup-gate locators, not the helper path, protocol, or
fingerprint. The launcher must wait at that gate while the Manager assigns it
to a newly created unnamed Windows Job object. The gate verifies the exact
launcher PID through Windows and releases a random per-instance capability over
the pipe; that capability is passed to the HTTP child and fixed Manager proof
client only through private stdin pipes, never environment or argv. The proof
client verifies that Windows reports the lifecycle Manager as the proof-pipe
server, then returns the private capability. The lifecycle Manager compares it
in constant time before applying supplemental job-membership, PID, creation
time, executable-path, command-line, and ancestry checks. The broker is
instance-bound, expires after startup, and accepts only one connection, so
stale, replayed, parent-spoofed, or unrelated connector attempts fail closed.
Only the authenticated pipe response releases the sealed helper contract.

The connector never searches `PATH`, and the helper never accepts a path
argument. The package directory and helper executable are opened without
following reparse points; redirected, unexpected-type, or multi-link package
objects are rejected before launch. A direct connector with copied environment
or argv values cannot obtain the private capability released only to the exact
Manager-created launcher, and a fake proof endpoint is rejected because its
server is not the trusted installed Manager. Job membership and lineage are
additional checks, not the sole ownership proof.

This SHA-256 binding and process proof are app-local substitution and ownership
checks, not a general code-signing root. They assume the fixed installed Manager
executable and the current user's DPAPI-protected settings remain trusted. An
actor able to replace those Manager-owned assets is outside this boundary.

Read-only diagnostics are synthetic-test coverage only; they do not
verify or inspect this user's live runtime during installation, and maintenance
operations are not part of the setting.

Logs are processed by one bounded policy before either disk persistence or the
in-window activity view and are stored under:

```text
%LOCALAPPDATA%\CodexProSafe Manager\logs
```

Manager and connector lines redact recognized bearer/key/secret fields,
provider-style identifiers, UUID correlation values in child output, private
URL userinfo/query values, and local paths. Oversized lines are suppressed or
bounded. Raw third-party tunnel payload lines are suppressed by default; only
fixed high-level startup, readiness, health-wait, shutdown, and exit summaries
are retained. This is a deliberately bounded policy, not a claim of universal
secret detection.

The visual activity surface is a custom-drawn control rather than a native text
control. Its accessibility object exposes only a fixed name and description,
not accumulated log text or children, so generic UI Automation cannot extract
the activity buffer. Status labels, buttons, Settings controls, and other user
actions remain accessible. The tradeoff is that the activity surface itself is
not selectable or screen-reader-readable; no unrestricted export/copy action is
provided.

For the normal always-available setup, enable all four options:

- **Launch manager when I sign in**
- **Start minimized to the notification area**
- **Start connector and tunnel when manager opens**
- **Restart manager-owned services after an unexpected exit**

## First takeover and daily use

If connector or tunnel processes were started manually, open the Manager and
choose **Restart All**. Approve the one-time takeover only after the Manager
shows the exact matching processes.

After that:

- **Start All** starts both services.
- **Restart All** performs a controlled restart of both services.
- **Stop All** shuts both services down.
- Closing the window minimizes the Manager to the notification area.
- Exiting the Manager asks whether to stop services that it owns.

“Ready” means more than an open local port. The Manager checks authenticated
tunnel metadata, confirms that the tunnel ID matches the configured profile,
and requires the main channel probe to report `ok`.

## Create the ChatGPT plugin

After the Manager reports both services ready:

1. Open ChatGPT **Settings → Plugins** and create a plugin.
2. Name it **CodexPro-Safe Manager**.
3. Choose **Tunnel** and select the tunnel used by the Manager profile.
4. Select **No Auth**. Tunnel access and connector policy are handled by the
   configured tunnel and CodexPro-Safe.
5. Review and acknowledge the custom MCP server warning, then create it.
6. Restart Codex after adding or replacing the plugin so its MCP tool registry
   is refreshed.
7. Verify an end-to-end CodexPro-Safe tool call through the new plugin before
   removing an older plugin.

Creating a new tunnel does not automatically update an existing ChatGPT plugin.
If the tunnel changes, update the local profile, restart the Manager-owned
services, and create or repoint the plugin to the new tunnel.

## Troubleshooting

| Log or symptom | Meaning | Action |
| --- | --- | --- |
| `invalid_api_key` | The saved runtime key is wrong, revoked, or incomplete. | Create or copy a valid runtime key, save it in Manager Settings, then choose **Restart All**. |
| `mismatched_organization` | The organization header does not match the API key's organization. | Set **Organization ID** to the organization that owns the key and tunnel. |
| `active_organization_required` | The tunnel requires an organization but none was supplied. | Add the owning `org-...` ID in Manager Settings. |
| `tunnel_use_forbidden` | The key's principal cannot use the selected tunnel. | Grant that principal access or create a tunnel with the same project/principal used by the runtime key. |
| The old plugin returns `404` or a terminated session | The plugin still targets an old or deleted tunnel. | Create or repoint **CodexPro-Safe Manager**, verify it, remove the stale plugin, and restart Codex. |
| Local ports are open but the tunnel is faulted | The process is running but control-plane authentication or the main channel probe failed. | Read the Manager log; do not treat the open port alone as ready. |

Use **Open Logs** for the combined lifecycle log and **Open Tunnel UI** for the
local tunnel status page. Never paste unredacted credentials into a chat, issue,
or committed document.

## Tunnel or credential rotation

When replacing a key or tunnel:

1. Create the replacement credential or tunnel.
2. Update the local tunnel profile and Manager Settings.
3. Choose **Restart All**.
4. Confirm authenticated readiness in the Manager.
5. Create or repoint **CodexPro-Safe Manager** in ChatGPT.
6. Restart Codex and verify an end-to-end tool call.
7. Only then revoke the old key, remove the old plugin, or delete the old
   tunnel.

Keep profile backups outside source control until the replacement is verified.
