import assert from "node:assert/strict";
import { test } from "node:test";
import {
  MAX_IMPORT_FILE_BYTES,
  clampLabel,
  formatUtcDateTime,
  groupRowText,
  importBody,
  importFileProblem,
  importSummary,
  modelFor,
  nodeNamesFromResults,
  pluralize,
} from "./modelsPresentation.ts";

const group = {
  node: "worker-a",
  model: "model-y",
  runs: 7,
  doneAndCorrect: 5,
  falseAccepts: 1,
  parked: 1,
  timedOut: 0,
  errors: 0,
  tasks: ["early-close", "slugify"],
  tooFew: false,
  lastRunUtc: "2026-10-12T09:30:00Z",
};

test("a group row shows the counts, the tasks by name and the last run in UTC", () => {
  assert.deepEqual(groupRowText(group), {
    machine: "worker-a",
    model: "model-y",
    runs: "7",
    doneAndCorrect: "5",
    falseAccepts: "1",
    parked: "1",
    timedOut: "0",
    errors: "0",
    tasks: "early-close, slugify",
    lastRun: "12 Oct 2026, 09:30 UTC",
    tag: null,
  });
});

test("fewer than five runs is tagged too few to judge in words, and five or more is not", () => {
  assert.equal(groupRowText({ ...group, runs: 4, tooFew: true }).tag, "too few to judge");
  assert.equal(groupRowText({ ...group, runs: 4, tooFew: false }).tag, "too few to judge");
  assert.equal(groupRowText({ ...group, runs: 5, tooFew: false }).tag, null);
});

test("an unreadable last-run time is said plainly", () => {
  assert.equal(formatUtcDateTime("not a time"), "date unavailable");
});

test("the import sentence says what was imported, what was already stored and what was skipped", () => {
  assert.equal(importSummary({ imported: 3, duplicates: 0, skipped: 0, models: [] }), "Imported 3 runs.");
  assert.equal(importSummary({ imported: 1, duplicates: 0, skipped: 0, models: [] }), "Imported 1 run.");
  assert.equal(
    importSummary({ imported: 2, duplicates: 1, skipped: 0, models: [] }),
    "Imported 2 runs. 1 run was already stored and not added again.",
  );
  assert.equal(
    importSummary({ imported: 0, duplicates: 2, skipped: 1, models: [] }),
    "Imported 0 runs. 2 runs were already stored and not added again. 1 line could not be read and was skipped.",
  );
  assert.equal(
    importSummary({ imported: 1, duplicates: 0, skipped: 2, models: [] }),
    "Imported 1 run. 2 lines could not be read and were skipped.",
  );
});

test("a file over 2 MB is refused by name before it is sent, and one at the limit is not", () => {
  assert.equal(importFileProblem({ name: "results.jsonl", size: MAX_IMPORT_FILE_BYTES }), null);
  assert.equal(
    importFileProblem({ name: "results.jsonl", size: MAX_IMPORT_FILE_BYTES + 1 }),
    "results.jsonl is larger than 2 MB, the most one import takes, so it was not sent. Import the results in smaller parts.",
  );
});

test("the machines come from the models column, each once and sorted, and lines that are not runs name none", () => {
  const text = [
    JSON.stringify({ planId: "plan-1", task: "slugify", status: "done", hiddenPass: true, models: "worker-b, hub" }),
    "not json",
    JSON.stringify({ planId: "plan-2", task: "slugify", status: "done", hiddenPass: true, models: "hub" }),
    JSON.stringify({ planId: " ", task: "slugify", status: "done", models: "ghost" }),
    JSON.stringify({ planId: "plan-3", task: "slugify", status: "done", models: "" }),
  ].join("\n");
  assert.deepEqual(nodeNamesFromResults(text), ["hub", "worker-b"]);
});

test("the import sends the run record as text, or the typed models when there is none, and leaves out blanks", () => {
  assert.deepEqual(
    importBody({ resultsJsonl: "{}\n", runJson: '{"nodes":[]}', nodes: ["hub"], models: { hub: "model-x" }, label: "  standard tier  " }),
    { resultsJsonl: "{}\n", run: '{"nodes":[]}', label: "standard tier" },
  );
  assert.deepEqual(
    importBody({ resultsJsonl: "{}\n", runJson: null, nodes: ["hub", "worker-a"], models: { hub: " model-x ", "worker-a": "  " }, label: "   " }),
    { resultsJsonl: "{}\n", models: { hub: "model-x" } },
  );
});

test("a machine named like a property every object has gets no model unless one was typed for it", () => {
  assert.deepEqual(
    importBody({ resultsJsonl: "{}\n", runJson: null, nodes: ["constructor", "toString"], models: {}, label: "" }),
    { resultsJsonl: "{}\n", models: {} },
  );
  assert.equal(modelFor({}, "constructor"), "");
  assert.equal(modelFor({ hub: "model-x" }, "hub"), "model-x");
});

test("a label is cut to 80 characters, and an emoji counts as one character", () => {
  assert.equal(clampLabel("standard tier"), "standard tier");
  assert.equal(Array.from(clampLabel("\u{1F600}".repeat(81))).length, 80);
});

test("pluralize keeps the count and uses the singular only for one", () => {
  assert.equal(pluralize(0, "run"), "0 runs");
  assert.equal(pluralize(1, "run"), "1 run");
  assert.equal(pluralize(2, "run"), "2 runs");
});
