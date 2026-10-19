import assert from "node:assert/strict";
import { test } from "node:test";
import { requirementCoverage, requirementRows } from "./planRequirements.ts";

test("a plan without a requirement list has no rows", () => {
  assert.deepEqual(requirementRows(undefined), []);
  assert.deepEqual(requirementRows(null), []);
  assert.deepEqual(requirementRows([]), []);
});

test("rows keep the plan's order and say what covers each requirement", () => {
  const rows = requirementRows([
    { id: "R1", text: "Rejects zero", coveredBy: ["R1_rejects_zero"] },
    { id: "R2", text: "Rejects negatives", coveredBy: [] },
    { id: "R3", text: "Accepts one", coveredBy: ["R3 R4: accepts one", "R3_extra"] },
  ]);
  assert.deepEqual(rows, [
    { id: "R1", text: "Rejects zero", coverage: "Covered by R1_rejects_zero" },
    { id: "R2", text: "Rejects negatives", coverage: "No test covers it yet" },
    { id: "R3", text: "Accepts one", coverage: "Covered by R3 R4: accepts one, R3_extra" },
  ]);
});

test("coverage of one requirement names its tests", () => {
  assert.equal(requirementCoverage({ id: "R4", text: "x", coveredBy: [] }), "No test covers it yet");
  assert.equal(requirementCoverage({ id: "R4", text: "x", coveredBy: ["R4_x"] }), "Covered by R4_x");
});
