# CodexPro FAQ

This document answers common questions concisely. Detailed onboarding belongs
to [README.md](README.md), normative safety guidance to
[SECURITY.md](SECURITY.md), and tunnel/domain/profile procedures to
[DOMAIN_SETUP.md](DOMAIN_SETUP.md).

## Which ChatGPT account should I use?

Use ChatGPT Plus or Pro with Apps / Developer Mode access.

Current testing shows free and Go accounts do not expose the app flow needed for CodexPro.

CodexPro does not unlock Developer Mode, unlock models, bypass account limits, or provide account access. It connects to the ChatGPT app surface your account already has.

Account access and model tool support are separate. A Plus or Pro account can have Apps / Developer Mode, while a specific model surface may still be unable to call connectors or MCP tools directly. Use the Pro context fallback for those sessions.

## What is the recommended install path?

Install globally once:

```bash
npm install -g codexpro-safe
```

The package/product is **CodexPro-Safe**. After installing it, use the
canonical `codexpro-safe` command; `codexpro` remains a supported CLI alias and
is not a separate npm package you need to install.

Then run setup from the repo you want ChatGPT to work on:

```bash
codexpro-safe setup --save-config
```

`--save-config` makes saving a workspace profile an explicit choice. To reuse a
saved profile later, opt in again at launch:

```bash
codexpro-safe start --profile
```

`npx codexpro-safe@latest start` is the no-install fallback. Plain `setup` or
`start` does not silently save or load a workspace profile.

## Can Windows start, restart, and stop CodexPro-Safe without terminal windows?

Yes. [CodexPro-Safe Manager](docs/WINDOWS_MANAGER.md) is the optional Windows
notification-area application for the local connector and OpenAI tunnel client.
It provides one-click lifecycle controls, sign-in startup, supervised recovery,
and authenticated tunnel-health checks.

The product and package remain **CodexPro-Safe**. Use **CodexPro-Safe Manager**
for the Windows application and the corresponding ChatGPT plugin.

## What do I enable in ChatGPT?

Open ChatGPT and go to:

```text
Settings
-> Apps
-> Advanced settings
-> Developer mode: on
-> Enforce CSP in developer mode: on
-> Create app
```

In Create App:

```text
Name: CodexPro
Description: Local workspace bridge for ChatGPT coding
Connection: Server URL
Server URL: paste the URL copied by CodexPro
Authentication: Bearer token
Token: paste your long workspace token
```

CodexPro Safe prefers `Authorization: Bearer <token>`. Temporary compatibility only: if your client cannot send a Bearer token, launch once with `--allow-query-token` or `CODEXPRO_ALLOW_QUERY_TOKEN=1` and use a URL containing `?codexpro_token=...`. Do not save or share that URL; query-token URLs are weaker because tokens can appear in URLs, logs, browser history, screenshots, and copied text.

## Should CSP stay enabled?

Yes. Keep Enforce CSP in developer mode enabled.

CodexPro widgets are built for the CSP-enabled path. They do not need unrestricted network access, external fonts, remote scripts, iframes, or third-party images.

## Does CodexPro bypass rate limits?

No.

CodexPro does not bypass, avoid, increase, pool, resell, or modify ChatGPT, Codex, OpenAI, or third-party model limits. Every request still runs through the user's own ChatGPT session and whatever limits that account has.

The useful part is that Codex and ChatGPT are different product surfaces. If one workflow is unavailable and another product surface you already have access to is still available, CodexPro lets you work against the same local repo without changing either product's limits.

## Can CodexPro use GPT-5.5?

Only if your ChatGPT account already exposes that exact model, or a similar stronger model, in the ChatGPT web product surface you are using, and that model surface can call Developer Mode apps.

Some stronger planning-model surfaces may not be able to call the CodexPro connector directly. CodexPro does not provide, proxy, resell, or unlock models. It gives compatible ChatGPT sessions local repo tools.

For models that cannot call tools, generate a repo context bundle instead:

```bash
codexpro pro-bundle --root /path/to/repo --copy
```

