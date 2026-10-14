// Run with: npm test (compiles first). When the extension asks the user to allow a chat tool call the backend is holding.
const test = require("node:test");
const assert = require("node:assert/strict");
const { approvalsFor, approvalText, isHyphenatedGuid, sleepUnlessAborted, watchApprovals } = require("../out/approvalPrompts.js");

const call = (id, extra = {}) => ({
  id,
  contextId: "thread-1",
  tool: "run_command",
  arguments: '{"command":"npm test"}',
  requestedAtUtc: "2026-10-15T08:00:00Z",
  expiresAtUtc: "2026-10-15T08:04:00Z",
  secondsLeft: 240,
  ...extra,
});

// The watch runs one round per turn of the event loop, without waiting for real time.
const yieldSleep = () => new Promise((resolve) => setImmediate(resolve));
const flush = () => new Promise((resolve) => setImmediate(resolve));

test("only the calls of this chat are listed", () => {
  const list = [call("a"), call("b", { contextId: "thread-2" }), call("c")];
  assert.deepEqual(approvalsFor(list, "thread-1").map((item) => item.id), ["a", "c"]);
  assert.deepEqual(approvalsFor(list, "thread-3"), []);
});

test("a list that is not an array lists nothing", () => {
  for (const list of [undefined, null, "text", 7, {}, { items: [call("a")] }]) {
    assert.deepEqual(approvalsFor(list, "thread-1"), []);
  }
});

test("entries that are not complete calls are skipped, and the complete ones are kept", () => {
  const list = [
    null,
    7,
    "text",
    [call("inside-an-array")],
    { contextId: "thread-1", tool: "run_command", secondsLeft: 9 },
    { id: 5, contextId: "thread-1", tool: "run_command", secondsLeft: 9 },
    { id: "no-tool", contextId: "thread-1", secondsLeft: 9 },
    { id: "no-chat", tool: "run_command", secondsLeft: 9 },
    { id: "chat-not-text", contextId: 1, tool: "run_command", secondsLeft: 9 },
    call("complete"),
  ];
  assert.deepEqual(approvalsFor(list, "thread-1").map((item) => item.id), ["complete"]);
});

test("a call whose time is up, or that has no time left, is not shown", () => {
  const list = [
    call("zero", { secondsLeft: 0 }),
    call("negative", { secondsLeft: -4 }),
    call("text", { secondsLeft: "30" }),
    call("missing", { secondsLeft: undefined }),
    call("last-second", { secondsLeft: 1 }),
  ];
  assert.deepEqual(approvalsFor(list, "thread-1").map((item) => item.id), ["last-second"]);
});

test("missing arguments come back as empty text", () => {
  const [item] = approvalsFor([{ id: "a", contextId: "thread-1", tool: "list_files", secondsLeft: 5 }], "thread-1");
  assert.equal(item.arguments, "");
});

test("the prompt names the tool, the arguments and the time left, and nothing else", () => {
  const item = { id: "a", contextId: "thread-1", tool: "run_command", arguments: '{"command":"npm test"}', secondsLeft: 42 };
  assert.deepEqual(approvalText(item), {
    message: 'Agent Fleet wants to run "run_command" with {"command":"npm test"}. Allow it? No answer in 42 seconds means it is refused.',
  });
});

test("one second left is said in the singular, and empty arguments are left out", () => {
  const item = { id: "a", contextId: "thread-1", tool: "run_command", arguments: "", secondsLeft: 1 };
  assert.deepEqual(approvalText(item), {
    message: 'Agent Fleet wants to run "run_command". Allow it? No answer in 1 second means it is refused.',
  });
});

test("the arguments are flattened to one line and cut to 300 characters, the ellipsis included", () => {
  const withArguments = (args) => ({ id: "a", contextId: "thread-1", tool: "write_file", arguments: args, secondsLeft: 9 });
  const timeLeft = ". Allow it? No answer in 9 seconds means it is refused.";

  const short = approvalText(withArguments('{\n  "path": "notes.txt"\n}')).message;
  assert.equal(short, `Agent Fleet wants to run "write_file" with { "path": "notes.txt" }${timeLeft}`);

  const long = approvalText(withArguments(`{\r\n  "text": "${"a".repeat(600)}"\r\n}`)).message;
  const start = long.indexOf(" with ") + " with ".length;
  const end = long.indexOf(". Allow it?");
  const shown = long.slice(start, end);
  assert.equal(shown.length, 300);
  assert.ok(shown.startsWith('{ "text": "aaa'));
  assert.ok(shown.endsWith("..."));
  assert.ok(!shown.includes("\n"));
  assert.equal(long.slice(end), timeLeft);
});

test("a call with no chat id is listed only when asked for, and another chat's call never is", () => {
  const list = [call("unattributed", { contextId: "" }), call("other-chat", { contextId: "thread-2" }), call("mine")];
  assert.deepEqual(approvalsFor(list, "thread-1").map((item) => item.id), ["mine"]);
  assert.deepEqual(approvalsFor(list, "thread-1", true).map((item) => item.id), ["unattributed", "mine"]);
});

test("only a hyphenated GUID counts as a chat with a durable context", () => {
  assert.equal(isHyphenatedGuid("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), true);
  assert.equal(isHyphenatedGuid("3F2504E0-4F89-11D3-9A0C-0305E82C3301"), true);
  assert.equal(isHyphenatedGuid("3f2504e04f8911d39a0c0305e82c3301"), false);
  assert.equal(isHyphenatedGuid("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}"), false);
  assert.equal(isHyphenatedGuid(""), false);
});

test("a call that stays on the list is asked once", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const asked = [];
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 5) controller.abort();
      return [call("a")];
    },
    decide: async () => {},
    ask: async (item) => {
      asked.push(item.id);
      return undefined;
    },
  });
  assert.equal(rounds, 5);
  assert.deepEqual(asked, ["a"]);
});

