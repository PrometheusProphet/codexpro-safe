# MCP Ecosystem and Responsibility Map

This document records stable architecture and repository boundaries for David's
local MCP environment. It intentionally does not record credentials, real
tunnel identifiers, private URLs, transient process IDs, or claims about what
is currently running.

For current machine-specific state, read the ignored local snapshot at:

```text
.ai-bridge/mcp-runtime-status.md
```

Verify that snapshot read-only before using it as operational evidence.

## Component responsibility

| Component | Stable responsibility | Does not own |
| --- | --- | --- |
| Codex | MCP client registration, tool visibility, plugin enablement, and user-facing approvals | Starting every external runtime or publishing local services remotely |
| Codex Plugins | Lifecycle and registration of plugin-provided MCPs | Ordinary local MCP containers or CodexPro-Safe's local connector process |
| ToolHive | Candidate general runtime for ordinary local MCPs: discovery, isolated workload lifecycle, local proxy endpoints, secrets injection, policy, health, and logs | Plugin-provided MCP lifecycle or the specialized CodexPro-Safe connector-plus-tunnel pair |
| CodexPro-Safe | Safe local connector exposing bounded repository inspection and handoff workflows | General installation or routing of every MCP |
| CodexPro-Safe Manager | Windows UI and lifecycle owner for the CodexPro-Safe connector plus OpenAI tunnel client | Replacing Codex Plugins or silently taking over arbitrary MCP processes |
| OpenAI tunnel client | Deliberate remote publication of a selected local HTTP or stdio MCP target and authenticated channel state | Package installation, general MCP discovery, or machine-wide lifecycle management |
| Individual MCP server | Its own tool contract, command/image, dependencies, minimum secrets, mounts, network access, and health behavior | Global client configuration or unrelated MCP credentials |

## MCP classes

### Plugin-provided MCPs

These remain owned by Codex Plugins. The Manager or another inventory surface
may display their presence but must not duplicate, start, stop, or overwrite
their plugin lifecycle.

### Ordinary local MCPs

ToolHive is the preferred candidate runtime after a workload-specific proof of
concept succeeds. Use separate localhost endpoints and direct Codex
registrations by default. This preserves clearer credentials, approvals,
failure domains, and rollback.

A shared router, group, or virtual MCP is optional and should be introduced only
for a measured client limitation, deliberate tool-catalog reduction, or a
defined common policy boundary. Do not route every MCP through one global
gateway by default.

### Connector-plus-tunnel MCPs

CodexPro-Safe remains under CodexPro-Safe Manager. It has a paired local
connector and OpenAI tunnel lifecycle, authenticated readiness semantics,
fail-closed process takeover, Windows startup behavior, and repository-access
policy that ordinary MCP runtime management does not replace.

Do not migrate CodexPro-Safe into ToolHive until ordinary workloads have proven
version pinning, mounts, secret redaction, health, restart, and rollback across
multiple update cycles.

## Repository map

Use `<github-workspace>` as the portable local repository root placeholder:

```text
<github-workspace>
```

The paths are navigation hints, not portable product configuration.

| Repository | Expected local path | Responsibility and authority |
| --- | --- | --- |
| `codexpro-safe` | `<github-workspace>\codexpro-safe` | CodexPro-Safe connector, Windows Manager, startup/tunnel documentation, and this ecosystem map |
| `codebase-memory-mcp` | `<github-workspace>\codebase-memory-mcp` | Codebase Memory MCP implementation used by project-specific registrations; inspect its current README and Git state before changes |
| `DarkPrometheus` | `<github-workspace>\DarkPrometheus` | WPF/Desktop product reference; its own `AGENTS.md` governs work there |
| `DarkPrometheus.Web` | `<github-workspace>\DarkPrometheus.Web` | Web product and product-contract workflows; its own `AGENTS.md` and `.ai-bridge` owners govern work there |
| `workflow-optimizer` | `<github-workspace>\workflow-optimizer` | Separate product repository with its own `AGENTS.md` and dedicated Codebase Memory registration |

Repository proximity does not grant edit scope. A task must explicitly include
another repository before changing it.

## Source-of-truth order

When facts conflict, use this order:

1. Current source and configuration in the owning repository
2. Current Git branch, status, and commit history
3. The owning repository's `AGENTS.md` and focused documentation
4. Current sanitized runtime inspection
5. `.ai-bridge/mcp-runtime-status.md`
6. Old handoffs, research reports, screenshots, and conversation memory

Runtime state can legitimately differ from tracked architecture. Report that
drift rather than rewriting stable documentation to match a transient incident.

## Safe adoption sequence

For a new ordinary local MCP:

1. Record the current sanitized MCP inventory.
2. Verify the repository, exact license, release/tag/digest, provenance, and
   transitive dependency posture.
3. Identify its required secrets, filesystem mounts, network destinations, and
   transport.
4. Begin with a disposable or non-production credential when one is required.
5. Run it alongside existing MCPs with the narrowest possible permissions.
6. Verify health, logs, tool discovery, and one real read-only tool call outside
   Codex when practical.
7. Add one explicit Codex registration; do not bulk-rewrite client
   configuration.
8. Restart Codex only when required to refresh the MCP registry.
9. Verify existing MCPs remain callable.
10. Test controlled stop, start, restart, removal, and rollback.
11. Update the ignored runtime snapshot.
12. Make an explicit go/no-go decision before migrating a production MCP.

Do not use CodexPro-Safe as the first migration candidate.

## Security boundary

- Prefer one process/container and one credential scope per ordinary MCP.
- Prefer direct per-MCP registration over a global credential-holding gateway.
- Pin immutable artifacts when possible.
- Treat registry metadata as discovery evidence, not automatic trust.
- Never grant broad writable repository mounts to an unreviewed MCP.
- Keep secrets in an approved encrypted provider and out of profiles, argv,
  logs, documentation, and chat.
- Use explicit network allowlists and remember that HTTP proxy controls do not
  cover every raw TCP or custom-protocol path.
- An open port is not authenticated readiness.
- A successful one-time tool call is not restart or rollback proof.
- Publish only deliberately selected MCPs through an OpenAI tunnel.

## Context maintenance

Update this tracked document only when stable ownership, repository boundaries,
or migration policy changes.

Refresh the ignored local runtime snapshot with:

```powershell
npm.cmd run status:mcp:windows
```

That command runs `scripts/refresh-mcp-runtime-status.ps1`. It performs
read-only Git, installed-file, process, and ToolHive CLI observations, then
atomically rewrites `.ai-bridge/mcp-runtime-status.md`. It does not read
encrypted Manager settings, environment values, credentials, tunnel
identifiers, private URLs, or provider configuration.

The script preserves the delimited **Manual verification ledger** in the
snapshot. Record real plugin tool calls, authenticated tunnel readiness,
lifecycle tests, rollback results, and unresolved interpretation there; those
facts cannot be established safely from process or file inspection alone.

Update `.ai-bridge/mcp-runtime-status.md` after:

- installing or upgrading an MCP runtime or Manager;
- adding, removing, or migrating an MCP;
- changing a launch owner;
- verifying or failing lifecycle/rollback;
- changing the Codex registration that a task depends on;
- discovering a material local runtime problem.

Do not copy the full contents of sibling repositories into this repository.
Route tasks to their authoritative files instead.
