import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { setupPathAllowed } from "../app/api/setup/setupPaths.ts";

const id = "0123456789abcdef";

test("the setup proxy passes on the Ollama update paths and the ones it always had", () => {
  for (const path of ["", "hub", "pull", "pulls", `pulls/${id}/cancel`, "start-ollama", "sandbox", "sandbox/key", "sandbox/prepare"]) {
    assert.equal(setupPathAllowed(path), true, path);
  }
  for (const path of ["ollama-versions", "ollama-versions/check", "ollama-update", "ollama-updates"]) {
    assert.equal(setupPathAllowed(path), true, path);
  }
});

test("the setup proxy refuses everything else", () => {
  for (const path of ["ollama-versions/other", "ollama-update/x", "ollama-updates/abc/cancel", "..", "pulls/ABC/cancel", "ollama-versions/check/extra", "sandbox/other"]) {
    assert.equal(setupPathAllowed(path), false, path);
  }
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

test("every /api/setup path the web UI calls is one the proxy allows", () => {
  const source = join(dirname(fileURLToPath(import.meta.url)), "..");
  const called = new Set();
  for (const file of sourceFiles(source)) {
    const text = readFileSync(file, "utf8");
    // A template expression between slashes, such as ${pull.id}, counts as one piece of the path; one at the end of a
    // piece, such as the optional query after /sandbox, adds nothing.
    for (const match of text.matchAll(/\/api\/setup\/((?:[A-Za-z0-9_\-/.]|\$\{[^}]*\})*)/g)) {
      called.add(match[1].replace(/\/\$\{[^}]*\}/g, `/${id}`).replace(/\$\{[^}]*\}/g, "").replace(/\/+$/, ""));
    }
  }

  assert.ok(
    called.has("ollama-versions") && called.has("ollama-versions/check") && called.has("ollama-update") && called.has("ollama-updates"),
    `expected to find the Ollama update calls, found ${[...called].join(", ")}`,
  );
  for (const path of called) {
    assert.ok(setupPathAllowed(path), `the web UI calls /api/setup/${path}, which the proxy would answer with 404`);
  }
});
