// Run with: npm test (compiles first). When the extension tells the user that a plan parked a step.
const test = require("node:test");
const assert = require("node:assert/strict");
const { parkedNotice } = require("../out/planNotices.js");

const plan = (stepsParked, extra = {}) => ({ title: "Two features", stepsDone: 1, stepsTotal: 4, stepsParked, ...extra });

test("a plan with nothing parked says nothing", () => {
  assert.deepEqual(parkedNotice(0, plan(0)), { announced: 0 });
  assert.deepEqual(parkedNotice(0, plan(undefined)), { announced: 0 });
});

test("the first parked step is told once and never again for the same count", () => {
  const first = parkedNotice(0, plan(1));
  assert.equal(first.announced, 1);
  assert.match(first.text, /parked a step of "Two features"/);
  assert.match(first.text, /1\/4 steps done/);
  assert.match(first.text, /retry or skip/);

  // The next polls of the same plan: nothing, however often they come.
  for (let poll = 0; poll < 5; poll++) {
    assert.deepEqual(parkedNotice(first.announced, plan(1)), { announced: 1 });
  }
});

test("a second parked step is news, in the plural", () => {
  const second = parkedNotice(1, plan(2));
  assert.equal(second.announced, 2);
  assert.match(second.text, /parked 2 steps of "Two features"/);
  assert.match(second.text, /depend on them/);
});

test("a step the user retried is forgotten, so parking again is told again", () => {
  assert.deepEqual(parkedNotice(2, plan(1)), { announced: 1 });
  assert.deepEqual(parkedNotice(1, plan(0)), { announced: 0 });
  const again = parkedNotice(0, plan(1));
  assert.equal(again.announced, 1);
  assert.ok(again.text);
});

test("a count that is not a number or is negative counts as none", () => {
  assert.deepEqual(parkedNotice(0, plan(-3)), { announced: 0 });
});
