import { createHash } from "node:crypto";
import fsp from "node:fs/promises";
import type { Workspace } from "./guard.js";
import { CodexProError, PathGuard } from "./guard.js";

export const PROMPT_SAVE_POLICY_PATH = ".codexpro/prompt-save-policy.json";

const MAX_POLICY_BYTES = 16_384;
const CONTRACT_TRIGGERS = new Set([
  "legacy-replacement",
  "migration",
  "parent-slicing",
  "parity",
  "promised-capability-completion",
  "recovery",
  "restoration",
  "supplied-reference-conflict",
  "visible-destination-command"
]);
const DISPOSITIONS = new Set([
  "blocked",
  "explicitly_changed_by_user",
  "outside_local_edit_scope_but_open",
  "preserved",
  "repaired"
]);
const RESULT_KINDS = new Set([
  "callback",
  "destination",
  "disabled",
  "export",
  "mutation",
  "other",
  "selection",
  "status",
  "workflow"
]);
const PARENT_STATUSES = new Set(["blocked", "complete", "open"]);
const LOCAL_SCOPES = new Set(["in_scope", "outside_local_edit_scope"]);
const PROFILES = new Set(["green", "yellow", "red", "complex"]);

interface PromptSavePolicy {
  schemaVersion: 1;
  validator: "product-contract-v1";
  requireManifest: true;
}

export interface PromptValidationResult {
  policy: "product-contract-v1";
  promptId: string;
  promptHash: string;
  verdict: "passed";
}

export async function validatePromptBeforeSave(
  guard: PathGuard,
  workspace: Workspace,
  prompt: string,
  manifest: unknown
): Promise<PromptValidationResult | undefined> {
  const policy = await loadPromptSavePolicy(guard, workspace);
  if (!policy && manifest === undefined) return undefined;
  if (policy?.requireManifest && manifest === undefined) {
    throw new CodexProError(
      `save_prompt_file requires contract_manifest because ${PROMPT_SAVE_POLICY_PATH} enables product-contract-v1.`
    );
  }

  const failures = validatePromptContractManifest(manifest);
  if (failures.length > 0) {
    throw new CodexProError(
      ["Prompt contract validation failed before save:", ...failures.map((failure) => `- ${failure}`)].join("\n")
    );
  }

  const record = manifest as Record<string, unknown>;
  const promptHash = `sha256:${createHash("sha256").update(`${prompt.trimEnd()}\n`).digest("hex")}`;
  if (record.promptHash !== undefined && record.promptHash !== promptHash) {
    throw new CodexProError("Prompt contract validation failed before save:\n- promptHash does not match the prompt being saved.");
  }
  return {
    policy: "product-contract-v1",
    promptId: String(record.promptId),
    promptHash,
    verdict: "passed"
  };
}

async function loadPromptSavePolicy(
  guard: PathGuard,
  workspace: Workspace
): Promise<PromptSavePolicy | undefined> {
  const resolved = guard.resolve(workspace, PROMPT_SAVE_POLICY_PATH);
  let content: string;
  try {
    content = await fsp.readFile(resolved.absPath, "utf8");
  } catch (error) {
    if (isNodeError(error) && error.code === "ENOENT") return undefined;
    throw error;
  }
  if (Buffer.byteLength(content, "utf8") > MAX_POLICY_BYTES) {
    throw new CodexProError(`${PROMPT_SAVE_POLICY_PATH} exceeds ${MAX_POLICY_BYTES} bytes.`);
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(content);
  } catch {
    throw new CodexProError(`${PROMPT_SAVE_POLICY_PATH} must contain valid JSON.`);
  }
  if (!isRecord(parsed)
    || parsed.schemaVersion !== 1
    || parsed.validator !== "product-contract-v1"
    || parsed.requireManifest !== true) {
    throw new CodexProError(
      `${PROMPT_SAVE_POLICY_PATH} must declare schemaVersion 1, validator product-contract-v1, and requireManifest true.`
    );
  }
  return parsed as unknown as PromptSavePolicy;
}

