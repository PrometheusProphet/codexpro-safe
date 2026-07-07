<p align="center">
  <img src="docs/favicon.svg" width="72" height="72" alt="CodexPro logo">
</p>

<h1 align="center">CodexPro Safe</h1>

<p align="center">
  Let ChatGPT web see your Codex-style repo context and act like a local coding agent.
</p>

<p align="center">
  <a href="https://www.npmjs.com/package/codexpro-safe"><img alt="npm" src="https://img.shields.io/npm/v/codexpro-safe?style=flat-square"></a>
  <a href="https://github.com/rebel0789/codexpro/actions"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/rebel0789/codexpro/ci.yml?branch=main&style=flat-square"></a>
  <a href="https://github.com/rebel0789/codexpro/blob/main/LICENSE"><img alt="License" src="https://img.shields.io/github/license/rebel0789/codexpro?style=flat-square"></a>
  <a href="https://rebel0789.github.io/codexpro/"><img alt="Website" src="https://img.shields.io/badge/site-GitHub%20Pages-67e8f9?style=flat-square"></a>
</p>

<p align="center">
  <a href="https://rebel0789.github.io/codexpro/">Website</a>
  ·
  <a href="README_ZH.md">中文 README</a>
  ·
  <a href="https://rebel0789.github.io/codexpro/zh.html">中文网站</a>
  ·
  <a href="https://github.com/rebel0789/codexpro">Star on GitHub</a>
  ·
  <a href="https://www.npmjs.com/package/codexpro">npm</a>
  ·
  <a href="DOMAIN_SETUP.md">Stable URL guide</a>
  ·
  <a href="FAQ.md">FAQ</a>
  ·
  <a href="SECURITY.md">Security</a>
</p>

CodexPro Safe is a safety-first fork of CodexPro. It keeps the upstream MIT license and attribution, but changes the default posture to local-only handoff planning: bash off, source writes limited to `.ai-bridge`, no public tunnel, no query-token URL, no symlink traversal, and no saved-profile reuse unless explicitly requested.

CodexPro is not a rate-limit bypass. It uses ChatGPT's official Developer Mode and MCP app path to connect your own ChatGPT session to your own local repo. ChatGPT and Codex remain separate product surfaces, each subject to its own plan limits, safety rules, and availability.

If one workflow is unavailable and another product surface you already have access to is still available, CodexPro lets you keep working against the same local repo without modifying or evading either product's limits.

```bash
npm install -g codexpro-safe
codexpro-safe start --root /path/to/repo
```

## Why

```text
ChatGPT web can see Codex-style context:
  AGENTS.md
  .ai-bridge plans and status
  git status and diff
  selected source files

ChatGPT web can act on your repo:
  read files
  write files
  exact-edit files
  search code
  run safe verification commands

Codex stays useful:
  execute plans locally
  handle deeper terminal-heavy work
  review or continue a handoff
```

What it gives you:

```text
Normal coding mode  ChatGPT reads, writes, edits, searches, and verifies directly.
Handoff mode        ChatGPT writes .ai-bridge/current-plan.md for a local implementation agent.
Pro planning mode   Export a durable context bundle for sessions that cannot call MCP tools.
Stable URLs         Use an ngrok free dev domain or Cloudflare named tunnel so the ChatGPT app URL stays fixed.
```

If your ChatGPT account exposes a stronger model in the web app, and that model/surface can call Developer Mode apps, CodexPro lets it work against your local repo through MCP. Some ChatGPT model surfaces may not be able to call connectors or MCP tools directly. CodexPro does not provide, proxy, resell, or unlock models; it gives compatible ChatGPT sessions local coding tools and repo context.

CodexPro is not an OS sandbox. It is a local developer bridge with safety defaults. Read [SECURITY.md](SECURITY.md) before exposing it through a tunnel.

## Requirements

```text
Node.js 20+
ChatGPT Plus or Pro account with Apps / Developer Mode access
Developer mode enabled from Settings -> Apps -> Advanced settings
Enforce CSP in developer mode kept enabled
One public tunnel option: Cloudflare quick tunnel, ngrok free dev domain, or Cloudflare named tunnel
```

Current testing shows free / Go ChatGPT accounts do not expose the app flow needed for CodexPro. Use Plus or Pro for the best experience.

Account tier and model tool support are separate things. Plus/Pro can expose Apps / Developer Mode, but a specific model surface may still be unable to call the connector. Use Pro context fallback for those sessions.

## Status

CodexPro Safe is a public open-source MCP bridge with conservative defaults: local-only handoff mode, bash off, blocked secret paths, token-protected public URLs when tunnels are explicitly enabled, and custom ChatGPT HTML tool cards off by default.

CodexPro does not bypass, avoid, increase, pool, resell, or modify ChatGPT, Codex, OpenAI, or third-party model limits. It does not provide models or account access. It only exposes local repo tools to the ChatGPT session the user already controls through official MCP and Developer Mode.

ChatGPT can do MCP-backed agentic coding in your local repo, while Codex remains available for terminal execution, review, or handoff workflows. Model, tool, and quota behavior are controlled by the product and account you connect CodexPro to.

### Compliance boundary

CodexPro is designed for the official ChatGPT Developer Mode / MCP app path:

