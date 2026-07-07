# Security Policy

CodexPro exposes a local workspace to an MCP client. Treat it like a developer tool with access to your source tree, not like a hosted SaaS app.

## Supported Version

Security fixes target the latest published version only until the project reaches `1.0.0`.

## Reporting

Please report security issues privately before opening a public issue. If the repository has GitHub private vulnerability reporting enabled, use that. Otherwise contact the maintainer listed by the project owner.

Do not include secrets, private repository contents, tunnel tokens, or `.env` values in reports.

## Terms Boundary

CodexPro is not designed to bypass, avoid, pool, resell, or modify ChatGPT, Codex, OpenAI, or third-party model limits. Do not market, deploy, or configure it that way.

Each user should connect their own ChatGPT account, use only product surfaces available to that account, and follow the limits, safety rules, and terms for ChatGPT, Codex, OpenAI, and any third-party model provider they connect.

## Threat Model

CodexPro can expose:

- file metadata and selected file contents from allowed workspaces
- git status and diffs
- `.ai-bridge` planning files
- optional shell command execution through the `bash` tool
- optional write/edit capability depending on `CODEXPRO_WRITE_MODE`
- optional prompt-file saves through `save_prompt_file`, limited to fixed Markdown/text prompt directories
- optional local handoff execution through `codexpro execute-handoff`, run from the user's terminal only

The main risks are:

- connecting an untrusted MCP client
- exposing the server through a public tunnel without auth
- running with `CODEXPRO_BASH_MODE=full`
- running with `CODEXPRO_WRITE_MODE=workspace` on an important repo
- executing an untrusted `.ai-bridge/current-plan.md` or custom `execute-handoff --command`
- adding overly broad allowed roots
- enabling query-token URLs and then leaking a `codexpro_token`
- reusing saved profiles without reviewing the target workspace
- trusting a downloaded `cloudflared` binary without understanding where it came from

## Safer Defaults

Default daily mode:

```bash
codexpro start \
  --root /path/to/repo \
  --mode handoff \
  --write handoff \
  --bash off \
  --tunnel none
```

In the default standard/handoff posture, the advertised source-inspection path is `search` -> `source_outline` -> small `read_source_lines` ranges. Generic `read` is full-mode compatibility, `bash` is hidden when bash mode is off, and generic `write`/`edit` are hidden unless workspace writes are explicitly enabled. Hidden tools still keep their server-side policy checks if called by a full-mode or stale client.

For local false-positive investigation, `CODEXPRO_LOG_TOOL_CALL_DETAILS=1` writes sanitized correlation logs only. These logs use workspace ids or hashes, path hashes/extensions, line ranges, byte counts, redaction counts when available, and mode summaries. They do not log raw file contents, prompt bodies, auth tokens, query strings, private absolute paths, or raw tool arguments. If no local correlation id appears for a blocked call, the host likely blocked the call before invoking the server.

Explicit source-editing mode for a trusted local repo:

```bash
codexpro start \
  --root /path/to/repo \
  --mode agent \
  --write workspace \
  --bash safe \
  --tunnel none
```

For stable public hostnames, keep the CodexPro auth token stable but private:

```bash
codexpro start \
  --root /path/to/repo \
  --tunnel cloudflare-named \
  --hostname codexpro.example.com \
  --tunnel-name codexpro \
  --token <long-random-token> \
  --mode handoff \
  --write handoff \
  --bash off
```

## Hard Rules

- Do not run public tunnels with `--no-auth`.
- Public tunnel mode and non-loopback binds fail closed if `CODEXPRO_HTTP_TOKEN` is missing.
- Prefer `Authorization: Bearer <token>`. Query-token URLs are disabled by default; use `CODEXPRO_ALLOW_QUERY_TOKEN=1` or `--allow-query-token` only for compatibility.
- Do not commit printed connector URLs that include `codexpro_token`.
- Do not commit Cloudflare tunnel tokens.
- Use `--mode handoff` for planning workflows where ChatGPT should not edit source files.
- In handoff mode, generic `write` and `edit` stay limited to `.ai-bridge/`. `save_prompt_file` may save generated prompts only under `.ai-bridge/prompts/`, `docs/chatgpt/generated-prompts/`, or `docs/loop/inbox/`; it is not a source-editing tool.
- Preview local handoff execution with `codexpro execute-handoff --dry-run` before running an unfamiliar adapter or custom command.
- Keep `execute-handoff` local. Do not wrap it in a remote MCP tool unless you add a stronger approval and sandbox story.
- Use `--mode agent --write workspace` only with trusted ChatGPT sessions and repo-specific roots.
- Use `--bash safe` or `--bash full` only for trusted local repos. Package-manager scripts can execute repository code; safe mode is not a sandbox.
- Prefer a repo-specific `--root` instead of `--allow-home`.
- Saved profiles are not loaded by default during `codexpro start`; pass `--profile` only after reviewing the saved workspace settings.
- Use `--cloudflared <path>` if your organization requires a managed Cloudflare Tunnel binary.

## Cloudflare Binary Install

CodexPro can download the official Cloudflare `cloudflared` release into `~/.codexpro/bin` on supported macOS, Windows, and Linux systems only when explicitly requested with `codexpro install-cloudflared` or `--install-cloudflared`. It does not install a system service, does not use sudo/admin rights, and does not modify shell startup files.

Resolution order:

```text
1. explicit --cloudflared path or CLOUDFLARED_BIN
2. cloudflared already available in PATH
3. ~/.codexpro/bin/cloudflared or cloudflared.exe
4. download official Cloudflare latest release only with install-cloudflared or --install-cloudflared
```

Use `--install-cloudflared` to refresh the local binary during startup, or run `codexpro install-cloudflared` as a separate explicit step.

## Built-In Guards

CodexPro blocks common sensitive paths by default:

- `.env` and `.env.*`
- `.git` internals
- `node_modules`
- common private key names
- npm, Python, cloud, Kubernetes, Docker, and service-account credential files
- local database/state files such as `*.sqlite`, `*.sqlite3`, and `*.db`
- build/cache folders such as `dist`, `build`, `.next`, `coverage`, `.cache`
- symlink/junction traversal by default, even when it resolves inside the workspace

These guards reduce risk. They are not an OS sandbox.
