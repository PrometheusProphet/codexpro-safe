import assert from "node:assert/strict";
import test from "node:test";

import { validatePromptContractManifest } from "../dist/promptContractValidation.js";

const destination = { kind: "destination", target: "Engagement detail" };

test("rejects destination downgrades", () => {
  for (const kind of ["status", "selection"]) {
    const failures = validatePromptContractManifest(triggeredManifest({
      currentResult: { kind },
      proposedResult: { kind },
      disposition: "preserved",
      knownDefect: true
    }));
    assert.ok(failures.some((failure) => failure.includes("known defect cannot be preserved")));
    assert.ok(failures.some((failure) => failure.includes("preserved requires")));
  }
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
    currentResult: { kind: "status" },
    proposedResult: { kind: "status" },
    disposition: "outside_local_edit_scope_but_open",
    localScope: "outside_local_edit_scope",
    parentStatus: "open",
    knownDefect: true
  })), []);
});

test("allows a lightweight Green manifest", () => {
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
    promptId: "parity-engagement-destination",
    profile: "complex",
    contractTriggers: ["parity", "visible-destination-command"],
    productAuthorityReferences: ["accepted parity contract v2"],
    parentRequirementIds: ["leaf-engagement-detail"],
    requirements: [{
      id: "leaf-engagement-detail",
      label: "Explore to Engagement detail",
      requiredResult: destination,
      currentResult: { kind: "status" },
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
