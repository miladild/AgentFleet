import assert from "node:assert/strict";
import { test } from "node:test";
import { lastRunLine, measuredLine, nextWindowLine, regressionLine, startHourLabel, windowLengthLabel } from "./weeklyPresentation.ts";

// A Sunday, 18 October 2026, at 10:00 UTC. Dates are read in UTC unless a test names a zone, so the results do not depend
// on the zone of the machine running the tests.
const clock = { now: new Date("2026-10-18T10:00:00Z"), timeZone: "UTC" };

const weekly = (overrides) => ({
  enabled: true,
  dayOfWeek: "sunday",
  startHour: 2,
  hours: 4,
  nextWindowStartUtc: "2026-10-25T02:00:00Z",
  lastRunUtc: null,
  nodes: [],
  ...overrides,
});

test("the next window names its day, its start in local time, and its length", () => {
  assert.equal(nextWindowLine(weekly({}), clock), "Next window: Sunday 25 Oct, 02:00 local time, for 4 hours.");
  assert.equal(nextWindowLine(weekly({ hours: 1 }), clock), "Next window: Sunday 25 Oct, 02:00 local time, for 1 hour.");
});

test("the next window is shown in the zone of the viewer", () => {
  // 02:00 UTC on that Sunday is 11:00 in Tokyo, on the same day.
  assert.equal(
    nextWindowLine(weekly({}), { now: clock.now, timeZone: "Asia/Tokyo" }),
    "Next window: Sunday 25 Oct, 11:00 local time, for 4 hours.",
  );
});

test("a switched-off measurement plans no window, and an enabled one without a time says so", () => {
  assert.equal(
    nextWindowLine(weekly({ enabled: false, nextWindowStartUtc: null }), clock),
    "The weekly measurement is off, so no window is planned.",
  );
  assert.equal(nextWindowLine(weekly({ nextWindowStartUtc: null }), clock), "No window is planned yet.");
});

test("a machine never measured says so", () => {
  assert.equal(measuredLine(null, clock), "Never measured.");
});

test("a recent measurement is relative to the day it is read on, with its date", () => {
  assert.equal(measuredLine("2026-10-18T06:00:00Z", clock), "Last measured today (18 Oct).");
  assert.equal(measuredLine("2026-10-17T23:30:00Z", clock), "Last measured yesterday (17 Oct).");
  assert.equal(measuredLine("2026-10-15T12:00:00Z", clock), "Last measured 3 days ago (15 Oct).");
  assert.equal(measuredLine("2026-10-12T12:00:00Z", clock), "Last measured 6 days ago (12 Oct).");
});

test("a measurement a week or more old shows its date alone, with the year when it is another year", () => {
  assert.equal(measuredLine("2026-10-11T12:00:00Z", clock), "Last measured 11 Oct.");
  assert.equal(measuredLine("2025-12-30T12:00:00Z", clock), "Last measured 30 Dec 2025.");
});

test("days are counted on the calendar of the zone the dates are shown in", () => {
  // 18:30 UTC on the 17th is 03:30 on the 18th in Tokyo.
  const measured = "2026-10-17T18:30:00Z";
  assert.equal(measuredLine(measured, { now: clock.now, timeZone: "Asia/Tokyo" }), "Last measured today (18 Oct).");
  assert.equal(measuredLine(measured, clock), "Last measured yesterday (17 Oct).");
});

test("the last complete run says when it finished, or that none has", () => {
  assert.equal(lastRunLine(null, clock), "No weekly run has finished yet.");
  assert.equal(lastRunLine("2026-10-15T12:00:00Z", clock), "Last complete run: 3 days ago (15 Oct).");
});

test("a date that cannot be read is said to be unavailable rather than guessed", () => {
  assert.equal(measuredLine("not a time", clock), "Last measured: date unavailable.");
  assert.equal(lastRunLine("not a time", clock), "Last complete run: date unavailable.");
});

test("a regression says the day it began and gives the backend's reasons as a sentence", () => {
  assert.equal(
    regressionLine({ since: "2026-10-11T12:00:00Z", summary: "the recommended context went from 16384 to 12288" }, clock),
    "Regression since 11 Oct: the recommended context went from 16384 to 12288.",
  );
  assert.equal(
    regressionLine({ since: "2026-10-11T12:00:00Z", summary: "speed at 16384 went from 15.2 to 9.8 tokens per second (down 36 percent)." }, clock),
    "Regression since 11 Oct: speed at 16384 went from 15.2 to 9.8 tokens per second (down 36 percent).",
  );
});

test("a regression whose start cannot be read still says there is one", () => {
  assert.equal(
    regressionLine({ since: "", summary: "the model no longer fully fits on the graphics card" }, clock),
    "Regression: the model no longer fully fits on the graphics card.",
  );
});

test("the start hour and the window length read as plain labels", () => {
  assert.equal(startHourLabel(2), "02:00");
  assert.equal(startHourLabel(23), "23:00");
  assert.equal(windowLengthLabel(1), "1 hour");
  assert.equal(windowLengthLabel(12), "12 hours");
});
