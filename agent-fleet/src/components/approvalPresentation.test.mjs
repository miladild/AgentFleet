import assert from "node:assert/strict";
import { test } from "node:test";
import { describeApproval, formatCountdown, sortApprovals, DEFAULT_APPROVAL_CONFIG, APPROVAL_LEVEL_TEXT } from "./approvalPresentation.ts";

const call = (tool, args) => ({
  id: "a".repeat(32),
  contextId: "chat-1",
  tool,
  arguments: args,
  requestedAtUtc: "2026-10-14T10:00:00Z",
  expiresAtUtc: "2026-10-14T10:02:00Z",
  secondsLeft: 120,
});

test("a shell command is described by the command itself", () => {
  assert.equal(describeApproval(call("run_command", JSON.stringify({ command: "npm test" }))), "npm test");
});

test("a shell command over several lines reads as one line", () => {
  assert.equal(describeApproval(call("run_command", JSON.stringify({ command: "cd app\n  npm test\n" }))), "cd app npm test");
});

test("a long shell command is shown in full, since the person is deciding on it", () => {
  const command = "x".repeat(300);
  assert.equal(describeApproval(call("run_command", JSON.stringify({ command }))), command);
});

test("a file write names the file and the size of its text", () => {
  assert.equal(describeApproval(call("write_file", JSON.stringify({ path: "src/app.ts", content: "hello" }))), "Write src/app.ts (5 characters)");
  assert.equal(describeApproval(call("write_file", JSON.stringify({ path: "notes.txt", content: "x" }))), "Write notes.txt (1 character)");
  assert.equal(describeApproval(call("write_file", JSON.stringify({ path: "empty.txt", content: "" }))), "Write empty.txt (0 characters)");
});

test("an edit names the file and the size of the new text", () => {
  assert.equal(describeApproval(call("edit_file", JSON.stringify({ path: "src/app.ts", oldText: "a", newText: "bcd" }))), "Edit src/app.ts (new text: 3 characters)");
  assert.equal(describeApproval(call("edit_file", JSON.stringify({ path: "src/app.ts", oldText: "gone", newText: "" }))), "Edit src/app.ts (new text: 0 characters)");
});

test("a write or edit without its text names only the file", () => {
  assert.equal(describeApproval(call("write_file", JSON.stringify({ path: "empty.txt" }))), "Write empty.txt");
  assert.equal(describeApproval(call("edit_file", JSON.stringify({ path: "src/app.ts", oldText: "a" }))), "Edit src/app.ts");
});

test("any other tool is described by its name and its arguments", () => {
  assert.equal(describeApproval(call("web_search", JSON.stringify({ query: "fleet" }))), 'web_search: {"query":"fleet"}');
  assert.equal(describeApproval(call("list_directory", JSON.stringify({ path: "src" }))), 'list_directory: {"path":"src"}');
});

test("arguments longer than 120 characters are cut to their first 120 with an ellipsis", () => {
  const args = JSON.stringify({ text: "x".repeat(300) });
  assert.equal(describeApproval(call("web_search", args)), `web_search: ${args.slice(0, 120)}...`);
});

test("arguments of exactly 120 characters are not cut", () => {
  const args = JSON.stringify({ text: "y".repeat(109) });
  assert.equal(args.length, 120);
  assert.equal(describeApproval(call("web_search", args)), `web_search: ${args}`);
});

test("invalid JSON arguments fall back to the raw text", () => {
  assert.equal(describeApproval(call("run_command", "{not json")), "run_command: {not json");
});

test("arguments that are not a JSON object fall back to the raw text", () => {
  assert.equal(describeApproval(call("run_command", '["npm test"]')), 'run_command: ["npm test"]');
  assert.equal(describeApproval(call("run_command", "null")), "run_command: null");
});

test("missing or wrongly typed fields fall back to the raw arguments", () => {
  assert.equal(describeApproval(call("run_command", "{}")), "run_command: {}");
  assert.equal(describeApproval(call("run_command", JSON.stringify({ command: 5 }))), 'run_command: {"command":5}');
  assert.equal(describeApproval(call("run_command", '{"command":"   "}')), 'run_command: {"command":"   "}');
  assert.equal(describeApproval(call("write_file", '{"content":"x"}')), 'write_file: {"content":"x"}');
});

test("a call with no arguments is described by its name alone", () => {
  assert.equal(describeApproval(call("web_search", "")), "web_search");
  assert.equal(describeApproval({ tool: "web_search" }), "web_search");
  assert.equal(describeApproval({}), "tool call");
});

test("the countdown shows minutes and seconds with the seconds padded", () => {
  assert.equal(formatCountdown(0), "0:00");
  assert.equal(formatCountdown(59), "0:59");
  assert.equal(formatCountdown(60), "1:00");
  assert.equal(formatCountdown(192), "3:12");
});

test("the countdown adds hours from an hour on and shows a time that has run out as zero", () => {
  assert.equal(formatCountdown(3599), "59:59");
  assert.equal(formatCountdown(3661), "1:01:01");
  assert.equal(formatCountdown(-5), "0:00");
  assert.equal(formatCountdown(Number.NaN), "0:00");
});

test("requests are ordered by when they run out, soonest first", () => {
  const later = { id: "b".repeat(32), expiresAtUtc: "2026-10-14T10:05:00Z" };
  const soonest = { id: "c".repeat(32), expiresAtUtc: "2026-10-14T10:01:00Z" };
  const middle = { id: "a".repeat(32), expiresAtUtc: "2026-10-14T10:03:00Z" };
  const waiting = [later, soonest, middle];
  assert.deepEqual(sortApprovals(waiting), [soonest, middle, later]);
  assert.deepEqual(waiting, [later, soonest, middle], "the list the caller holds is not reordered");
});

test("a request whose expiry time cannot be read goes last", () => {
  const unreadable = { id: "d".repeat(32), expiresAtUtc: "not a time" };
  const known = { id: "e".repeat(32), expiresAtUtc: "2026-10-14T10:09:00Z" };
  assert.deepEqual(sortApprovals([unreadable, known]), [known, unreadable]);
});

test("requests that run out at the same time keep a fixed order by id", () => {
  const first = { id: "1".repeat(32), expiresAtUtc: "2026-10-14T10:01:00Z" };
  const second = { id: "2".repeat(32), expiresAtUtc: "2026-10-14T10:01:00Z" };
  assert.deepEqual(sortApprovals([second, first]), [first, second]);
  assert.deepEqual(sortApprovals([]), []);
});

test("missing approval settings show commands and HTTP with the usual wait", () => {
  assert.deepEqual(DEFAULT_APPROVAL_CONFIG, { mode: "risky", enabled: true, timeoutSeconds: 240 });
});

test("each approval level explains the tools that ask", () => {
  assert.equal(APPROVAL_LEVEL_TEXT.off, "Chat tools run without asking.");
  assert.equal(APPROVAL_LEVEL_TEXT.risky, "Ask before shell commands, Git commands and HTTP requests.");
  assert.equal(APPROVAL_LEVEL_TEXT.all, "Ask before every tool that is not read-only or fleet bookkeeping, including connected tools.");
});
