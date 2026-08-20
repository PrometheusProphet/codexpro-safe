# Public Launch Checklist

CodexPro-Safe is a local developer bridge. Treat public launch readiness as two separate gates:

1. The npm package is safe and understandable for local developers.
2. The ChatGPT app surface is stable enough for users to connect through Developer Mode.

Do not present CodexPro as a fully reviewed public ChatGPT app until it has gone through the current app review flow.

Use [README.md](README.md) for onboarding truth, [SECURITY.md](SECURITY.md) for
normative safety gates, and [DOMAIN_SETUP.md](DOMAIN_SETUP.md) for detailed
public-tunnel, stable-domain, and profile procedures. This checklist verifies
those owners; it does not replace them.

## Release Gate

Run these before tagging a release:

```bash
npm install --package-lock-only
npm run build
npm run smoke
npm pack --dry-run
codexpro-safe doctor --tunnel none
```

On Windows, also run the serialized native and Manager boundary gate:

```powershell
npm run test:windows-boundaries
```

The tarball must not include:

```text
.env files
local tunnel URLs
CodexPro tokens
Cloudflare or ngrok tokens
.ai-bridge runtime files
node_modules
local screenshots or reports
```

## ChatGPT App Gate

Before announcing broadly:

- Test in ChatGPT Developer Mode with a fresh app install.
- Test local-only default mode, then explicitly selected quick-tunnel and saved-ngrok-profile paths.
- Refresh actions after widget URI or metadata changes.
- Confirm CSP stays enabled in Developer Mode.
- Capture screenshots for:
  - app connection screen
  - `server_config`
  - `open_current_workspace`
  - one handoff or prompt save
  - one explicitly enabled `write` or `edit`
  - one `search`
  - one failure state
- Run the same golden prompts on each release and compare behavior.

Suggested golden prompts:

```text
Use CodexPro-Safe. Call server_config, then open_current_workspace with include_tree=false. Read README.md and summarize the project without editing files.
```

```text
Use CodexPro-Safe in default handoff mode. Read PRODUCT.md and create a narrow implementation plan without editing source files.
```

```text
Use CodexPro-Safe. Try to read .env. Explain why the request is blocked.
```

```text
Use CodexPro-Safe in default mode. Confirm that bash is unavailable, then explain that `--bash safe` is an explicit trusted-repository option.
```

## Security Gate

- Reverify the release against [SECURITY.md](SECURITY.md).
- Keep auth enabled for public tunnels.
- Keep bash off by default; enable `--bash safe` or `--bash full` only for a trusted local repository.
- Keep generic source writes unavailable by default; use workspace writes only in explicit agent mode.
- Keep blocked path tests for `.env`, `.git`, `node_modules`, private keys, and symlink escapes.
- Do not broaden allowed roots during setup unless the user explicitly asks.
- Do not log query strings, tokens, file contents, prompts, or full command output by default.

## Onboarding Gate

Fresh-user setup should work with:

```bash
npx codexpro-safe@latest start
```

The terminal must clearly show:

- workspace root
- current mode
- local-only default and any explicitly selected public URL strategy
- that the Server URL is copied
- that Enter opens ChatGPT connector settings
- how to stop the process

For stable URLs, document that saving is opt-in through `codexpro-safe setup --save-config` and reuse is explicit:

```bash
codexpro-safe start --profile
```

## Known Non-Goals For The Current Local Package

- CodexPro is not an OS sandbox.
- CodexPro does not guarantee a ChatGPT model can call MCP tools.
- CodexPro does not change ChatGPT, Codex, or OpenAI quota behavior.
- Quick Cloudflare tunnels are not permanent URLs.
- A single shared public URL for every user requires a hosted relay architecture, not only a local npm package.
