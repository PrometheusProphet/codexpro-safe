# AGENTS.md example

This repo is connected through CodexPro.

Rules for any connected coding agent:

- Follow the current user request and the closest tracked owner.
- Inspect current source and Git state before changing files.
- Do not edit source unless implementation is authorized and the connector exposes write tools.
- Use `.ai-bridge` only for a genuine durable handoff, not routine same-task work.
- Report the checks actually run and their results.