export function validatePromptContractManifest(manifest: unknown): string[] {
  const failures: string[] = [];
  if (!isRecord(manifest) || manifest.schemaVersion !== 1) {
    return ["Prompt contract manifest has an unsupported shape."];
  }
  if (!isNonEmptyString(manifest.promptId)) failures.push("promptId is required.");
  if (!PROFILES.has(String(manifest.profile))) {
    failures.push("profile must be green, yellow, red, or complex.");
  }
  if (manifest.promptHash !== undefined
    && !/^sha256:[a-f0-9]{64}$/.test(String(manifest.promptHash))) {
    failures.push("promptHash must use sha256:<lowercase digest> when supplied.");
  }

  const arrayFields = [
    "contractTriggers",
    "productAuthorityReferences",
    "parentRequirementIds",
    "requirements",
    "omittedParentRows"
  ] as const;
  for (const field of arrayFields) {
    if (!Array.isArray(manifest[field])) failures.push(`${field} must be an array.`);
  }
  if (failures.length > 0) return failures;

  const contractTriggers = manifest.contractTriggers as unknown[];
  const authorityReferences = manifest.productAuthorityReferences as unknown[];
  const parentRequirementIds = manifest.parentRequirementIds as unknown[];
  const requirements = manifest.requirements as unknown[];
  const omittedParentRows = manifest.omittedParentRows as unknown[];
  const triggers = new Set(contractTriggers);
  for (const trigger of triggers) {
    if (!CONTRACT_TRIGGERS.has(String(trigger))) failures.push(`Unknown contract trigger: ${String(trigger)}.`);
  }
  const gateApplies = triggers.size > 0
    || authorityReferences.length > 0
    || parentRequirementIds.length > 0
    || requirements.length > 0
    || omittedParentRows.length > 0;
  if (!gateApplies) return failures;

  if (authorityReferences.length === 0
    || authorityReferences.some((value) => !isNonEmptyString(value))) {
    failures.push("Triggered manifests require productAuthorityReferences.");
  }
  if (parentRequirementIds.length === 0) {
    failures.push("Triggered manifests require parentRequirementIds.");
  }

  const parentIds = uniqueStrings(parentRequirementIds, "parentRequirementIds", failures);
  const coveredIds = new Set<string>();
  for (const row of requirements) validateRequirement(row, parentIds, coveredIds, failures);
  for (const row of omittedParentRows) validateOmittedRow(row, parentIds, coveredIds, failures);
  for (const id of parentIds) {
    if (!coveredIds.has(id)) {
      failures.push(`${id}: parent requirement is missing from requirements and omittedParentRows.`);
    }
  }
  return failures;
}

