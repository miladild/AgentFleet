import type { PlanStep } from "../PlanCard";
import type { LivePlan } from "./useLiveFeed";

/** Why a step failed, in a few words, and what the user can do about it. */
export type Why = { short: string; hint: string };

/** The line of a check's output that says what went wrong: a failing test or an error, not the exit code. */
export function failureLine(detail: string): string {
  const lines = detail
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line && !/^(Exit code:|---|Lines naming failures|The end of the output|\.\.\.)/.test(line));
  const failing = lines.find((line) => /✖|✗|\bnot ok\b|\bfail(ed|ing|ures?)?\b|\berror\b/i.test(line) && !/^ℹ\s*fail 0/.test(line));
  return (failing ?? lines[0] ?? "").slice(0, 180);
}

/** What the repair ladder did for a step, from its events: rounds whose check ran and failed, and the highest rung. */
export type Ladder = { rounds: number; rung: number };

export function explain(detail: string | null | undefined, step: PlanStep | null, ladder?: Ladder): Why {
  const text = detail ?? "";
  if (/^Environment issue:/im.test(text)) {
    const cause = text.split("\n", 1)[0].replace(/^Environment issue:\s*/i, "").trim();
    return {
      short: `environment issue: ${cause}`,
      hint:
        "Fleet stopped retries because the selected worker cannot run or verify this step. Check that its limited SSH workspace account, pinned host key, platform and project toolchain are configured in Config > Machines, then retry the step.",
    };
  }
  if (/of working time \(\d+ minutes of model calls and checks\)/i.test(text)) {
    return {
      short: "ran out of its working time",
      hint:
        "The step used all of its working time (model calls and checks, not waiting for a machine) without passing, so it was stopped " +
        "with its files at the best state it reached. Retrying gives it a fresh clock and a fresh repair ladder; or skip the step.",
    };
  }
  if (/plan reached its run deadline/i.test(text)) {
    return { short: "run deadline reached", hint: "The plan stopped with its changes preserved. Review the run log, then approve it to continue with a fresh eight-hour window." };
  }

  const missing = text.match(/still do not exist: (.+?)\.?$/m);
  if (missing) {
    return {
      short: `check passed, but ${missing[1]} was never created`,
      hint:
        "The fleet expected this step to create that file. If the step only runs something (the tests, a build), its file list " +
        "is what is wrong: skip it. If the file should exist, retry.",
    };
  }
  if (/does not exit on its own/.test(text)) {
    return {
      short: `its check \`${step?.verify ?? "?"}\` never finishes`,
      hint:
        "A server or a watcher keeps running, so this check could never pass, and no attempt was spent on it. Skip the step " +
        "(start it yourself once the plan is done), or ask for a check that finishes and approve the plan again.",
    };
  }
  const timeout = text.match(/exceeded the (\d+(?:\.\d+)?)s timeout/);
  if (timeout) {
    return {
      short: `its check was still running after ${Math.round(Number(timeout[1]))} s`,
      hint: "A test that never ends, or one waiting on a server, was stopped. Retry, or change the check so that it finishes.",
    };
  }
  if (/is not recognized as|command not found|ENOENT/i.test(text)) {
    return { short: "its check could not start", hint: "A program the check needs is not installed on the hub, or not on its PATH." };
  }
  const line = failureLine(text);
  const tried = ladder && ladder.rounds > 0
    ? `${ladder.rounds} repair round${ladder.rounds === 1 ? "" : "s"}${ladder.rung > 1 ? `, up to rung ${ladder.rung} of the repair ladder,` : ""}`
    : "The attempts";
  return {
    short: line ? `check failed: ${line}` : "its check failed",
    hint:
      `${tried} did not get the check passing. Read the output, choose another ready machine in the step panel if useful, ` +
      "then retry (a fresh repair ladder) or skip the step.",
  };
}