test("a second call is shown while the first prompt is still open", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const shown = [];
  const answers = {};
  const decided = [];
  let shownWhenListed = null;
  let rounds = 0;
  const watch = watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 1) return [call("a")];
      if (rounds === 2) return [call("a"), call("b")];
      shownWhenListed = [...shown];
      answers.a("approve");
      answers.b("reject");
      await flush();
      controller.abort();
      return [];
    },
    decide: async (id, approve) => {
      decided.push([id, approve]);
    },
    ask: (item) => {
      shown.push(item.id);
      return new Promise((resolve) => {
        answers[item.id] = resolve;
      });
    },
  });
  await watch;
  assert.deepEqual(shownWhenListed, ["a", "b"]);
  assert.deepEqual(decided, [["a", true], ["b", false]]);
});

test("approve and reject reach decide with the call's id and the matching flag", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const decided = [];
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds > 1) controller.abort();
      return rounds === 1 ? [call("a"), call("b")] : [];
    },
    decide: async (id, approve) => {
      decided.push([id, approve]);
    },
    ask: async (item) => (item.id === "a" ? "approve" : "reject"),
  });
  await flush();
  assert.deepEqual(decided, [["a", true], ["b", false]]);
});

test("a dismissed prompt decides nothing, and the call is not asked again", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const asked = [];
  const decided = [];
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 3) controller.abort();
      return [call("a")];
    },
    decide: async (id, approve) => {
      decided.push([id, approve]);
    },
    ask: async (item) => {
      asked.push(item.id);
      return undefined;
    },
  });
  assert.deepEqual(asked, ["a"]);
  assert.deepEqual(decided, []);
});

test("an answer given after the watch is stopped is not sent", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const answers = {};
  const decided = [];
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 1) return [call("a")];
      controller.abort();
      return [];
    },
    decide: async (id, approve) => {
      decided.push([id, approve]);
    },
    ask: (item) =>
      new Promise((resolve) => {
        answers[item.id] = resolve;
      }),
  });
  // The chat was cancelled while its prompt was open: a late click must not approve the call.
  answers.a("approve");
  await flush();
  assert.deepEqual(decided, []);
});

test("errors from the list, a prompt or an answer do not stop the watch", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  const asked = [];
  const decided = [];
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 1) throw new Error("backend unreachable");
      if (rounds === 2) return [call("a"), call("b")];
      if (rounds === 3) return [call("c")];
      controller.abort();
      return [];
    },
    decide: async (id, approve) => {
      decided.push([id, approve]);
      throw new Error("answer failed");
    },
    ask: async (item) => {
      asked.push(item.id);
      if (item.id === "a") throw new Error("prompt failed");
      return "approve";
    },
  });
  await flush();
  assert.deepEqual(asked, ["a", "b", "c"]);
  assert.deepEqual(decided, [["b", true], ["c", true]]);
});

test("no backend list call is made once the watch is stopped", { timeout: 5000 }, async () => {
  let calls = 0;
  const stopped = new AbortController();
  stopped.abort();
  await watchApprovals({
    threadId: "thread-1",
    signal: stopped.signal,
    sleep: yieldSleep,
    list: async () => {
      calls += 1;
      return [];
    },
    decide: async () => {},
    ask: async () => undefined,
  });
  assert.equal(calls, 0);

  const controller = new AbortController();
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    sleep: yieldSleep,
    list: async () => {
      rounds += 1;
      if (rounds === 2) controller.abort();
      return [];
    },
    decide: async () => {},
    ask: async () => undefined,
  });
  await flush();
  await flush();
  assert.equal(rounds, 2);
});

test("the watch shows calls with no chat id only when it is told to", { timeout: 5000 }, async () => {
  for (const includeUnattributed of [false, true]) {
    const controller = new AbortController();
    const asked = [];
    let rounds = 0;
    await watchApprovals({
      threadId: "thread-1",
      includeUnattributed,
      signal: controller.signal,
      sleep: yieldSleep,
      list: async () => {
        rounds += 1;
        if (rounds === 2) controller.abort();
        return [call("unattributed", { contextId: "" })];
      },
      decide: async () => {},
      ask: async (item) => {
        asked.push(item.id);
        return undefined;
      },
    });
    assert.deepEqual(asked, includeUnattributed ? ["unattributed"] : []);
  }
});

test("the default wait between rounds stops the watch", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  let rounds = 0;
  await watchApprovals({
    threadId: "thread-1",
    signal: controller.signal,
    intervalMs: 1,
    list: async () => {
      rounds += 1;
      if (rounds === 3) controller.abort();
      return [];
    },
    decide: async () => {},
    ask: async () => undefined,
  });
  assert.equal(rounds, 3);
});

test("the default wait returns at once when the watch is stopped", { timeout: 5000 }, async () => {
  const controller = new AbortController();
  let resolved = false;
  const waiting = sleepUnlessAborted(20, controller.signal).then(() => {
    resolved = true;
  });
  controller.abort();
  // The abort itself must end the wait: the 20 ms timer has not fired by the next turn of the event loop.
  await flush();
  assert.equal(resolved, true);
  await waiting;

  const stopped = new AbortController();
  stopped.abort();
  await sleepUnlessAborted(20, stopped.signal);
});
