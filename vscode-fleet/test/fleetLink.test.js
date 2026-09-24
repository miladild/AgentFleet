// Run with: npm test (compiles first). Covers the parts of joining a fleet conversation that do not need VS Code.
const test = require("node:test");
const assert = require("node:assert/strict");
const { continuedMessages, describeSession, pickerMessageId, routePickerRequest, INSTRUCTIONS_PREFIX } = require("../out/fleetLink.js");

const GUID = "1b10662d-23b2-4733-b2af-206e5f1d1ca1";
const OTHER = "bc57f560-e59a-4d0e-907f-eb3579c80673";
const newId = () => "7321a119-e19e-43f6-990c-f16c1ec9d5e8";

test("with no link and no carrier the model picker is not journaled", () => {
  const routing = routePickerRequest({ carrierEnabled: false, carriedId: null, linkedId: null, newId });
  assert.equal(routing.contextId, null);
  assert.equal(routing.threadId, "7321a119e19e43f6990cf16c1ec9d5e8");
  assert.equal(routing.journalOnly, false);
  assert.equal(routing.carrierId, null);
});

test("a link joins the linked chat, journal only, and never hands out a carrier", () => {
  for (const carrierEnabled of [false, true]) {
    const routing = routePickerRequest({ carrierEnabled, carriedId: OTHER, linkedId: GUID, newId });
    assert.deepEqual(routing, { threadId: GUID, contextId: GUID, journalOnly: true, carrierId: null });
  }
});

test("the carrier keeps a chat's own id, or mints one on its first turn", () => {
  assert.deepEqual(routePickerRequest({ carrierEnabled: true, carriedId: GUID, linkedId: null, newId }), {
    threadId: GUID,
    contextId: GUID,
    journalOnly: false,
    carrierId: GUID,
  });
  const minted = routePickerRequest({ carrierEnabled: true, carriedId: null, linkedId: null, newId });
  assert.equal(minted.contextId, newId());
  assert.equal(minted.carrierId, newId());
});

test("picker message ids follow the content, so a replayed message keeps its id", () => {
  assert.equal(pickerMessageId("user", "add tests"), pickerMessageId("user", "add tests"));
  assert.notEqual(pickerMessageId("user", "add tests"), pickerMessageId("assistant", "add tests"));
  assert.notEqual(pickerMessageId("user", "add tests"), pickerMessageId("user", "add more tests"));
  assert.match(pickerMessageId("user", "x"), /^vp-[0-9a-f]{24}$/);
});

test("a continued chat replays the saved transcript, swaps in fresh instructions, then adds the turn", () => {
  const transcript = [
    { id: "a1", role: "system", content: `${INSTRUCTIONS_PREFIX} (.github/copilot-instructions.md):\n\nold rules` },
    { id: "w1", role: "user", content: "Design a rate limiter" },
    { id: "w2", role: "assistant", content: "Use a token bucket", toolCalls: [] },
    { id: "w3", role: "user", content: [{ type: "text", text: "with an image" }] },
    "not a message",
    null,
  ];
  const instructions = { id: "x-instructions", role: "system", content: `${INSTRUCTIONS_PREFIX}: new rules` };
  const turn = { id: "x-t1", role: "user", content: "Now add tests" };

  const messages = continuedMessages(transcript, instructions, turn);

  assert.deepEqual(
    messages.map((m) => m.id),
    ["x-instructions", "w1", "w2", "w3", "x-t1"],
  );
  assert.deepEqual(messages[2].toolCalls, [], "the web UI's own fields are kept as they are");
  assert.equal(continuedMessages({ messages: [] }, null, turn), null);
  assert.deepEqual(
    continuedMessages([], null, turn).map((m) => m.id),
    ["x-t1"],
  );
});

test("a chat is described by size and age", () => {
  const now = new Date("2026-09-25T12:00:00Z");
  assert.equal(describeSession(1, "2026-09-25T11:59:50Z", now), "1 message, just now");
  assert.equal(describeSession(4, "2026-09-25T11:30:00Z", now), "4 messages, 30 min ago");
  assert.equal(describeSession(9, "2026-09-24T12:00:00Z", now), "9 messages, 24 h ago");
  assert.equal(describeSession(2, "2026-09-15T12:00:00Z", now), "2 messages, 10 days ago");
});
