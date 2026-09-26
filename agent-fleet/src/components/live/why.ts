import type { PlanRunEvent, PlanStep } from "../PlanCard";
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

export function explain(detail: string | null | undefined, step: PlanStep | null): Why {
  const text = detail ?? "";
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
  return {
    short: line ? `check failed: ${line}` : "its check failed",
    hint:
      "Three attempts did not pass the check, the later ones on the strongest machine. Read the output in the step's panel, " +
      "then retry (three fresh attempts) or skip the step.",
  };
}

/** Each step's latest failed check, explained: shown on the step's card. */
export function stepFailures(plan: LivePlan): Map<number, Why> {
  const byStep = new Map<number, Why>();
  for (const event of plan.events ?? []) {
    if (event.stepId === null) continue;
    const step = plan.steps.find((candidate) => candidate.id === event.stepId) ?? null;
    if (event.kind === "check-failed" || (event.kind === "plan-blocked" && /does not exit/.test(event.detail))) {
      byStep.set(event.stepId, explain(event.detail, step));
    }
  }
  return byStep;
}

/** The tier each step's latest attempt ran on: retries go to the heavy tier. */
export function stepTiers(events: PlanRunEvent[] | null | undefined): Map<number, string> {
  const byStep = new Map<number, string>();
  for (const event of events ?? []) {
    if (event.kind === "attempt-started" && event.stepId !== null && event.tier) byStep.set(event.stepId, event.tier);
  }
  return byStep;
}

/**
 * Each step's attempts in its latest run, from the run log. The stored count added up across a block and a retry
 * ("4 of 3"); a step waiting for its turn again keeps the stored count, which a resume set back to nought.
 */
export function withRunAttempts(plan: LivePlan): LivePlan {
  const latest = new Map<number, number>();
  for (const event of plan.events ?? []) {
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
  /** The tiers of the attempts since the run last started, in order. */
  tiers: string[];
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
      tiers: [],
    };
  }

  const since = events.findLastIndex((event) => event.kind === "run-started" || event.kind === "resumed");
  const tiers = events
    .slice(Math.max(0, since))
    .filter((event) => event.kind === "attempt-started" && event.stepId === step?.id && event.tier)
    .map((event) => event.tier as string);
  const failure = [...events].reverse().find((event) => event.stepId === step?.id && event.kind === "check-failed");
  const endless = last && /does not exit on its own/.test(last.detail) ? last.detail : null;
  return { step, stopped, why: explain(endless ?? failure?.detail ?? last?.detail, step), tiers };
}
