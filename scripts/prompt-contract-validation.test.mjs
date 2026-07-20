import assert from "node:assert/strict";
import test from "node:test";

import { validatePromptContractManifest } from "../dist/promptContractValidation.js";

const destination = result("destination", "Engagement detail opens", {
  target: "Engagement detail",
  view: "Overview",
  objectType: "engagement",
  objectId: "clicked-engagement"
});

test("rejects destination downgrades", () => {
  for (const kind of ["status", "selection"]) {
    const failures = validatePromptContractManifest(triggeredManifest({
      currentResult: result(kind, `${kind} only`),
      proposedResult: result(kind, `${kind} only`),
      disposition: "preserved",
      knownDefect: true
    }));
    assert.ok(failures.some((failure) => failure.includes("known defect cannot be preserved")));
    assert.ok(failures.some((failure) => failure.includes("preserved requires")));
  }
});

test("accepts a repaired same-kind other result when visible outcome and context prove the current defect", () => {
  const required = result(
    "other",
    "Normalize Structural pressure across runtime files at or above 500 SLOC and show under-500 only as context",
    { context: "LOC Overview structural-pressure distribution" }
  );
  const current = result(
    "other",
    "Normalize Structural pressure across all runtime files so under-500 dominates",
    { context: "LOC Overview structural-pressure distribution" }
  );

  const failures = validatePromptContractManifest(triggeredManifest({
    requiredResult: required,
    currentResult: current,
    proposedResult: required,
    disposition: "repaired",
    knownDefect: true
  }));

  assert.deepEqual(failures, []);
});

test("rejects repaired when a same-kind current result already matches the requirement", () => {
  const required = result("other", "Required visible result", { context: "same context" });
  const failures = validatePromptContractManifest(triggeredManifest({
    requiredResult: required,
    currentResult: required,
    proposedResult: required,
    disposition: "repaired"
  }));

  assert.ok(failures.some((failure) => failure.includes("repaired requires")));
});

test("rejects an omitted parent requirement", () => {
  const manifest = triggeredManifest();
  manifest.parentRequirementIds.push("leaf-export");
  assert.deepEqual(validatePromptContractManifest(manifest), [
    "leaf-export: parent requirement is missing from requirements and omittedParentRows."
  ]);
});

test("allows a dirty-overlap slice that keeps the parent open", () => {
  assert.deepEqual(validatePromptContractManifest(triggeredManifest({
    currentResult: result("status", "Status only"),
    proposedResult: result("status", "Status only"),
    disposition: "outside_local_edit_scope_but_open",
    localScope: "outside_local_edit_scope",
    parentStatus: "open",
    knownDefect: true
  })), []);
});

test("rejects the same destination target with different view or object identity", () => {
  for (const override of [{ view: "Activity" }, { objectId: "different-engagement" }]) {
    const failures = validatePromptContractManifest(triggeredManifest({
      proposedResult: { ...destination, ...override }
    }));
    assert.ok(failures.some((failure) => failure.includes("repaired requires")));
  }
});

test("rejects same-kind mutation, export, and workflow results with different identities", () => {
  const variants = [
    ["mutation", { operation: "archive" }, { operation: "delete" }],
    ["export", { format: "csv" }, { format: "pdf" }],
    ["workflow", { workflow: "renewal" }, { workflow: "onboarding" }]
  ];
  for (const [kind, requiredFields, proposedFields] of variants) {
    const requiredResult = result(kind, `${kind} accepted result`, requiredFields);
    const failures = validatePromptContractManifest(triggeredManifest({
      requiredResult,
      proposedResult: result(kind, `${kind} accepted result`, proposedFields)
    }));
    assert.ok(failures.some((failure) => failure.includes("repaired requires")), kind);
  }
});

test("rejects unknown and kind-incompatible result identity fields", () => {
  const unknown = triggeredManifest({
    proposedResult: { ...destination, inventedSemantic: "not validated" }
  });
  assert.ok(validatePromptContractManifest(unknown).some((failure) => (
    failure.includes("unsupported semantic fields: inventedSemantic")
  )));

  const incompatible = triggeredManifest({
    proposedResult: result("status", "Show status only", { target: "Engagement detail" })
  });
  assert.ok(validatePromptContractManifest(incompatible).some((failure) => (
    failure.includes("status does not allow: target")
  )));
});

test("rejects missing result schemaVersion or visibleOutcome", () => {
  const missingSchemaVersion = triggeredManifest({
    proposedResult: { kind: "status", visibleOutcome: "Show status only" }
  });
  assert.ok(validatePromptContractManifest(missingSchemaVersion).some((failure) => (
    failure.includes("proposedResult.schemaVersion must be 1")
  )));

  const missingVisibleOutcome = triggeredManifest({
    proposedResult: { schemaVersion: 1, kind: "status" }
  });
  assert.ok(validatePromptContractManifest(missingVisibleOutcome).some((failure) => (
    failure.includes("proposedResult.visibleOutcome is required")
  )));
});

test("rejects objectId without objectType", () => {
  const failures = validatePromptContractManifest(triggeredManifest({
    proposedResult: result("selection", "Select the row", { objectId: "row-1" })
  }));
  assert.ok(failures.some((failure) => failure.includes("objectType is required when objectId is supplied")));
});

test("accepts an exact non-destination semantic result", () => {
  const required = result("status", "Show the accepted status", { context: "save completion" });
  assert.deepEqual(validatePromptContractManifest(triggeredManifest({
    requiredResult: required,
    currentResult: required,
    proposedResult: required,
    disposition: "preserved",
    knownDefect: false
  })), []);
});

test("requires top-level resultSchemaVersion for structured manifests", () => {
  const manifest = triggeredManifest();
  delete manifest.resultSchemaVersion;
  assert.ok(validatePromptContractManifest(manifest).some((failure) => (
    failure.includes("resultSchemaVersion must be 1")
  )));
});

test("allows a lightweight Green manifest without structured result schema", () => {
  assert.deepEqual(validatePromptContractManifest(greenManifest()), []);
});

export function greenManifest() {
  return {
    schemaVersion: 1,
    promptId: "routine-green-repair",
    profile: "green",
    contractTriggers: [],
    productAuthorityReferences: [],
    parentRequirementIds: [],
    requirements: [],
    omittedParentRows: []
  };
}

function triggeredManifest(overrides = {}) {
  return {
    schemaVersion: 1,
    resultSchemaVersion: 1,
    promptId: "parity-engagement-destination",
    profile: "complex",
    contractTriggers: ["parity", "visible-destination-command"],
    productAuthorityReferences: ["accepted parity contract v2"],
    parentRequirementIds: ["leaf-engagement-detail"],
    requirements: [{
      id: "leaf-engagement-detail",
      label: "Explore to Engagement detail",
      requiredResult: destination,
      currentResult: result("status", "Status only"),
      proposedResult: destination,
      disposition: "repaired",
      localScope: "in_scope",
      parentStatus: "complete",
      proof: ["named parity contract"],
      commandEnabled: true,
      destinationAllowed: true,
      knownDefect: true,
      ...overrides
    }],
    omittedParentRows: []
  };
}

function result(kind, visibleOutcome, fields = {}) {
  return { schemaVersion: 1, kind, visibleOutcome, ...fields };
}