## What can ChatGPT see through CodexPro?

ChatGPT can see explicit workspace context exposed by tools:

- `AGENTS.md`
- `.ai-bridge` plans and status files
- git status
- git diff
- selected source files
- file tree and search results
- source outlines and small bounded line ranges

It cannot read hidden Codex runtime memory or anything outside the allowed workspace unless you explicitly allow that root.

## What can ChatGPT edit?

By default, CodexPro runs in handoff mode. ChatGPT can save prompt/handoff files and export context, while generic source `write`/`edit` tools are not advertised in the standard tool surface. If you explicitly enable workspace write mode, ChatGPT can write and exact-edit files inside the configured workspace.

Safety defaults block common sensitive paths:

- `.env`
- private keys
- `.git`
- `node_modules`
- generated build/cache folders
- symlink escapes
- paths outside the workspace

Use handoff mode if you want ChatGPT to write a plan only and let Codex execute locally. For inspection, start with `search` or `source_outline`, then use `read_source_lines` for small bounded ranges.

## Which tunnel should I choose?

Use this rule:

```text
Fast demo:              Cloudflare quick tunnel
Recommended stable URL: ngrok free dev domain
Custom domain:          Cloudflare named tunnel
No public tunnel:       local-only mode, only for clients that can reach localhost
```

Cloudflare quick tunnel URLs change on restart. If you put a quick-mode URL into ChatGPT, you must edit the ChatGPT app Server URL every time you restart the tunnel.

For most users, the better path is a free ngrok dev domain. Create a free ngrok account, find your assigned dev domain under Universal Gateway -> Domains, and use `codexpro-safe setup --save-config` only if you want to save that workspace choice.

If you own a domain, use Cloudflare named tunnels and route DNS to a hostname like `codexpro.example.com`.

Official references:

- ngrok dev domains: https://ngrok.com/docs/universal-gateway/domains
- Cloudflare Tunnel routing: https://developers.cloudflare.com/tunnel/routing/
- Cloudflare Tunnel DNS records: https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/routing-to-tunnel/dns/

## Can I use the same ChatGPT app URL every day?

Yes, if you reserve a stable ngrok or Cloudflare hostname. Saving its workspace
settings remains opt-in with `codexpro-safe setup --save-config`, and reuse is
explicit with `codexpro-safe start --profile`. Follow the complete
[stable-domain and profile procedure](DOMAIN_SETUP.md); this FAQ does not define
a separate setup path.

## What if I run CodexPro in two repos at once?

Use different local ports and different tunnel hostnames.

Example:

```text
repo A: port 8787, hostname A
repo B: port 8788, hostname B
```

Run `codexpro-safe setup --save-config` in each repo only when you want a saved profile per workspace; launch a saved profile with `codexpro-safe start --profile`.

## Why not use codexpro.github.io?

GitHub Pages gives `owner.github.io` only to the GitHub user or organization named `owner`.

The `codexpro` GitHub username already exists, so this repo cannot use `codexpro.github.io` from the `rebel0789` account.

The clean GitHub Pages URL for this project is:

```text
https://rebel0789.github.io/codexpro/
```

## Is CodexPro production safe?

CodexPro is a local developer bridge, not an OS sandbox.

Use it with repos you trust. Keep token auth enabled for public tunnels. Bash is off by default. Enable it with `--bash safe` or `--bash full` only when you understand the risk. Read [SECURITY.md](SECURITY.md) before exposing it through a public tunnel.

## Where are saved settings stored?

Workspace profiles are saved under:

```text
~/.codexpro/profiles/
```

Use:

```bash
codexpro settings
codexpro settings list
codexpro settings delete --yes
```

Saved tokens are redacted when profiles are displayed.

Profiles are opt-in: `--save-config` offers to save setup choices, and
`--profile` explicitly loads them for setup or start.

See [DOMAIN_SETUP.md](DOMAIN_SETUP.md#saved-workspace-profiles) for the detailed
stable-URL profile workflow.
