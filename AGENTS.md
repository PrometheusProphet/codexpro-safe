# CodexPro-Safe Agent Instructions

This repository owns the CodexPro-Safe connector and its optional Windows
lifecycle application, CodexPro-Safe Manager. Treat current source and Git state
as authoritative over old conversations, remembered runtime state, or stale
handoffs.

## Startup and context routing

Before implementation, operational changes, research recommendations, or
cross-repository conclusions:

1. Run `git status --branch --short` and inspect the current branch and HEAD.
2. Read this file.
3. Read `docs/MCP_ECOSYSTEM.md`.
4. Read `docs/WINDOWS_MANAGER.md` when the task touches Windows startup,
   credentials, the OpenAI tunnel, the ChatGPT plugin, or Manager behavior.
5. When the task depends on what is installed, registered, running, connected,
   or already tested on David's desktop, read
   `.ai-bridge/mcp-runtime-status.md` if it exists.
6. Verify volatile facts read-only before acting. A status snapshot is context,
   not proof that the same process, version, port, registration, or health state
   still exists.
7. Before working in a related repository, switch to that repository and read
   its own `AGENTS.md` and current Git state. Do not apply this repository's
   implementation rules as a substitute for the target repository's rules.

Do not propose installing or repeating a proof of concept until the current
runtime snapshot and live registrations have been checked. Explicitly
distinguish a new experiment from verification or completion of an existing
experiment.

## Naming and ownership

- **CodexPro-Safe** is the connector, repository, and package.
- **CodexPro-Safe Manager** is the Windows notification-area lifecycle
  application and the corresponding ChatGPT plugin name.
- `codexpro-safe-local` is a local tunnel profile name. It does not rename the
  product or package.
- CodexPro-Safe Manager owns the specialized connector-plus-OpenAI-tunnel
  lifecycle.
- ToolHive may own ordinary local MCP workloads after each workload passes a
  bounded migration or proof-of-concept gate.
- Codex Plugins own plugin-provided MCPs. Do not duplicate their lifecycle in
  ToolHive or the Manager.

Use `docs/MCP_ECOSYSTEM.md` for stable responsibility boundaries and repository
relationships. Do not duplicate volatile machine state into tracked
documentation.

## Stable versus local context

Tracked documentation may contain:

- stable architecture and component responsibilities;
- repository names and ownership boundaries;
- supported setup and migration procedures;
- security, verification, and rollback contracts.

Ignored `.ai-bridge/` status files may contain sanitized local facts:

- installed versions and executable owners;
- MCP registrations and launch owners;
- last verified health or tool call;
- known local runtime problems;
- pending operational verification.

Never put credentials, API-key values, organization IDs, real tunnel IDs,
private URLs, raw environment values, or secret-bearing command lines in either
location. Do not commit `.ai-bridge/*`.

## Related repositories

The current local ecosystem includes:

- `codebase-memory-mcp`
- `DarkPrometheus`
- `DarkPrometheus.Web`
- `workflow-optimizer`

Their roles and expected local locations are summarized in
`docs/MCP_ECOSYSTEM.md`. The summary is navigation context only. Current files,
Git state, and the target repository's own instructions remain authoritative.

Do not edit another repository from a CodexPro-Safe task unless the user
explicitly places that repository in scope. Do not copy source between
repositories without inspecting license, provenance, and target-repository
rules.

## Operational safety

For Manager, tunnel, ToolHive, Docker/Podman, Codex registration, or MCP
lifecycle work:

- inspect before mutating;
- identify the exact launch/configuration owner;
- preserve unrelated MCPs and registrations;
- make one bounded change at a time;
- state interruption and restart consequences before stopping a live service;
- use least-privilege credentials, mounts, roots, tools, and network access;
- verify health and one real tool call separately;
- define rollback before migrating a production MCP;
- do not claim readiness from an open port alone;
- do not treat a plugin being visible as proof its tools are callable;
- do not treat a callable tool as proof lifecycle restart and rollback have
  passed.

Creating, deleting, or rotating provider credentials, tunnels, plugins, or
organization settings requires explicit user authorization. Installing or
changing Docker, Podman, WSL, Hyper-V, services, startup tasks, or machine-wide
packages also requires explicit authorization.

## Repository changes and verification

Preserve unrelated dirty work. Use targeted edits and stage only task-owned
files.

Unless the user asks for review-only or explicitly says not to publish, a
completed repository change includes focused verification, staging only
task-owned files, committing, and pushing the current branch when safe. Do not
leave completed work uncommitted merely because the user did not separately ask
for a commit or push. Stop before commit or push only when verification fails,
task-owned and unrelated changes cannot be separated safely, authentication or
branch protection blocks publication, the work is incomplete, or the user asks
to review first. Report that exception prominently.

For connector source changes, follow the repository's existing build, smoke,
and focused-test guidance. For Manager source changes, run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tools\CodexProSafe.Manager\build.ps1
```

That build includes the Manager self-test unless explicitly skipped.

For documentation-only changes, run at minimum:

- `git diff --check`
- a focused link/reference review
- a credential and machine-specific identifier scan
- full and staged diff review before commit

Do not commit generated Manager `bin/` or `obj/` output. Do not publish the npm
package, create a release, mutate provider infrastructure, or deploy anything
unless the user separately authorizes that outcome.
