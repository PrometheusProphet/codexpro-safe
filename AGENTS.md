# CodexPro-Safe Router

This repository owns the CodexPro-Safe package/connector and CodexPro-Safe
Manager, the optional Windows notification-area lifecycle application. Current
source and Git state define behavior; preserve the Safe defaults documented in
`README.md` and `SECURITY.md`.

## Routing and stable ownership

Read `docs/MCP_ECOSYSTEM.md` for stable responsibility boundaries and
`docs/WINDOWS_MANAGER.md` for Manager, Windows startup, credentials, OpenAI
tunnel, plugin, or Manager behavior. Ignored `.ai-bridge/` is sanitized local
runtime state only: never commit it or promote a snapshot into proof.

CodexPro-Safe is the product/package; CodexPro-Safe Manager owns its specialized
connector-plus-OpenAI-tunnel lifecycle. `codexpro-safe-local` is only a local
tunnel profile name. ToolHive owns only ordinary MCP workloads that pass their
own bounded migration/proof gate, and plugins retain their plugin-provided MCP
lifecycle. Nearby DarkPrometheus repositories are retained reference/history,
not active development targets.

## Product-specific safety

For Manager, tunnel, registration, or MCP lifecycle work, identify the actual
launch/configuration owner, preserve unrelated registrations, make one bounded
change, state interruption/restart consequences, and verify health and a real
tool call separately. Never treat visibility, an open port, or a stale snapshot
as callable/restart/rollback proof. Provider credentials, tunnels, plugins,
organization settings, machine-wide software, services, or startup tasks retain
their product-specific consequence boundaries. Use least-privilege credentials,
mounts, roots, tools, and network access; define rollback before migrating a
production MCP.

## Proof and delivery

Build the Manager with:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\build.ps1
```

For documentation-only work, run `git diff --check`, focused link/reference
checks, credential/machine-ID scans, and complete task-owned unstaged/staged
diff review. Use package/release checks when package contents or publication
consequences are in scope. Distinguish stable tracked documentation from local
runtime facts. CodexPro-Safe uses the workspace direct-main delivery rule; package
publication or release remains a separate explicit consequence.
