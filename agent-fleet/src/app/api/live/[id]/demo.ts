import type { LiveEvent } from "../record";

// /live/demo: a made-up plan that runs on a loop of two and a half minutes, so the live view can be seen, and its
// cost measured, without a real plan running. Nothing here touches the backend.

const CYCLE = 150;
const STEPS = [
  { id: 1, title: "Add a test runner", tier: "light", group: null, node: "laptop", start: 0, end: 20, files: ["package.json", "test/smoke.test.ts"], verify: "npm test" },
  { id: 2, title: "US market sessions and holidays", tier: "heavy", group: "core", node: "hub", start: 20, end: 72, files: ["src/server/marketHours.ts", "test/marketHours.test.ts"], verify: "node --import tsx --test test/marketHours.test.ts", failAt: 44 },
  { id: 3, title: "Back off when the provider fails", tier: "standard", group: "core", node: "gpu-box", start: 20, end: 52, files: ["src/server/quoteService.ts", "test/quoteService.test.ts"], verify: "node --import tsx --test test/quoteService.test.ts" },
  { id: 4, title: "Validate the watchlist", tier: "light", group: "core", node: "laptop", start: 20, end: 46, files: ["src/server/watchlist.ts", "test/watchlist.test.ts"], verify: "node --import tsx --test test/watchlist.test.ts" },
  { id: 5, title: "Wire it together and add a health endpoint", tier: "standard", group: "surface", node: "gpu-box", start: 72, end: 110, files: ["src/server/app.ts", "test/app.test.ts"], verify: "npm run typecheck && node --import tsx --test test/app.test.ts" },
  { id: 6, title: "Board shows sessions and falls back to REST", tier: "light", group: "surface", node: "laptop", start: 72, end: 98, files: ["src/web/board.js", "src/web/style.css"], verify: "node --check src/web/board.js" },
  { id: 7, title: "Everything together", tier: "standard", group: null, node: "gpu-box", start: 110, end: 140, files: ["ARCHITECTURE.md"], verify: "npm run typecheck && npm test" },
] as const;

const ACTIONS = ["read_file", "edit_file", "run_command", "search_files", "write_file", "read_file", "run_command"];

export function demoPayload(after: number) {
  const nowSec = Math.floor(Date.now() / 1000);
  const cycleStart = nowSec - (nowSec % CYCLE);
  const t = nowSec - cycleStart;
  const at = (second: number) => new Date((cycleStart + second) * 1000).toISOString();

  const events: LiveEvent[] = [];
  const runEvents: { atUtc: string; stepId: number | null; attempt: number | null; kind: string; tier: string | null; node: string | null; detail: string }[] = [
    { atUtc: at(0), stepId: null, attempt: null, kind: "run-started", tier: null, node: null, detail: "7 step(s), a demo of the live view." },
  ];
  for (const step of STEPS) {
    if (t < step.start) continue;
    const failAt = "failAt" in step ? step.failAt : null;
    const until = Math.min(t, step.end);
    if (step.group && step.id === STEPS.find((s) => s.group === step.group)!.id) {
      runEvents.push({ atUtc: at(step.start), stepId: null, attempt: null, kind: "parallel-started", tier: null, node: null, detail: `The '${step.group}' group runs on three machines at once.` });
    }
    runEvents.push({ atUtc: at(step.start), stepId: step.id, attempt: 1, kind: "attempt-started", tier: step.tier, node: null, detail: "" });
    for (let second = step.start; second <= until; second++) {
      const id = (cycleStart + second) * 10 + step.id;
      const node = failAt !== null && second > failAt ? "hub" : step.node;
      if (second === step.start || (failAt !== null && second === failAt + 1)) {
        events.push({ id, at: at(second), step: step.id, kind: "route", node, text: `model call on ${node} (${step.tier})` });
      } else if ((second - step.start) % 4 === 1) {
        const action = ACTIONS[(second + step.id) % ACTIONS.length];
        const target = action === "run_command" ? step.verify : step.files[(second / 4) % step.files.length | 0];
        events.push({ id, at: at(second), step: step.id, kind: "tool-call", node: null, text: `${action} ${target}` });
      } else if ((second - step.start) % 4 === 2) {
        events.push({ id, at: at(second), step: step.id, kind: "tool-result", node: null, text: "exit 0 · ok", ok: true });
      } else if ((second - step.start) % 8 === 3) {
        events.push({ id, at: at(second), step: step.id, kind: "assistant-output", node, text: `Working on ${step.title.toLowerCase()}.` });
      }
    }
    if (failAt !== null && t >= failAt) {
      runEvents.push({ atUtc: at(failAt), stepId: step.id, attempt: 1, kind: "check-failed", tier: step.tier, node: step.node, detail: "✖ Good Friday 2026-04-03 is a holiday\nℹ tests 11\nℹ pass 10\nℹ fail 1" });
      runEvents.push({ atUtc: at(failAt + 1), stepId: step.id, attempt: 2, kind: "attempt-started", tier: "heavy", node: null, detail: "Retrying with the previous failure." });
    }
    if (t >= step.end) {
      runEvents.push({ atUtc: at(step.end), stepId: step.id, attempt: failAt !== null ? 2 : 1, kind: "check-passed", tier: step.tier, node: step.node, detail: `\`${step.verify}\` passed.` });
      runEvents.push({ atUtc: at(step.end), stepId: step.id, attempt: failAt !== null ? 2 : 1, kind: "step-done", tier: step.tier, node: step.node, detail: "" });
    }
  }
  const finished = t >= 140;
  if (finished) runEvents.push({ atUtc: at(140), stepId: null, attempt: null, kind: "plan-done", tier: null, node: null, detail: "Every step passed its check." });

  const plan = {
    id: "demo",
    title: "Demo: a reliable, tested board",
    goal: "A made-up plan on a loop, to show the live view without a real run.",
    status: finished ? "done" : "running",
    workingDirectory: "C:\\src\\board",
    assumptions: [],
    openQuestions: [],
    risks: [],
    diagram: null,
    diagramNote: null,
    createdUtc: at(0),
    approvedUtc: at(0),
    updatedUtc: at(Math.min(t, 140)),
    contextId: null,
    events: runEvents.sort((a, b) => Date.parse(a.atUtc) - Date.parse(b.atUtc)),
    steps: STEPS.map((step) => {
      const failAt = "failAt" in step ? step.failAt : null;
      const status = t >= step.end ? "done" : t >= step.start ? "running" : "pending";
      const attempts = t < step.start ? 0 : failAt !== null && t > failAt ? 2 : 1;
      return {
        id: step.id,
        title: step.title,
        detail: `Demo step. In a real plan this is the step's full instructions.`,
        files: [...step.files],
        verify: step.verify,
        tier: step.tier,
        parallelGroup: step.group,
        status,
        note: null,
        attempts,
      };
    }),
  };

  return {
    plan,
    events: events.filter((event) => event.id > after),
    nodes: [
      { name: "hub", model: "hub/qwen3-coder:30b", ready: true },
      { name: "gpu-box", model: "qwen3.5:9b", ready: true },
      { name: "laptop", model: "qwen3.5:9b", ready: true },
    ],
    serverTime: new Date().toISOString(),
  };
}
