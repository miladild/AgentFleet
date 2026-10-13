import assert from "node:assert/strict";
import { test } from "node:test";
import { formatWorkLine } from "./workPresentation.ts";

const empty = { models: [], infrastructureRoundsByNode: {}, skippedButCountedDoneByNode: {}, truncatedPlans: 0 };

test("a machine without a plan result says there is no plan record", () => {
  assert.equal(formatWorkLine(empty, "vision-node", "model-vision", "hub"), "Work last 7 days: no plan record.");
});

test("fewer than five worked steps are explicitly too few to judge", () => {
  assert.match(formatWorkLine({
    ...empty,
    models: [{ node: "worker-a", model: "model-a", stepsWorked: 4, stepsDoneOnOwn: 3 }],
  }, "worker-a", "model-a", "hub"), /3 of 4 steps on its own, 0 parked \(too few to judge\)/);
});

test("the line uses counts for rescue, infrastructure, skips, and cut plans without a percentage", () => {
  const line = formatWorkLine({
    models: [{
      node: "worker-a", model: "model-a", stepsWorked: 6, stepsDoneOnOwn: 4,
      rescuedByNode: { hub: 2 }, parksByFailureSignature: { check: 1, environment: 1 },
    }],
    infrastructureRoundsByNode: { "worker-a": 3 },
    skippedButCountedDoneByNode: { "worker-a": 1 },
    truncatedPlans: 1,
  }, "worker-a", "model-a", "hub");

  assert.equal(line, "Work last 7 days: 4 of 6 steps on its own, 2 parked. Rescued by the hub 2 · infrastructure problems 3 · skipped 1. Some long plans were cut.");
  assert.doesNotMatch(line, /%/);
  assert.doesNotMatch(line, /too few to judge/);
});
