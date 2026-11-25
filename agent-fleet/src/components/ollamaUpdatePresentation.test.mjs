import assert from "node:assert/strict";
import { test } from "node:test";
import {
  addressLabel,
  anyJobActive,
  confirmQuestion,
  jobFor,
  jobLine,
  latestLine,
  updateAllBlockedReason,
  updatableMachines,
  versionLine,
} from "./ollamaUpdatePresentation.ts";

const machine = (overrides) => ({
  url: "http://192.0.2.10:11434/",
  nodes: ["worker"],
  answers: true,
  version: "0.12.0",
  platform: "linux",
  updateMethod: "ssh-linux",
  upToDate: false,
  canUpdate: true,
  blockedReason: null,
  manualSteps: [],
  ...overrides,
});

const latest = { version: "0.13.0", publishedAtUtc: "2026-11-01T10:00:00Z", checkedAtUtc: "2026-11-25T10:00:00Z" };
const report = (overrides) => ({ machines: [machine({})], latest, planRunning: false, updateRunning: false, ...overrides });

test("before a check the panel says nothing was asked, and after it names the release and when it was checked", () => {
  assert.match(latestLine(null, new Date()), /Nothing has been checked yet/);
  const now = new Date("2026-11-25T10:07:00Z");
  assert.equal(latestLine(latest, now), "Newest stable release: 0.13.0, published 1 Nov 2026. Checked 7 minutes ago.");
  assert.match(latestLine(latest, new Date("2026-11-25T10:00:20Z")), /Checked just now\./);
});

test("a machine row says what it runs, or that it does not answer", () => {
  assert.equal(versionLine(machine({}), latest), "Version 0.12.0");
  assert.equal(versionLine(machine({ upToDate: true, version: "0.13.0" }), latest), "Version 0.13.0 (up to date)");
  assert.equal(versionLine(machine({ answers: false, version: null }), latest), "Does not answer");
  assert.equal(versionLine(machine({ version: null }), latest), "Version unknown");
  assert.equal(versionLine(machine({}), null), "Version 0.12.0");
});

test("Update all follows the backend's own judgement of which machines may be updated", () => {
  const mixed = report({
    machines: [machine({}), machine({ url: "http://192.0.2.11:11434/", canUpdate: false, blockedReason: "Already up to date." })],
  });
  assert.equal(updatableMachines(mixed).length, 1);
  assert.equal(updateAllBlockedReason(mixed), null);
});

test("Update all is off before a check, during a plan, during an update, and when nothing can be updated", () => {
  assert.equal(updateAllBlockedReason(report({ latest: null })), "Check for updates first.");
  assert.match(updateAllBlockedReason(report({ planRunning: true })), /plan is running/);
  assert.match(updateAllBlockedReason(report({ updateRunning: true })), /already running/);
  assert.match(updateAllBlockedReason(report({ machines: [machine({ canUpdate: false })] })), /No machine needs an update/);
});

test("the confirmation says it restarts Ollama and interrupts model use, and names each machine", () => {
  const question = confirmQuestion([machine({}), machine({ url: "http://192.0.2.11:11434/", nodes: ["small", "eyes"], version: "0.11.5" })], "0.13.0");
  assert.match(question, /Update Ollama to 0\.13\.0/);
  assert.match(question, /worker \(192\.0\.2\.10:11434, now 0\.12\.0\)/);
  assert.match(question, /small, eyes \(192\.0\.2\.11:11434, now 0\.11\.5\)/);
  assert.match(question, /restarts Ollama on each of these machines, one at a time, and interrupts any model use/);
});

test("a job row shows its progress, the new version, or the reason it failed", () => {
  const job = (overrides) => ({
    id: "a",
    url: "http://192.0.2.10:11434/",
    nodes: ["worker"],
    state: "running",
    status: "downloading file",
    fromVersion: "0.12.0",
    targetVersion: "0.13.0",
    finalVersion: null,
    error: null,
    ...overrides,
  });
  assert.equal(jobLine(job({})), "Downloading file...");
  assert.equal(jobLine(job({ state: "queued" })), "Waiting for its turn.");
  assert.equal(jobLine(job({ state: "done", finalVersion: "0.13.0" })), "Updated: now 0.13.0.");
  assert.equal(jobLine(job({ state: "failed", error: "Failed at stopping Ollama (exit 1)." })), "Failed at stopping Ollama (exit 1).");
  assert.equal(anyJobActive([job({ state: "done" })]), false);
  assert.equal(anyJobActive([job({ state: "done" }), job({ state: "queued" })]), true);
  assert.equal(jobFor([job({ id: "old", state: "failed" }), job({ id: "new" })], "http://192.0.2.10:11434/").id, "new");
  assert.equal(jobFor([], "x"), undefined);
});

test("an address is shown without its scheme", () => {
  assert.equal(addressLabel("http://192.0.2.10:11434/"), "192.0.2.10:11434");
  assert.equal(addressLabel("not a url"), "not a url");
});
