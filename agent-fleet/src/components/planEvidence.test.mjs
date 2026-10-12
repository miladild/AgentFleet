import assert from "node:assert/strict";
import { test } from "node:test";
import { planEvidenceLabel, planEvidenceTone } from "./planEvidence.ts";

test("plan evidence labels cover both checks and no evidence", () => {
  assert.equal(planEvidenceLabel("own-check-only"), "Accepted on its own tests, not independently verified");
  assert.equal(planEvidenceLabel("second-opinion-agreed"), "Checked by a second opinion as well");
  assert.equal(planEvidenceLabel(null), null);
});

test("plan evidence tone distinguishes own checks from second opinion", () => {
  assert.equal(planEvidenceTone("own-check-only"), "amber");
  assert.equal(planEvidenceTone("second-opinion-agreed"), "green");
  assert.equal(planEvidenceTone(null), null);
});
