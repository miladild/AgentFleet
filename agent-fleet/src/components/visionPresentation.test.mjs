import assert from "node:assert/strict";
import { test } from "node:test";
import {
  DEFAULT_VISION_MODE,
  TURNED_OFF_REASON,
  VISION_MODES,
  effectiveVisionMode,
  nodesForSave,
  normalizeVisionMode,
  roleOf,
  statusTone,
  withRole,
} from "./visionPresentation.ts";

test("the three modes are offered in order, each with a one-line hint", () => {
  assert.deepEqual(VISION_MODES.map((mode) => mode.value), ["off", "ondemand", "always"]);
  assert.deepEqual(VISION_MODES.map((mode) => mode.label), ["Off", "On demand", "Always ready"]);
  for (const mode of VISION_MODES) assert.ok(mode.hint.length > 0 && !mode.hint.includes("\n"));
  assert.equal(DEFAULT_VISION_MODE, "ondemand");
});

test("the mode accepts the spellings the backend accepts, in any case", () => {
  assert.equal(normalizeVisionMode("on-demand"), "ondemand");
  assert.equal(normalizeVisionMode("on_demand"), "ondemand");
  assert.equal(normalizeVisionMode("on demand"), "ondemand");
  assert.equal(normalizeVisionMode(" Always "), "always");
  assert.equal(normalizeVisionMode("OFF"), "off");
});

test("an unknown or missing mode is not a mode, and the effective one falls back to on demand", () => {
  assert.equal(normalizeVisionMode("sometimes"), null);
  assert.equal(normalizeVisionMode(undefined), null);
  assert.equal(normalizeVisionMode(null), null);
  assert.equal(effectiveVisionMode({ vision: true }), "ondemand");
  assert.equal(effectiveVisionMode({ vision: true, visionMode: "always" }), "always");
});

test("a text machine never sends a vision mode, and a vision machine always does", () => {
  const nodes = [
    { name: "worker-a", vision: false, tier: "standard", visionMode: "always" },
    { name: "eyes", vision: true, tier: null, visionMode: "off" },
    { name: "eyes-2", vision: true, tier: null },
  ];
  const saved = nodesForSave(nodes);
  assert.equal("visionMode" in saved[0], false);
  assert.equal(saved[1].visionMode, "off");
  assert.equal(saved[2].visionMode, "ondemand");
  assert.equal(nodes[0].visionMode, "always", "the draft itself is not changed");
});

test("a Vision-role machine gets on demand unless it already has a mode, and loses its fallback", () => {
  const added = withRole({ name: "eyes", vision: false, tier: "standard", fallback: true }, "vision");
  assert.equal(added.visionMode, "ondemand");
  assert.equal(added.vision, true);
  assert.equal(added.tier, null);
  assert.equal(added.fallback, false);
  const kept = withRole({ name: "eyes", vision: true, tier: null, fallback: false, visionMode: "always" }, "vision");
  assert.equal(kept.visionMode, "always");
});

test("moving a machine to a text role makes it a text machine", () => {
  const moved = withRole({ name: "eyes", vision: true, tier: null, fallback: false, visionMode: "off" }, "light");
  assert.equal(moved.vision, false);
  assert.equal(moved.tier, "light");
  assert.equal(roleOf(moved), "light");
  assert.equal(roleOf({ name: "eyes", vision: true, tier: null }), "vision");
  assert.equal(roleOf({ name: "plain", vision: false }), "standard");
});

test("a machine turned off by its setting is shown as off, never as a failure", () => {
  assert.equal(TURNED_OFF_REASON, "turned_off");
  assert.equal(statusTone({ ready: false, reachable: false, reason: "turned_off" }), "off");
  assert.equal(statusTone({ ready: false, reachable: true, listsModels: true, reason: "turned_off" }), "off");
  assert.equal(statusTone({ ready: true, reachable: true, reason: null }), "ready");
  assert.equal(statusTone({ ready: false, reachable: true, reason: "no answer" }), "warn");
  assert.equal(statusTone({ ready: false, reachable: false, reason: "down" }), "down");
  assert.equal(statusTone(undefined), "unknown");
});
