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
because that process's helper environment cannot be verified.

The build produces `CodexProSafe.DiagnosticHelper.exe` and an exact SHA-256
manifest beside the Manager. The installer copies both files to the same
per-user application directory and runs the Manager's noninteractive sealing
mode, which persists the helper path, protocol, and fingerprint inside the
existing DPAPI-protected settings. On connector start, the Manager verifies the
saved values and file bytes while retaining a no-delete lock, then supplies the
contract through the connector environment. The connector never searches
`PATH`, and the helper never accepts a path argument. The package directory and
helper executable are opened without following reparse points; redirected,
unexpected-type, or multi-link package objects are rejected before launch.

This SHA-256 binding is an app-local substitution check, not a general
code-signing root. It assumes the Manager executable, its controlled launch
contract, and the current user's DPAPI-protected settings remain trusted. An
actor able to replace both that launch contract and the protected settings is
outside this boundary.

Read-only diagnostics are synthetic-test coverage only; they do not
verify or inspect this user's live runtime during installation, and maintenance
operations are not part of the setting.

Logs are redacted and stored under:

```text
%LOCALAPPDATA%\CodexProSafe Manager\logs
```

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
