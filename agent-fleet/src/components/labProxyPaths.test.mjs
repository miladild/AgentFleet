import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { labPathAllowed } from "../app/api/lab/labPaths.ts";

const id = "0123456789abcdef0123456789abcdef";

test("the lab proxy passes on the paths the web UI uses, and only with the right method", () => {
  assert.equal(labPathAllowed(["fit"], "GET"), true);
  assert.equal(labPathAllowed(["fit", "latest"], "GET"), true);
  assert.equal(labPathAllowed(["scorecards"], "GET"), true);
  assert.equal(labPathAllowed(["weekly"], "GET"), true);
  assert.equal(labPathAllowed(["models"], "GET"), true);
  assert.equal(labPathAllowed(["fit"], "POST"), true);
  assert.equal(labPathAllowed(["fit", id, "cancel"], "POST"), true);
  assert.equal(labPathAllowed(["results", "import"], "POST"), true);
  assert.equal(labPathAllowed(["results", id], "DELETE"), true);
});

test("the lab proxy refuses everything else", () => {
  assert.equal(labPathAllowed(["scorecards"], "POST"), false);
  assert.equal(labPathAllowed(["weekly"], "DELETE"), false);
  assert.equal(labPathAllowed(["fit"], "DELETE"), false);
  assert.equal(labPathAllowed(["fit", "other"], "GET"), false);
  assert.equal(labPathAllowed(["fit", "ABCDEF0123456789ABCDEF0123456789", "cancel"], "POST"), false);
  assert.equal(labPathAllowed(["results", "not-an-id"], "DELETE"), false);
  assert.equal(labPathAllowed(["results", id, "extra"], "DELETE"), false);
  assert.equal(labPathAllowed(["..", "plans"], "GET"), false);
  assert.equal(labPathAllowed([], "GET"), false);
  assert.equal(labPathAllowed(["fit"], "PUT"), false);
});

function sourceFiles(directory) {
  const found = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) found.push(...sourceFiles(path));
    else if (/\.(ts|tsx)$/.test(entry.name)) found.push(path);
  }
  return found;
}

test("every /api/lab path the web UI calls is one the proxy allows", () => {
  const source = join(dirname(fileURLToPath(import.meta.url)), "..");
  const called = new Set();
  for (const file of sourceFiles(source)) {
    const text = readFileSync(file, "utf8");
    // A template expression such as ${encodeURIComponent(importId)} counts as one piece of the path.
    for (const match of text.matchAll(/\/api\/lab\/((?:[A-Za-z0-9_\-/.]|\$\{[^}]*\})*)/g)) {
      called.add(match[1].replace(/\$\{[^}]*\}/g, id));
    }
  }

  // The scan must really find the calls, or it would pass on an empty set.
  assert.ok(called.size >= 6, `expected to find the lab calls, found ${[...called].join(", ")}`);
  for (const path of called) {
    const segments = path.split("/").filter((part) => part !== "");
    const allowed = ["GET", "POST", "DELETE"].some((method) => labPathAllowed(segments, method));
    assert.ok(allowed, `the web UI calls /api/lab/${path}, which the proxy would answer with 404`);
  }
});
