import type { CodexProConfig } from "./config.js";

const MINIMAL_TOOL_NAMES = [
  "server_config",
  "codexpro_self_test",
  "open_current_workspace",
  "open_workspace",
  "source_outline",
  "read_source_lines",
  "show_changes"
] as const;

const STANDARD_TOOL_NAMES = [
  ...MINIMAL_TOOL_NAMES,
  "tree",
  "search",
  "load_skill",
  "read_instructions",
  "read_handoff",
  "save_prompt_file",
  "export_pro_context",
  "handoff_to_agent"
] as const;

const FULL_TOOL_NAMES = [
  "server_config",
  "codexpro_self_test",
  "codexpro_inventory",
  "load_skill",
  "read_instructions",
  "list_workspaces",
  "open_current_workspace",
  "open_workspace",
  "workspace_snapshot",
  "tree",
  "search",
  "source_outline",
  "read_source_lines",
  "read",
  "write",
  "edit",
  "bash",
  "git_status",
  "git_diff",
  "show_changes",
  "read_handoff",
  "codex_context",
  "save_prompt_file",
  "export_pro_context",
  "handoff_to_agent",
  "handoff_to_codex"
] as const;

const ADVANCED_STANDARD_TOOL_NAMES = ["read", "write", "edit", "bash"] as const;
const CODEX_DIAGNOSTIC_TOOL_NAMES = ["codex_diagnostic_inventory", "codex_diagnostic_config_summary", "codex_diagnostic_sqlite_metadata"] as const;

interface HiddenTool {
  name: string;
  reason: string;
}

export interface ToolExposure {
  effectiveTools: string[];
  hiddenTools: HiddenTool[];
}

function uniqueToolNames(names: readonly string[]): string[] {
  return [...new Set(names)];
}

export function toolExposureForMode(config: CodexProConfig): ToolExposure {
  if (config.toolMode === "full") {
    const effectiveTools = config.codexDiagnosticReadMode === "read"
      ? uniqueToolNames([...FULL_TOOL_NAMES, ...CODEX_DIAGNOSTIC_TOOL_NAMES])
      : uniqueToolNames(FULL_TOOL_NAMES);
    return { effectiveTools, hiddenTools: [] };
  }

  const base = config.toolMode === "minimal" ? [...MINIMAL_TOOL_NAMES] : [...STANDARD_TOOL_NAMES];
  const effective = new Set<string>(base);
  const hiddenTools: HiddenTool[] = [];

  hiddenTools.push({
    name: "read",
    reason: "hidden in minimal/standard modes because source_outline and read_source_lines are the safer bounded source-inspection path"
  });

  if (config.bashMode === "off") {
    hiddenTools.push({ name: "bash", reason: "hidden because bashMode=off" });
  } else {
    effective.add("bash");
  }

  if (config.writeMode === "workspace") {
    effective.add("write");
    effective.add("edit");
  } else {
    hiddenTools.push({
      name: "write",
      reason: `hidden because writeMode=${config.writeMode}; use save_prompt_file, handoff_to_agent, or export_pro_context`
    });
    hiddenTools.push({
      name: "edit",
      reason: `hidden because writeMode=${config.writeMode}; use save_prompt_file, handoff_to_agent, or export_pro_context`
    });
  }

  for (const name of ADVANCED_STANDARD_TOOL_NAMES) {
    if (!effective.has(name) && !hiddenTools.some((item) => item.name === name)) {
      hiddenTools.push({ name, reason: "hidden by current tool mode" });
    }
  }

  if (config.codexDiagnosticReadMode === "read") {
    for (const name of CODEX_DIAGNOSTIC_TOOL_NAMES) effective.add(name);
  }

  return {
    effectiveTools: uniqueToolNames([...effective]),
    hiddenTools
  };
}

export function shouldRegisterTool(config: CodexProConfig, name: string): boolean {
  if (config.toolMode === "full") return true;
  return toolExposureForMode(config).effectiveTools.includes(name);
}

export const READ_ONLY_ANNOTATIONS = { readOnlyHint: true, openWorldHint: false, destructiveHint: false };
export const SESSION_READ_ANNOTATIONS = { readOnlyHint: true, openWorldHint: false, destructiveHint: false, idempotentHint: false };
export const LOCAL_WRITE_ANNOTATIONS = { readOnlyHint: false, openWorldHint: false, destructiveHint: true, idempotentHint: false };
export const BASH_ANNOTATIONS = { readOnlyHint: false, openWorldHint: true, destructiveHint: true, idempotentHint: false };
export const HANDOFF_WRITE_ANNOTATIONS = { readOnlyHint: false, openWorldHint: false, destructiveHint: false, idempotentHint: false };

function annotationsForTool(name: string): Record<string, boolean> {
  if (name === "bash") return BASH_ANNOTATIONS;
  if (name === "write" || name === "edit") return LOCAL_WRITE_ANNOTATIONS;
  if (name === "save_prompt_file" || name === "export_pro_context" || name === "handoff_to_agent" || name === "handoff_to_codex" || name === "codexpro_self_test") {
    return HANDOFF_WRITE_ANNOTATIONS;
  }
  if (name === "open_current_workspace" || name === "open_workspace") return SESSION_READ_ANNOTATIONS;
  return READ_ONLY_ANNOTATIONS;
}

export function annotationSummary(toolNames: string[]): Record<string, unknown> {
  const counts = { read_only: 0, local_write: 0, handoff_write: 0, open_world: 0, destructive: 0 };
  const byTool: Record<string, Record<string, boolean>> = {};
  for (const name of toolNames) {
    const annotations = annotationsForTool(name);
    byTool[name] = annotations;
    if (annotations.readOnlyHint) counts.read_only += 1;
    else if (annotations.destructiveHint) counts.local_write += 1;
    else counts.handoff_write += 1;
    if (annotations.openWorldHint) counts.open_world += 1;
    if (annotations.destructiveHint) counts.destructive += 1;
  }
  return { counts, by_tool: byTool };
}
