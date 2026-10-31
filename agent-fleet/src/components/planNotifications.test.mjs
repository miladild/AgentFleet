import assert from "node:assert/strict";
import { test } from "node:test";
import { mergeNotifications, notificationBadge, rememberDismissed } from "./planNotifications.ts";

const notice = (key) => ({ notificationKey: key });

test("new notices are added once and keep their order", () => {
  const merged = mergeNotifications([notice("a"), notice("b")], [notice("b"), notice("c"), notice("c")]);
  assert.deepEqual(merged.map((n) => n.notificationKey), ["a", "b", "c"]);
});

test("merging nothing new returns the same list", () => {
  const current = [notice("a")];
  assert.equal(mergeNotifications(current, [notice("a")]), current);
});

test("clearing all remembers every key and keeps only the newest hundred", () => {
  const seen = Array.from({ length: 98 }, (_, index) => `old${index}`);
  const next = rememberDismissed(seen, ["x", "y", "z"]);
  assert.equal(next.length, 100);
  assert.deepEqual(next.slice(-3), ["x", "y", "z"]);
  assert.equal(next[0], "old1");
});

test("a key that is already remembered is not added twice", () => {
  assert.deepEqual(rememberDismissed(["a", "b"], ["b", "c"]), ["a", "b", "c"]);
});

test("the badge shows the count, caps it at 9+ and hides at zero", () => {
  assert.equal(notificationBadge(0), null);
  assert.equal(notificationBadge(1), "1");
  assert.equal(notificationBadge(9), "9");
  assert.equal(notificationBadge(10), "9+");
});