function validateRequirement(
  value: unknown,
  parentIds: Set<string>,
  coveredIds: Set<string>,
  failures: string[]
): void {
  if (!isRecord(value) || !isNonEmptyString(value.id)) {
    failures.push("Each requirement must have a stable id.");
    return;
  }
  const id = value.id;
  if (coveredIds.has(id)) failures.push(`${id}: requirement is duplicated.`);
  coveredIds.add(id);
  if (!parentIds.has(id)) failures.push(`${id}: requirement is not declared in parentRequirementIds.`);
  if (!isNonEmptyString(value.label)) failures.push(`${id}: label is required.`);
  if (!DISPOSITIONS.has(String(value.disposition))) failures.push(`${id}: disposition is invalid.`);
  if (!LOCAL_SCOPES.has(String(value.localScope))) failures.push(`${id}: localScope is invalid.`);
  if (!PARENT_STATUSES.has(String(value.parentStatus))) failures.push(`${id}: parentStatus is invalid.`);
  if (!Array.isArray(value.proof)
    || value.proof.length === 0
    || value.proof.some((proof) => !isNonEmptyString(proof))) {
    failures.push(`${id}: proof is required.`);
  }

  const requiredValid = validateResult(value.requiredResult, id, "requiredResult", failures);
  const currentValid = validateResult(value.currentResult, id, "currentResult", failures);
  const proposedValid = validateResult(value.proposedResult, id, "proposedResult", failures);
  if (!requiredValid || !currentValid || !proposedValid || !DISPOSITIONS.has(String(value.disposition))) return;

  const requiredResult = value.requiredResult as Record<string, unknown>;
  const currentResult = value.currentResult as Record<string, unknown>;
  const proposedResult = value.proposedResult as Record<string, unknown>;
  const currentMatches = sameResult(currentResult, requiredResult);
  const proposedMatches = sameResult(proposedResult, requiredResult);
  const authorizedChange = value.disposition === "explicitly_changed_by_user"
    && isNonEmptyString(value.explicitUserChangeAuthorization);
  const retainedOpen = value.disposition === "outside_local_edit_scope_but_open"
    && value.localScope === "outside_local_edit_scope"
    && new Set(["open", "blocked"]).has(String(value.parentStatus));
  const blocked = value.disposition === "blocked" && value.parentStatus === "blocked";

  if (value.knownDefect === true && value.disposition === "preserved") {
    failures.push(`${id}: a known defect cannot be preserved.`);
  }
  if (value.disposition === "preserved" && (!currentMatches || !proposedMatches)) {
    failures.push(`${id}: preserved requires current and proposed results to match the product requirement.`);
  }
  if (value.disposition === "repaired" && (currentMatches || !proposedMatches)) {
    failures.push(`${id}: repaired requires a defective current result and a contract-matching proposed result.`);
  }
  if (value.disposition === "explicitly_changed_by_user" && !authorizedChange) {
    failures.push(`${id}: explicit user change authorization is required.`);
  }
  if (value.disposition === "outside_local_edit_scope_but_open" && !retainedOpen) {
    failures.push(`${id}: outside-scope requirements must remain open or blocked in the parent.`);
  }
  if (value.disposition === "blocked" && !blocked) {
    failures.push(`${id}: blocked requirements must retain blocked parent status.`);
  }
  if (!proposedMatches && !authorizedChange && !retainedOpen && !blocked) {
    failures.push(`${id}: proposed result weakens or changes the product requirement without authorization.`);
  }
  if (requiredResult.kind === "destination"
    && value.commandEnabled === true
    && value.destinationAllowed === false) {
    failures.push(`${id}: an enabled destination command cannot prohibit its required destination.`);
  }
}

function validateOmittedRow(
  value: unknown,
  parentIds: Set<string>,
  coveredIds: Set<string>,
  failures: string[]
): void {
  if (!isRecord(value) || !isNonEmptyString(value.id)) {
    failures.push("Each omitted parent row must have a stable id.");
    return;
  }
  const id = value.id;
  if (coveredIds.has(id)) failures.push(`${id}: requirement is duplicated.`);
  coveredIds.add(id);
  if (!parentIds.has(id)) failures.push(`${id}: omitted row is not declared in parentRequirementIds.`);
  if (!new Set(["open", "blocked"]).has(String(value.retainedStatus))) {
    failures.push(`${id}: omitted parent rows must retain open or blocked status.`);
  }
}

function validateResult(value: unknown, rowId: string, field: string, failures: string[]): boolean {
  if (!isRecord(value) || !RESULT_KINDS.has(String(value.kind))) {
    failures.push(`${rowId}: ${field}.kind is invalid.`);
    return false;
  }
  if (value.kind === "destination" && !isNonEmptyString(value.target)) {
    failures.push(`${rowId}: ${field}.target is required for a destination.`);
    return false;
  }
  return true;
}

function sameResult(left: Record<string, unknown>, right: Record<string, unknown>): boolean {
  return left.kind === right.kind && (left.kind !== "destination" || left.target === right.target);
}

function uniqueStrings(values: unknown[], field: string, failures: string[]): Set<string> {
  const result = new Set<string>();
  for (const value of values) {
    if (!isNonEmptyString(value)) failures.push(`${field} must contain non-empty strings.`);
    else if (result.has(value)) failures.push(`${field} contains a duplicate: ${value}.`);
    else result.add(value);
  }
  return result;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isNodeError(value: unknown): value is NodeJS.ErrnoException {
  return value instanceof Error;
}
