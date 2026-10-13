import assert from "node:assert/strict";
import { test } from "node:test";
import { fitStaleReason, formatFitLine, formatFitSize, formatSetupFitSummary } from "./fitPresentation.ts";

const measured = {
  node: "worker-a",
  model: "model-x",
  results: [{ context: 16384, status: "ok", gpuShare: 0.76, tokensPerSecond: 15.2, loadSeconds: 4, detail: null, completedAtUtc: "2026-10-05T00:00:00Z" }],
  recommendation: { context: 16384, doesNotFullyFit: false, reason: "Keeps enough speed." },
  measuredAtUtc: "2026-10-05T00:00:00Z",
  stale: false,
  staleReason: null,
};

test("the fit line uses the recommendation, measured share, speed, and date", () => {
  assert.equal(formatFitSize(16384), "16K");
  assert.equal(formatFitLine(measured), "Fit 16K runs partly on the processor (76% card share), about 15 tokens/s (measured 5 Oct).");
  const onCard = { ...measured, results: [{ ...measured.results[0], gpuShare: 0.95 }] };
  assert.equal(formatFitLine(onCard), "Fit 16K stays on the graphics card (95% card share), about 15 tokens/s (measured 5 Oct).");
});

test("the fit line says when there are too few measurements", () => {
  assert.equal(formatFitLine({ ...measured, results: [], recommendation: { context: null, doesNotFullyFit: false, reason: "No size could be measured." } }), "Fit too few measurements to suggest a size.");
});

test("an incomplete fit is described without saying that it fits", () => {
  const line = formatFitLine({ ...measured, recommendation: { context: 16384, doesNotFullyFit: true, reason: "" } });
  assert.match(line, /does not fully fit on the graphics card/);
  assert.doesNotMatch(line, /stays on the graphics card/);
});

test("stale reasons are brief and use plain words", () => {
  assert.equal(fitStaleReason({ ...measured, stale: true, staleReason: "The server version changed." }), "The server was updated since this was measured.");
  assert.equal(fitStaleReason({ ...measured, stale: true, staleReason: "The model data changed." }), "The model changed since this was measured.");
  assert.equal(fitStaleReason(measured), null);
});

test("a matching setup result confirms a fit without changing the static reason", () => {
  const onCard = { ...measured, results: [{ ...measured.results[0], gpuShare: 0.95 }] };
  assert.deepEqual(formatSetupFitSummary("model-x:latest", onCard), {
    line: "Measured on this computer: 16K stays on the graphics card.",
    replacesStaticReason: false,
  });
});

test("a size that spills to the processor does not claim to stay on the card", () => {
  assert.deepEqual(formatSetupFitSummary("model-x:latest", measured), {
    line: "Measured on this computer: 16K runs partly on the processor.",
    replacesStaticReason: true,
  });
});

test("a measured incomplete fit replaces a static claim that the model fits", () => {
  const summary = formatSetupFitSummary("model-x", {
    ...measured,
    recommendation: { context: 16384, doesNotFullyFit: true, reason: "" },
  });
  assert.deepEqual(summary, {
    line: "Measured on this computer: 16K does not fully fit on the graphics card.",
    replacesStaticReason: true,
  });
  assert.doesNotMatch(summary.line, /fits on the graphics card/);
});

test("setup measurements are shown only for their model and stale results replace static reasons", () => {
  assert.equal(formatSetupFitSummary("model-y", measured), null);
  assert.deepEqual(formatSetupFitSummary("model-x", { ...measured, stale: true }), {
    line: "Measured on this computer: the last result is out of date; measure again.",
    replacesStaticReason: true,
  });
});