- It exposes local workspace files, git state, safe verification commands, and `.ai-bridge` handoff files selected by the user.
- It does not ask for raw ChatGPT transcripts or broad conversation history. Context exports use explicit workspace files and bounded previews.
- It does not scrape or act as pass-through middleware for third-party services unless the user connects an authorized local integration that follows that service's terms.
- It does not automate ChatGPT, Codex, or terminal approval flows to bypass product security, rate limits, quota limits, account access, or review prompts.
- Remote MCP tools do not execute Codex/OpenCode/Pi/local agents. Agent execution is a separate user-started CLI/watch process on the user's machine.

Relevant OpenAI references: [ChatGPT Developer Mode](https://developers.openai.com/api/docs/guides/developer-mode), [MCP servers for ChatGPT Apps](https://developers.openai.com/api/docs/mcp), and [Apps SDK submission guidelines](https://developers.openai.com/apps-sdk/app-submission-guidelines).

## Tools exposed to ChatGPT

CodexPro defaults to `CODEXPRO_TOOL_MODE=standard`, which keeps ChatGPT's tool picker focused on the normal coding loop plus handoff/export workflows. Use `--tool-mode minimal` for the tightest demo surface, or `--tool-mode full` when you want every compatibility and debugging tool exposed.

The smaller default tool list is deliberate. ChatGPT behaves better when routine work goes through a few high-signal tools instead of a large action catalog. Installed user/plugin skills are still discovered during workspace open; they are surfaced as structured workspace context and can be loaded on demand with `load_skill`, not exposed as dozens of separate ChatGPT actions.

Standard mode exposes:

- `server_config` — show safety modes, limits, blocked globs, and allowed roots.
- `codexpro_self_test` — run one local-only diagnostic for modes, expected tools, safe bash policy, `.ai-bridge` write/edit, and selected-only Pro context.
- `open_current_workspace` — open the configured default workspace without accepting a path. Fastest/safest first call.
- `open_workspace` — open a local project directory using `root` or `path` and return workspace id, git status, AGENTS.md status, optional skill discovery, and optional file tree.
- `tree` — inspect files.
- `search` — search code with ripgrep or a Node fallback.
- `source_outline` — inspect one source file's metadata, imports/exports, top-level symbols, and optional query anchors without returning the full file.
- `read_source_lines` — read a small bounded, line-numbered source range without Markdown code fences.
- `load_skill` — load bounded `SKILL.md` instructions for a discovered workspace, user, or plugin skill by name, with optional source/path disambiguation.
- `show_changes` — one review-oriented summary with git status, diff stats, and optional diff.
- `read_handoff` — read `.ai-bridge` files.
- `save_prompt_file` — save generated Codex prompts as Markdown/text in fixed prompt-only directories; works in handoff mode without enabling source writes.
- `export_pro_context` — write `.ai-bridge/pro-context.md` for models that cannot call MCP tools directly.
- `handoff_to_agent` — write `.ai-bridge/current-plan.md` for Codex, OpenCode, Pi, or a custom local implementation agent without executing local commands.

`read`, `write`, `edit`, and `bash` are compatibility/advanced tools. In minimal and standard mode, CodexPro advertises them only when the current safety modes make them appropriate: `bash` is hidden when bash mode is off, and generic `write`/`edit` are hidden unless workspace writes are explicitly enabled. Full mode exposes the advanced catalog for trusted debugging and compatibility.

Minimal mode exposes only:

```text
server_config
codexpro_self_test
open_current_workspace / open_workspace
source_outline / read_source_lines
show_changes
```

Full mode adds:

- `codexpro_inventory` — list discovered skill names and configured MCP server names without exposing MCP command arguments or secrets.
- `list_workspaces` — show opened workspaces in the current MCP session.
- `workspace_snapshot` — project status plus `.ai-bridge` handoff context.
- `git_status` — inspect git status.
- `git_diff` — inspect current diff.
- `codex_context` — load Codex-style context in one call: AGENTS instructions for a target path, `.ai-bridge` files, and optional git status/diff.
- `handoff_to_codex` — compatibility wrapper for `handoff_to_agent` with `agent=codex`.

Local-only companion command:

- `codexpro execute-handoff` — run a previously written `.ai-bridge/current-plan.md` through a local agent, then collect status, logs, and git diff. This is intentionally a CLI command, not a remote MCP tool.
- `codexpro watch-handoff` — watch `.ai-bridge/current-plan.md` locally and run a new plan through a configured agent when its content hash changes. This is also CLI-only and is not exposed as a remote MCP tool.

The watcher is the safer way to automate handoff execution from ChatGPT Web. ChatGPT writes the plan through `handoff_to_agent`; the user-started local watcher notices the new plan and runs Pi, OpenCode, Codex, or a restricted custom command from the terminal:

```bash
codexpro start --mode handoff
codexpro watch-handoff --agent opencode --model provider/model --yes
```

For custom local agents:

```bash
codexpro watch-handoff \
  --agent custom \
  --command "node ./agent.js --task-file {{plan_file}}" \
  --yes
```

Useful watcher flags:

```text
--once                  check one new plan and exit
--dry-run               show the command without executing it
--poll-interval-ms 2000 polling interval
--debounce-ms 500       wait for the plan file to become stable
--state-file <path>     duplicate-run state, default .ai-bridge/watch-handoff-state.json
```

The watcher writes the same review files as `execute-handoff`:

```text
.ai-bridge/agent-status.md
.ai-bridge/implementation-diff.patch
.ai-bridge/execution-log.jsonl
```

## Visual ChatGPT cards

CodexPro keeps custom ChatGPT HTML cards opt-in. By default, CodexPro does not advertise `_meta.ui.resourceUri` or `_meta["openai/outputTemplate"]` on tool descriptors, and it does not register the bundled widget resource. Tool text, structuredContent, errors, source inspection data, diffs, and verification output are unchanged.

Use `--tool-card-mode compact` or `CODEXPRO_TOOL_CARD_MODE=compact` to restore the bundled Apps SDK widget resource:

```text
ui://widget/codexpro-tool-card-v9.html
```

In compact mode, CodexPro tool descriptors attach that resource through `_meta.ui.resourceUri` and the ChatGPT compatibility key `_meta["openai/outputTemplate"]`. In ChatGPT Developer Mode this renders compact cards for:

```text
server_config and codexpro_self_test
open_current_workspace / open_workspace project summaries
codexpro_inventory, list_workspaces, workspace_snapshot
tree, search, load_skill, read
write/edit diffs
bash verification commands
git_status, git_diff, show_changes review summaries
read_handoff, codex_context
prompt saves and handoff/pro-context exports
```

Compact cards fold or bound git details, discovered skills, file trees, terminal output, context bundles, and raw diffs so the chat does not fill with project inventory unless you open it.

ChatGPT may still show platform-owned generic tool-call UI or transcript chrome depending on the host UI and model behavior. CodexPro can suppress only its own custom HTML widget advertisement; the ChatGPT client controls final transcript rendering.

The visual cards are not unlocked by "normal coding mode" alone; the MCP server has to register an HTML resource with `text/html;profile=mcp-app` and point tool descriptors at it.

The widget sets both domain and CSP metadata surfaces:

```text
_meta.ui.domain
_meta["openai/widgetDomain"]
_meta.ui.csp
_meta["openai/widgetCSP"]
```

`CODEXPRO_WIDGET_DOMAIN` defaults to `https://rebel0789.github.io` for this package and is used when compact cards are enabled. For app submission, set it to a dedicated HTTPS origin you control, for example `https://widgets.yourdomain.com`. The CSP lists are intentionally strict because the widget has no external fetches, fonts, scripts, images, or iframes.

After upgrading or changing widget metadata, open the CodexPro app settings in ChatGPT Developer Mode and click `Refresh` / `Refresh actions` so ChatGPT reloads the tool descriptors and resource URI.

## Install

Recommended install:

```bash
npm install -g codexpro-safe
```

First run from the repo you want ChatGPT to work on:

```bash
codexpro-safe start --root /absolute/path/to/your/repo
```

Compatibility alias:

```bash
codexpro start --root /absolute/path/to/your/repo
```

No-install fallback:

```bash
npx codexpro-safe@latest start --root /absolute/path/to/your/repo
```

From source:

```bash
cd codexpro-safe
npm ci --ignore-scripts
npm run build
```

## CodexPro Start

From the project folder you want ChatGPT to work on:

```bash
codexpro start --root /absolute/path/to/your/repo
```

That is the safe first-run path. It:

```text
- uses the current folder as the workspace root
- starts the local HTTP MCP server
- uses local-only tunnel mode unless you pass a public tunnel flag
- uses handoff mode with source writes limited to .ai-bridge
- keeps bash disabled unless you pass --bash safe or --bash full
- does not load saved profiles unless you pass --profile
- supports Cloudflare quick tunnel, ngrok free dev domain, or Cloudflare stable tunnel only when explicitly requested
- requires bearer-token auth for public tunnels and non-loopback binds
- keeps query-token URLs disabled unless --allow-query-token or CODEXPRO_ALLOW_QUERY_TOKEN=1 is set
- shows a compact terminal control panel
- lets you press Enter to open ChatGPT in your browser
- lets you press `o` to open a local setup/status page
```

Explicit source-editing mode for a trusted local repo:

```bash
codexpro start --root /absolute/path/to/your/repo --mode agent --write workspace --bash safe
```

## ChatGPT app setup

Before you paste the CodexPro URL, turn on Developer Mode in ChatGPT:

```text
ChatGPT Settings
-> Apps
-> Advanced settings
-> Developer mode: on
-> Enforce CSP in developer mode: on
-> Create app
```

This is a one-time ChatGPT setting. Keep CSP enabled; CodexPro widgets are built for that path.

In Create App, use:

```text
Name: CodexPro
Description: Local workspace bridge for ChatGPT coding
Connection: Server URL
Server URL: paste the copied URL
Authentication: Authorization header / Bearer token when a token is configured; otherwise No Authentication / None
```

CodexPro Safe prefers `Authorization: Bearer <token>`. It does not put the private token in copied URLs unless query-token mode is explicitly enabled for compatibility.

Keep the terminal running while ChatGPT uses the connector. When you stop it, the quick-tunnel URL stops working.

If `cloudflared` is missing, install it yourself, pass `--cloudflared <path>`, or explicitly run:

```bash
codexpro install-cloudflared
```

OS behavior:

```text
macOS    copies with pbcopy when available, opens ChatGPT with open
Windows  copies with clip when available, opens ChatGPT with start
Linux    opens ChatGPT with xdg-open when available
```

Linux clipboard copy requires one of `wl-copy`, `xclip`, or `xsel`. If none is installed, CodexPro prints the URL clearly so it can be copied manually.

First-run tunnel choice:

```text
cloudflare  Cloudflare quick tunnel. Easiest demo path, new URL each restart.
ngrok       ngrok free dev domain. Recommended stable URL for most users.
stable      Cloudflare named tunnel. Stable URL with your own Cloudflare domain.
local       No public tunnel. Only for local MCP clients.
```

If you use quick mode, the Server URL changes every time the tunnel restarts. That means you must update the ChatGPT app Server URL each time. Use quick mode for demos, not daily work.

Public-tunnel daily path: create a free ngrok account, use the dev domain assigned to your account, optionally save it with `codexpro setup --save-config`, and keep the same ChatGPT app Server URL across restarts.

Saved profiles are opt-in. To create one from setup and use it later:

```bash
codexpro setup --save-config
codexpro start --profile
```

Cross-workspace profile reuse is explicit through `codexpro settings use --from-root /path/to/another/repo`.

If you are running this repository from source instead of npm:

```bash
npm run connect -- --root /absolute/path/to/your/repo
```

Guided onboarding:

```bash
codexpro setup
```

`setup` asks for the workspace folder, local port, mode, and public URL strategy, then prints the exact `codexpro start ...` command and can launch it immediately. Saving the selected tunnel provider, hostname, local port, mode, and generated CodexPro auth token under `~/.codexpro/profiles/` is opt-in with `--save-config`.

From a source checkout:

```bash
npm run connect:setup
```

Preflight diagnostics:

```bash
codexpro doctor
```

`doctor` does not start the MCP server or open a tunnel. It checks the local package build, Node version, workspace profile, port availability, tunnel prerequisites, clipboard support, and browser-open support. Run it before filing setup bugs or before recording a demo.

Use `--no-copy-url` if you do not want CodexPro to copy the connector URL. Add `--open-chatgpt` if you want the browser to open automatically instead of pressing Enter.

Local setup/status page:

```text
press o in the CodexPro terminal control panel
```

The page shows the active workspace, local MCP endpoint, safety modes, allowed roots, and the exact ChatGPT setup steps. It is served by the local CodexPro process and stays token-protected when auth is enabled.

Saved workspace profile behavior:

```text
codexpro setup --save-config
  choose quick, stable, ngrok, or local
  enter the Cloudflare/ngrok hostname when needed
  accept the generated CodexPro auth token
  save the profile

future codexpro start --profile
  loads the saved profile for the current folder
  reuses the saved tunnel provider, hostname, port, mode, and token
```

If you opt into a saved ngrok or Cloudflare stable profile, CodexPro prints the saved hostname and reminds you to use:

```bash
codexpro start --profile
```

Use `codexpro setup --save-config` again only when you want to change the port, mode, tunnel provider, hostname, or CodexPro auth token.

Useful profile flags:

```bash
codexpro start --profile         # load saved profile for this run
codexpro setup --no-save-config  # run setup without saving
codexpro setup --save-config     # explicitly save setup choices
```

Workspace settings:

```bash
codexpro settings
codexpro settings show
codexpro settings list
codexpro settings set --tunnel ngrok --hostname your-domain.ngrok-free.dev
codexpro settings use --from-root /path/to/another/repo
codexpro settings set --tunnel cloudflare
codexpro settings delete --yes
```

Use `codexpro settings` when you want to create a saved ngrok or Cloudflare preference, explicitly reuse a saved setup from another repo, or delete the saved workspace preference. The saved token is redacted when settings are shown.

Terminal controls:

```text
Enter  open ChatGPT connector settings in your browser
c      copy Server URL again
o      open local setup/status page
h      show controls
q      stop CodexPro
```

Advanced controls such as `u` for printing the full URL, `p` for Create App fields, and `m` for mode help are still available through `h`.

Startup modes:

```bash
codexpro start                 # local-only handoff mode: bash off, source writes blocked
codexpro start --agent --write workspace --bash safe
                               # explicit coding mode for trusted repos
codexpro setup                 # guided onboarding for new users
codexpro start --mode handoff  # planning-only .ai-bridge handoff
codexpro start --mode pro      # export context for models without MCP tools
codexpro stable --hostname codexpro.example.com --tunnel-name codexpro
codexpro ngrok --hostname your-domain.ngrok-free.dev
```

## Easiest run mode

This is the lightweight launcher so you do not have to manually start the MCP server and copy/paste multiple fields by hand.

If you are running from source, use `npm run connect -- --root /absolute/path/to/your/repo`.

By default this:

```text
- starts the local HTTP MCP server
- uses local-only tunnel mode
- uses CODEXPRO_WRITE_MODE=handoff so source writes are blocked
- uses CODEXPRO_BASH_MODE=off
- keeps query-token URLs disabled
- does not install tunnel binaries
```

In ChatGPT Developer Mode, use the printed fields:

```text
Name: CodexPro
Connection: Server URL
Server URL: http://127.0.0.1:8787/mcp
Authentication: No Authentication / None for local-only without a token; Authorization header / Bearer token when a token is configured
```

Planning-only handoff mode:

```bash
codexpro start \
  --root /absolute/path/to/your/repo \
  --mode handoff \
  --write handoff \
  --bash off \
  --tunnel none
```

In handoff mode, ChatGPT can create a plan for a local implementation agent without getting direct source-write access. Use `handoff_to_agent` from ChatGPT with `agent=opencode`, `agent=pi`, `agent=codex`, or a custom agent id. CodexPro writes:

```text
.ai-bridge/current-plan.md
.ai-bridge/agent-status.md
.ai-bridge/implementation-diff.patch
.ai-bridge/execution-log.jsonl
```

Then run the implementation locally with `codexpro execute-handoff`:

```bash
codexpro execute-handoff --agent opencode --model provider/cheap-model
```

Dry-run first if you want to inspect the exact command:

```bash
codexpro execute-handoff --agent opencode --model provider/cheap-model --dry-run
```

Pi adapter:

```bash
codexpro execute-handoff --agent pi --model provider/cheap-model
```

Custom adapter:

```bash
codexpro execute-handoff \
  --agent custom \
  --command "my-agent --model {{model}} --task-file {{plan_file}}" \
  --model provider/cheap-model
```

Template placeholders:

```text
{{model}}      model passed with --model
{{plan_file}}  absolute path to .ai-bridge/current-plan.md
{{plan_text}}  full plan text as one argument
{{root}}       workspace root
```

By default, `execute-handoff` asks for local confirmation before running. Use `--yes` only in trusted scripts. After execution, CodexPro writes:

```text
.ai-bridge/agent-status.md
.ai-bridge/implementation-diff.patch
.ai-bridge/execution-log.jsonl
```

Then let ChatGPT review those files through `read_handoff` or `codex_context`.

Manual fallback:

```bash
opencode run --model provider/cheap-model "$(cat .ai-bridge/current-plan.md)"
git diff --no-ext-diff -- > .ai-bridge/implementation-diff.patch
```

For debugging whether ChatGPT is actually reaching the local server, add:

```bash
--log-requests
```

To open ChatGPT settings automatically:

```bash
codexpro start --root /absolute/path/to/your/repo --open-chatgpt
```

To explicitly install `cloudflared` for public tunnel use:

```bash
codexpro install-cloudflared
```

Request logs print method, path, status, and duration. CodexPro also logs tool name, success/error state, and duration as `[CodexProTool] ...` lines. Query strings, file contents, and prompts are not logged, so query-token compatibility mode and source content are not printed. For false-positive investigation, `CODEXPRO_LOG_TOOL_CALL_DETAILS=1` adds local-only sanitized tool-call lines with correlation ids, path hashes/extensions, line ranges, byte counts, redaction counts when available, and mode summaries. If a ChatGPT-side block happens and no matching local correlation log appears, the block likely happened before the server received the call.

For faster ChatGPT runs, keep the first call narrow:

```text
Call open_current_workspace with include_tree=false unless you need the tree immediately.
Use tree with max_depth=2 and max_entries=100 when you need file structure.
Use load_skill only for the specific discovered skill needed for the task.
Use --tool-mode full and call codexpro_inventory only when you want ChatGPT to see full global skill and MCP server inventory.
Do not call open_workspace after open_current_workspace unless you are switching to a different root.
Use search/source_outline first, read_source_lines only for small bounded ranges, one targeted search plus show_changes for review, and bash only for focused build/test/lint verification.
```

`open_current_workspace` and `open_workspace` discover workspace, user, and plugin skills by default. Use `include_global_skills=false` when you only want repo-local instructions, or `include_skills=false` when you want the fastest possible open call. `load_skill` only accepts a discovered skill name plus optional source and exact displayed path, then reads that skill's `SKILL.md` with a bounded byte limit; it does not accept arbitrary file paths. If multiple discovered skills still match, CodexPro returns an ambiguity error instead of guessing. `workspace_snapshot` stays narrower by default for speed. In `--tool-mode full`, use `codexpro_inventory` for global/user/plugin skills and MCP server names. `codexpro_inventory` reports names/descriptions and sanitized paths only; it does not expose MCP command arguments or environment values.

## Codex-style context

CodexPro is not reading Codex's private runtime memory. It gives ChatGPT explicit workspace context through tools:

```text
open_current_workspace  root, safety mode, AGENTS.md status, git status
codex_context           AGENTS chain, .ai-bridge handoff files, optional git status/diff
read_handoff            .ai-bridge files only
workspace_snapshot      larger project snapshot plus .ai-bridge context
```

`codex_context` is the closest match to "load what Codex should know." It reads AGENTS-style instruction files from the workspace root down to a target path:

```text
AGENTS.override.md
AGENTS.md
agents.md
.agents.md
```

Then it adds:

```text
.ai-bridge/current-plan.md
.ai-bridge/agent-status.md
.ai-bridge/implementation-diff.patch
.ai-bridge/codex-status.md
.ai-bridge/decisions.md
.ai-bridge/open-questions.md
.ai-bridge/execution-log.jsonl
git status
optional git diff
```

Use it before planning or review:

```text
Call open_current_workspace with include_tree=false.
Call codex_context with target_path="src/App.tsx" and include_diff=false.
Then inspect only the files needed for the task.
```

This keeps ChatGPT closer to Codex's instruction model without hidden state, browser memory, or repeated broad file scans.

Demo/Codex-like mode, where ChatGPT can use `write` and `edit` on source files:

```bash
codexpro start \
  --root /absolute/path/to/your/repo \
  --mode agent \
  --write workspace \
  --bash safe \
  --tunnel none
```

Local-only mode, for local MCP clients that can reach `127.0.0.1` directly:

```bash
codexpro start --root /absolute/path/to/your/repo --tunnel none
```

The local endpoint is usually:

```text
http://127.0.0.1:8787/mcp
```

## Pro context fallback

Some ChatGPT models or product surfaces may not be able to call Developer Mode apps, connectors, or MCP tools directly. This can include stronger planning-model surfaces even when the same ChatGPT account can create and use the CodexPro app from other chats. When that happens, use a durable context bundle instead of fighting the tool boundary.

Generate a bundle:

```bash
codexpro pro-bundle --root /absolute/path/to/your/repo --copy
```

This writes:

```text
.ai-bridge/pro-context.md
```

The bundle includes the file tree, git status, current diff, recent commits, selected important config files, changed files, and existing `.ai-bridge` handoff context. `--copy` also copies the bundle to the macOS clipboard when `pbcopy` is available.

For an exact selected-file bundle, disable the automatic config/docs and changed-file inclusions:

```bash
codexpro pro-bundle \
  --root /absolute/path/to/your/repo \
  --path README.md \
  --path package.json \
  --no-important-files \
  --no-changed-files \
  --no-diff \
  --no-ai-bridge \
  --copy
```

Useful options:

```bash
codexpro pro-bundle \
  --root /absolute/path/to/your/repo \
  --path src/App.tsx \
  --glob "src/**/*.ts" \
  --max-files 32 \
  --max-total-bytes 300000 \
  --copy
```

Paste the bundle into any model that cannot call MCP tools directly and ask it to produce a narrow implementation plan. Save the returned plan to a file, then apply it:

```bash
codexpro pro-apply --root /absolute/path/to/your/repo --file plan.md
```

Or pipe from stdin:

```bash
cat plan.md | codexpro pro-apply --root /absolute/path/to/your/repo --stdin
```

That writes:

```text
.ai-bridge/current-plan.md
```

Then run Codex, OpenCode, Pi, or another local implementation agent against `.ai-bridge/current-plan.md`.

## Cloudflare options

The launcher uses Cloudflare quick tunnels only when you pass:

```bash
--tunnel cloudflare
```

Quick tunnels are good for demos, but the `trycloudflare.com` URL changes whenever the tunnel restarts. Do not use quick tunnels if you want a URL users can keep in ChatGPT.

CodexPro needs `cloudflared` for public HTTPS tunnels. The launcher first uses `cloudflared` from PATH, then `~/.codexpro/bin`. Downloading the official Cloudflare release is explicit through `codexpro install-cloudflared` or `--install-cloudflared`.

```bash
codexpro start --tunnel cloudflare
```

To perform a fresh local install:

```bash
codexpro install-cloudflared
```

You can also force a refresh during normal startup with `codexpro start --install-cloudflared`.

To manage Cloudflare Tunnel yourself, pass a path:

```bash
codexpro start --tunnel cloudflare --cloudflared /path/to/cloudflared
```

Explicit local install currently supports:

```text
macOS:   arm64, x64
Windows: x64, 32-bit
Linux:   x64, 32-bit, arm64, arm
```

Other platforms can still work by installing `cloudflared` manually and passing `--cloudflared <path>`.

### Stable URL mode

For daily use, use ngrok's free dev domain, a Cloudflare named tunnel, or a Cloudflare dashboard-managed tunnel token. This gives you one stable ChatGPT connector URL, for example:

```text
https://codexpro.example.com/mcp
```

There is one unavoidable boundary: a permanent public URL needs a tunnel provider such as Cloudflare or ngrok and a hostname reserved with that provider. CodexPro can run the tunnel after that setup, but a quick tunnel cannot be made permanent.

If you use quick mode, you will need to edit the ChatGPT app every restart because the copied Server URL changes.

One-time Cloudflare CLI setup with your own domain:

```bash
cloudflared tunnel login
cloudflared tunnel create codexpro
cloudflared tunnel route dns codexpro codexpro.example.com
```

Then daily startup is one command:

```bash
codexpro stable \
  --root /absolute/path/to/your/repo \
  --hostname codexpro.example.com \
  --tunnel-name codexpro \
  --token keep-this-codexpro-token-stable \
  --mode handoff \
  --write handoff \
  --bash off
```

Put this stable Server URL into ChatGPT Developer Mode once:

```text
https://codexpro.example.com/mcp
```

Configure `Authorization: Bearer keep-this-codexpro-token-stable` when your MCP client supports headers. Use `--allow-query-token` only for backward-compatible clients that cannot send headers.

After that, users only restart the local command. They do not need to edit the ChatGPT connector unless they change the hostname or token.

If you create a remotely managed tunnel in the Cloudflare dashboard instead, save its tunnel token to a local file and run:

```bash
codexpro start \
  --root /absolute/path/to/your/repo \
  --tunnel cloudflare-named \
  --hostname codexpro.example.com \
  --cloudflare-token-file ~/.codexpro/cloudflare-tunnel-token \
  --token keep-this-codexpro-token-stable \
  --mode handoff \
  --write handoff \
  --bash off
```

Token naming matters:

```text
--cloudflare-token-file  Cloudflare's tunnel connector token.
--token                  CodexPro's MCP bearer auth token.
```

### Stable URL with ngrok

If you already installed ngrok and authenticated it:

```bash
ngrok config add-authtoken <your-ngrok-token>
```

Create a free ngrok account, find your assigned dev domain in the ngrok dashboard under Universal Gateway -> Domains, then start CodexPro with:

```bash
codexpro ngrok \
  --root /absolute/path/to/your/repo \
  --hostname your-domain.ngrok-free.dev \
  --token keep-this-codexpro-token-stable
```

Equivalent explicit form:

```bash
codexpro start \
  --root /absolute/path/to/your/repo \
  --tunnel ngrok \
  --hostname your-domain.ngrok-free.dev \
  --token keep-this-codexpro-token-stable
```

CodexPro runs ngrok in the background with:

```bash
ngrok http http://127.0.0.1:8787 --url https://your-domain.ngrok-free.dev
```

Put this Server URL into ChatGPT Developer Mode once:

```text
https://your-domain.ngrok-free.dev/mcp
```

Configure `Authorization: Bearer keep-this-codexpro-token-stable` when your MCP client supports headers.

After that, keep using the same hostname and token. You do not need to recreate the ChatGPT app unless you change either one.

After saving this in `codexpro setup --save-config`, daily startup from that repo is:

```bash
codexpro start --profile
```

CodexPro will reuse the saved ngrok hostname and saved CodexPro token.

### Running two repositories at the same time

You can run CodexPro for multiple repositories at once, but each running workspace needs its own local port:

```bash
# repo A
codexpro setup  # choose port 8787

# repo B
codexpro setup  # choose port 8788
```

If both repositories use quick tunnels, different local ports are enough because each run gets a different temporary public URL.

If both repositories use stable ngrok or Cloudflare URLs, each repository also needs its own public hostname:

```text
repo A  port 8787  codexpro-a.ngrok-free.dev
repo B  port 8788  codexpro-b.ngrok-free.dev
```

Do not point two running repositories at the same local port or the same ngrok/Cloudflare hostname. The second process will fail because the port or public hostname is already owned by the first process.

For Namecheap and custom-domain setup, read [DOMAIN_SETUP.md](DOMAIN_SETUP.md). The key point is that a stable domain can solve your own repeated ChatGPT connector setup now, but a single shared URL for every future user needs a hosted relay or per-user tunnel routing.

If ChatGPT does not let you edit an existing app's Server URL, do not use quick tunnels for daily work. Use `codexpro stable` with a Cloudflare named tunnel and put the stable URL into ChatGPT once:

```bash
codexpro stable-help
```

For a less manual daily workflow, create a shell alias:

```bash
alias codexpro-local='codexpro start --root /path/to/your/repo --bash safe'
```

Then run:

```bash
codexpro-local
```

## Manual HTTP MCP mode

```bash
CODEXPRO_ROOT=/absolute/path/to/your/repo \
CODEXPRO_ALLOWED_ROOTS=/absolute/path/to/your \
CODEXPRO_BASH_MODE=off \
CODEXPRO_WRITE_MODE=handoff \
CODEXPRO_HTTP_TOKEN='replace-with-long-random-token' \
npm run start:http
```

Health check:

```bash
curl -H 'Authorization: Bearer replace-with-long-random-token' 'http://127.0.0.1:8787/healthz'
```

MCP endpoint:

```text
http://127.0.0.1:8787/mcp
```

## Stdio MCP mode

For clients that launch local MCP commands:

```bash
node /absolute/path/to/codexpro/dist/stdio.js \
  --root /absolute/path/to/your/repo \
  --allow-root /absolute/path/to/your \
  --bash off \
  --write handoff
```

Example MCP config:

```json
{
  "mcpServers": {
    "CodexPro": {
      "command": "node",
      "args": [
        "/absolute/path/to/codexpro/dist/stdio.js",
        "--root",
        "/absolute/path/to/your/repo",
        "--allow-root",
        "/absolute/path/to/your",
        "--bash",
        "off",
        "--write",
        "handoff"
      ]
    }
  }
}
```

## Write modes

`CODEXPRO_WRITE_MODE=handoff` is the default. Use `workspace` only when you want ChatGPT to edit source files directly in a trusted repo.

```text
off        write/edit tools are disabled; handoff_to_agent and handoff_to_codex still write .ai-bridge/current-plan.md
handoff    write/edit can only write inside .ai-bridge/
workspace  write/edit can write workspace files, except blocked paths
```

The launcher uses `handoff` unless you explicitly pass `--write workspace`.

`save_prompt_file` is the narrow exception for prompt handoff and coordination. In standard and full tool modes it can save generated prompts only as `.md` or `.txt` files under fixed prompt-only targets: `.ai-bridge/prompts/`, `docs/chatgpt/generated-prompts/`, or `docs/loop/inbox/`. It returns safe metadata and diff stats, not the prompt body or full diff. It does not accept arbitrary directories, does not execute commands, and does not allow generic source editing. Use `workspace` write mode only for trusted direct source edits.

## Tool modes

`CODEXPRO_TOOL_MODE=standard` is the default. It exposes the normal coding loop plus bounded source inspection, `show_changes`, Pro context export, prompt saves, and generic agent handoff. Generic read/write/edit/bash are mode-dependent or full-mode compatibility tools.

```text
minimal   smallest surface for demos and simple coding: open/source_outline/read_source_lines/show_changes
standard  default surface for local coding plus bounded source inspection and handoff/export
full      all tools, including inventory, workspace snapshots, raw git tools, codex_context, and compatibility wrappers
```

Launcher examples:

```bash
codexpro start --tool-mode minimal
codexpro start --tool-mode full
```

## Bash modes

`CODEXPRO_BASH_MODE=off` is the default. `CODEXPRO_BASH_MODE=safe` allows common inspection and test commands, including:

```text
pwd, ls, find
git status, git diff, git log, git show, git branch, git rev-parse, git ls-files
npm/pnpm/yarn/bun test/build/lint/typecheck/check, including suffix scripts such as npm run build:clients
pytest, go test, cargo test, cargo check, cargo clippy, tsc, eslint, biome check
```

Use the MCP `read` and `search` tools for file contents. The safe shell blocks obvious destructive commands, redirects, pipes, `curl`, `wget`, `ssh`, `docker`, `git push/reset/clean/checkout/switch/restore`, `find -exec`, `find -delete`, and file-content shell readers such as `cat`, `grep`, `rg`, `head`, and `tail`. Package-manager scripts can execute repository code; safe mode is an allowlist, not a sandbox.

`CODEXPRO_BASH_MODE=off` disables bash completely.

`CODEXPRO_BASH_MODE=full` allows arbitrary shell commands. Use this only for trusted local repos; MCP itself is not an OS sandbox.

By default the bash environment is sanitized. To inherit your full local environment:

```bash
CODEXPRO_INHERIT_ENV=1 CODEXPRO_BASH_MODE=full npm run start:http
```

## Safety boundaries

Blocked by default:

```text
.env, .env.*
.git internals
node_modules
private key patterns such as *.pem, *.key, id_rsa, id_ed25519
credential files such as .npmrc, .pypirc, .netrc, .aws, .azure, .kube, Docker config, and service-account JSON
local state/database files such as *.sqlite, *.sqlite3, and *.db
build/cache outputs such as dist, build, .next, coverage, .cache
paths outside the opened workspace root
workspace roots outside CODEXPRO_ALLOWED_ROOTS
symlink/junction traversal by default, even when it resolves inside the workspace
```

Extra blocked globs can be added with a comma-separated env var:

```bash
CODEXPRO_BLOCKED_GLOBS='**/secrets/**,**/*.sqlite,**/*.db' codexpro start --root /repo
```

## First ChatGPT prompt

```text
Use CodexPro.

Call server_config first, then codexpro_self_test.
If self-test fails, stop and report the failed checks.
Then call open_current_workspace with include_tree=false.

Act as a coding agent. Inspect the relevant files, make the requested source edits with write/edit, then verify with search/read/bash and show_changes when useful. Use bash only for focused verification commands such as build, test, lint, or typecheck.

Keep changes scoped to the request. Do not use handoff_to_agent unless I explicitly ask for planning-only handoff.
```

After upgrading CodexPro or changing the Server URL, refresh the app actions in ChatGPT before judging the card UI. Existing cards do not retroactively re-render after a widget URI change.

## Prompt for a local agent

```text
Read .ai-bridge/current-plan.md and execute it in small, reviewable steps.

After each meaningful change, update .ai-bridge/agent-status.md with:

- what changed
- files touched
- tests, lint, or typecheck commands run
- results
- blockers or questions
- what ChatGPT or another reviewer should review next

Keep .ai-bridge/decisions.md aligned with implementation choices. Save the final review diff to .ai-bridge/implementation-diff.patch when practical. Do not overwrite .ai-bridge/current-plan.md unless asked.
```

## Demo prompt matching the screenshots

For this demo prompt, start explicitly with `codexpro start --mode agent --write workspace --bash safe`.

```text
Use CodexPro.

Open ~/tmp/codexpro-example as the active workspace. Demonstrate each tool call while you work:

1. server_config
2. open_workspace
3. tree
4. read the relevant HTML/table file
5. write README.md explaining the demo
6. edit the repeated table row so each tool appears once
7. run one final targeted search and show_changes to verify

Narrate which CodexPro tool you are using before each call.
```

## Recommended workflow

1. Start CodexPro MCP against your repo with `codexpro start --root /repo`.
2. Connect the printed endpoint in ChatGPT Developer Mode.
3. Ask ChatGPT to inspect the repo and write a handoff plan, or explicitly start with `--mode agent --write workspace --bash safe` when direct source edits are intended.
4. If your chosen ChatGPT model cannot call tools, run `codexpro pro-bundle --root /repo --copy`, paste the bundle into that model, then apply its plan with `codexpro pro-apply --root /repo --file plan.md`.
5. Use `codexpro start --profile` only after reviewing saved settings for the current workspace.

## Development

```bash
npm ci --ignore-scripts
npm run build
npm run smoke
npm run doctor -- --tunnel none
```

Before publishing or opening a pull request, check:

```bash
npm pack --dry-run
```

The package should not include local runtime reports, `.ai-bridge`, `.env` files, tunnel tokens, or generated tarballs.

For public release gates, see [PUBLIC_LAUNCH_CHECKLIST.md](PUBLIC_LAUNCH_CHECKLIST.md). For contribution and security boundaries, see [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

MIT