/** What the repair ladder did for a step since its run last started or was approved again. */
export function ladderOf(plan: LivePlan, stepId: number | undefined): Ladder {
  const events = plan.events ?? [];
  const boundary = events.findLastIndex((event) => event.kind === "run-started" || event.kind === "resumed" || event.kind === "retry-approved");
  let rounds = 0;
  let rung = 1;
  for (const event of events.slice(boundary + 1)) {
    if (event.stepId !== stepId) continue;
    if (event.kind === "round-classified" && event.round != null && event.failureClass !== "Passed") rounds++;
    if (event.rung != null) rung = Math.max(rung, event.rung);
  }
  return { rounds, rung };
}

/** Each step's latest failed check, explained: shown on the step's card. */
export function stepFailures(plan: LivePlan): Map<number, Why> {
  const byStep = new Map<number, Why>();
  for (const event of plan.events ?? []) {
    if (event.stepId === null) continue;
    const step = plan.steps.find((candidate) => candidate.id === event.stepId) ?? null;
    if (event.kind === "check-failed" || event.kind === "final-validation-failed" || event.kind === "run-deadline-exceeded" ||
        event.kind === "step-time-limit" || (event.kind === "plan-blocked" && /does not exit/.test(event.detail))) {
      byStep.set(event.stepId, explain(event.detail, step, ladderOf(plan, event.stepId)));
    }
  }
  return byStep;
}

/**
 * Each step's attempts in its latest run, from the run log. The stored count added up across a block and a retry
 * ("4 of 3"); a step waiting for its turn again keeps the stored count, which a resume set back to nought.
 */
export function withRunAttempts(plan: LivePlan): LivePlan {
  const latest = new Map<number, number>();
  const events = plan.events ?? [];
  const boundary = events.findLastIndex((event) => event.kind === "run-started" || event.kind === "resumed" || event.kind === "retry-approved");
  for (const event of events.slice(boundary + 1)) {
    if (event.kind === "attempt-started" && event.stepId !== null && event.attempt) latest.set(event.stepId, event.attempt);
  }
  if (latest.size === 0) return plan;
  return {
    ...plan,
    steps: plan.steps.map((step) => (step.status !== "pending" && latest.has(step.id) ? { ...step, attempts: latest.get(step.id)! } : step)),
  };
}

export type Block = {
  step: PlanStep | null;
  stopped: boolean;
  why: Why;
  /** The machines the step's rounds ran on, since the run last started, in order (a machine once per change). */
  machines: string[];
  /** What the repair ladder did for the step. */
  ladder: Ladder;
};

/** Why a blocked plan stopped: the step it stopped at, and whether the user stopped it or a check did. */
export function blockOf(plan: LivePlan): Block | null {
  if (plan.status !== "blocked") return null;
  const events = plan.events ?? [];
  const last = [...events].reverse().find((event) => event.kind === "plan-blocked" || event.kind === "stopped");
  const stopped = last?.kind === "stopped";
  const step =
    plan.steps.find((candidate) => candidate.status === "failed") ??
    (last?.stepId != null ? plan.steps.find((candidate) => candidate.id === last.stepId) : undefined) ??
    plan.steps.find((candidate) => candidate.status !== "done") ??
    null;
  if (stopped) {
    return {
      step,
      stopped,
      why: { short: "stopped by you", hint: `Finished steps stay done. Resuming carries on${step ? ` with step ${step.id}` : ""}.` },
      machines: [],
      ladder: { rounds: 0, rung: 1 },
    };
  }

  const since = events.findLastIndex((event) => event.kind === "run-started" || event.kind === "resumed");
  const machines: string[] = [];
  for (const event of events.slice(Math.max(0, since))) {
    const machine = event.modelNode ?? event.node;
    if (event.kind === "attempt-started" && event.stepId === step?.id && machine && machines[machines.length - 1] !== machine) machines.push(machine);
  }
  const ladder = ladderOf(plan, step?.id);
  const failure = [...events].reverse().find((event) => event.stepId === step?.id &&
    (event.kind === "check-failed" || event.kind === "final-validation-failed" || event.kind === "run-deadline-exceeded" || event.kind === "step-time-limit"));
  const endless = last && /does not exit on its own/.test(last.detail) ? last.detail : null;
  return { step, stopped, why: explain(endless ?? failure?.detail ?? last?.detail, step, ladder), machines, ladder };
}
