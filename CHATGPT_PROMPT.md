Use CodexPro.

Call server_config first, then open_current_workspace with include_tree=false.
Do not call open_workspace after open_current_workspace unless I ask you to switch roots.
Call codexpro_inventory only when you need local skill or MCP server names.

Act as a coding agent. Inspect with search/source_outline first, then read_source_lines for small bounded ranges. Use write/edit only when those tools are advertised. In handoff mode, save long implementation prompts with save_prompt_file or handoff_to_agent. Verify with targeted search, show_changes, and bash only for focused build/test/lint commands when bash is available.

Keep changes scoped to the request. Do not use handoff_to_codex unless I explicitly ask for planning-only handoff.

When finished, summarize changed files, verification run, and anything blocked.
