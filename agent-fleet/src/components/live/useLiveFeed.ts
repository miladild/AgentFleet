"use client";

import { useEffect, useMemo, useState } from "react";
import type { Plan, PlanRunEvent } from "../PlanCard";
import { failureLine } from "./why";

export type LiveEvent = {
  id: number;
  at: string;
  step: number | null;
  kind: string;
  node: string | null;
  text: string;
  ok?: boolean;
  tool?: string;
  target?: string;
};

export type LiveNode = {
  name: string;
  model: string;
  tier?: string | null;
  ready: boolean;
  reachable?: boolean;
  listsModels?: boolean;
  answers?: boolean;
  reason?: string | null;
  vision?: boolean;
  workspace?: boolean;
};

export type LivePlan = Plan & { approvedUtc?: string | null; createdUtc?: string | null };

/** A line of the log: an event of the plan's record or of its run log, in one shape. */
export type LogLine = {
  key: string;
  at: number;
  step: number | null;
  node: string | null;
  kind: string;
  text: string;
  tone: "info" | "good" | "bad" | "warn" | "dim" | "accent";
};

export type StepActivity = {
  node: string | null;
  workspaceNode: string | null;
  lastAction: string | null;
  lastAt: number | null;
  toolCalls: number;
  /** Where the step is on the repair ladder (see the run log's rung-changed lines), when it has been on it. */
  rung: number | null;
  round: number | null;
};

/** The rungs of the repair ladder, in a few words. */
export const RUNG_NAME: Record<number, string> = {
  1: "requested tier",
  2: "hub model, same conversation",
  3: "fresh conversation with a brief",
};

const CLASS_WORDS: Record<string, string> = {
  Passed: "passed",
  CodeProgress: "failed, but the failures changed",
  Stall: "same failures, no file changed",
  NoOp: "nothing changed",
  Infra: "a machine did not answer",
  Environment: "environment problem",
  CheckDefect: "the check itself is broken",
  Unknown: "unknown failure",
};

const LIVE_STATUSES = new Set(["awaiting-approval", "approved", "running", "blocked"]);
const MAX_EVENTS = 800;

const RUN_TONE: Record<string, LogLine["tone"]> = {
  "check-passed": "good",
  "step-done": "good",
  "plan-done": "good",
  "check-failed": "bad",
  "final-validation-failed": "bad",
  "model-failed": "bad",
  "workspace-failed": "bad",
  "attempt-timed-out": "bad",
  "plan-blocked": "bad",
  "parallel-fallback": "warn",
  "work-restored": "warn",
  "step-skipped": "warn",
  waiting: "warn",
  stopped: "warn",
  "parallel-started": "accent",
  "run-started": "accent",
  resumed: "accent",
  "machine-selected": "accent",
  "recovery-routed": "warn",
  "final-validation-started": "accent",
  "final-validation-passed": "good",
  "run-deadline-exceeded": "bad",
  "run-lease-busy": "warn",
  "retry-approved": "accent",
  "rung-changed": "accent",
  "round-rolled-back": "warn",
  "step-time-limit": "bad",
};

// A line of the run log in words: which attempt, on which tier, and the part of the detail that matters.
function runText(event: PlanRunEvent): string {
  const firstLine = (event.detail ?? "").split("\n").find((line) => line.trim())?.trim() ?? "";
  const tail = (text: string) => (text ? ` · ${text.slice(0, 200)}` : "");
  const route = [
    (event.modelNode ?? event.node) ? `model ${(event.modelNode ?? event.node)}` : null,
    event.workspaceNode ? `workspace ${event.workspaceNode}` : null,
  ].filter(Boolean).join(" · ");
  const routeText = route ? ` · ${route}` : "";
  const ladder = event.rung != null ? ` · rung ${event.rung}${event.round != null ? `, round ${event.round}` : ""}` : "";
  switch (event.kind) {
    case "attempt-started":
      return `attempt ${event.attempt ?? "?"}${ladder} on ${event.tier ?? "?"}${routeText}${tail(firstLine)}`;
    case "attempt-ended":
      return `attempt ${event.attempt ?? "?"}${ladder} ended${tail(firstLine)}`;
    case "check-failed":
      return `✖ check failed on attempt ${event.attempt ?? "?"}${ladder}${tail(failureLine(event.detail ?? ""))}`;
    case "round-classified": {
      if (event.round == null && !event.failureClass) return `round classified${tail(firstLine)}`;
      const files = event.changedFiles?.length
        ? ` · changed ${event.changedFiles.slice(0, 3).join(", ")}${event.changedFiles.length > 3 ? ` +${event.changedFiles.length - 3}` : ""}`
        : event.filesChanged === 0 ? " · changed no file" : "";
      return `round ${event.round ?? "?"}${event.rung != null ? ` (rung ${event.rung})` : ""}: ${CLASS_WORDS[event.failureClass ?? ""] ?? event.failureClass ?? "classified"}${files}`;
    }
    case "rung-changed":
      return event.detail || `moved to rung ${event.rung ?? "?"}`;
    case "round-rolled-back":
      return event.detail || `round ${event.round ?? "?"} was undone`;
    case "step-time-limit":
      return event.detail || "the step used its working time";
    case "check-passed":
      return `✔ check passed${tail(firstLine)}`;
    default:
      return `${event.kind.replace(/-/g, " ")}${routeText}${tail(firstLine)}`;
  }
}

function recordTone(event: LiveEvent): LogLine["tone"] {
  if (event.kind === "verification" || event.kind === "tool-result") return event.ok === false ? "bad" : event.kind === "verification" ? "good" : "dim";
  if (event.kind === "assistant-output") return "accent";
  if (event.kind === "route") return "dim";
  return "info";
}

/**
 * Follows one plan for the live view: a single request every two seconds while the plan can still change (fifteen once
 * it cannot), none while the tab is hidden, and only the record's new events each time.
 */
export function useLiveFeed(planId: string) {
  const [plan, setPlan] = useState<LivePlan | null>(null);
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [nodes, setNodes] = useState<LiveNode[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let after = 0;
    let inFlight = false;
    let live = true;
    setPlan(null);
    setEvents([]);

    const schedule = () => {
      clearTimeout(timer);
      if (!stopped && !document.hidden) timer = setTimeout(tick, live ? 2000 : 15000);
    };

    const tick = async () => {
      if (stopped || inFlight || document.hidden) return;
      inFlight = true;
      try {
        const res = await fetch(`/api/live/${planId}?after=${after}`, { cache: "no-store" });
        const data = await res.json();
        if (stopped) return;
        if (!res.ok) {
          setError(data.error ?? "Could not load the plan.");
          setConnected(false);
        } else {
          setError(null);
          setConnected(true);
          setPlan(data.plan);
          setNodes(data.nodes ?? []);
          const fresh: LiveEvent[] = data.events ?? [];
          after = Math.max(after, Number(data.nextAfter) || 0);
          if (fresh.length > 0) {
            after = Math.max(after, ...fresh.map((event) => event.id));
            setEvents((current) => {
              const seen = new Set(current.map((event) => event.id));
              const merged = [...current, ...fresh.filter((event) => !seen.has(event.id))];
              return merged.length > MAX_EVENTS ? merged.slice(merged.length - MAX_EVENTS) : merged;
            });
          }
          live = LIVE_STATUSES.has(data.plan.status);
        }
      } catch {
        if (!stopped) {
          setConnected(false);
          setError("Could not reach the web server.");
        }
      } finally {
        inFlight = false;
        schedule();
      }
    };

    const onVisibility = () => {
      if (document.hidden) clearTimeout(timer);
      else tick();
    };
    document.addEventListener("visibilitychange", onVisibility);
    tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [planId]);

  // What each step's machine is doing: the machine that took its latest model call, and its latest tool call.
  const activity = useMemo(() => {
    const byStep = new Map<number, StepActivity>();
    for (const event of events) {
      if (event.step === null) continue;
      const entry = byStep.get(event.step) ?? { node: null, workspaceNode: null, lastAction: null, lastAt: null, toolCalls: 0, rung: null, round: null };
      if (event.kind === "route" && event.node) entry.node = event.node;
      if (event.kind === "tool-call") {
        entry.toolCalls += 1;
        entry.lastAction = event.text;
      }
      entry.lastAt = Date.parse(event.at);
      byStep.set(event.step, entry);
    }
    for (const event of plan?.events ?? []) {
      if (event.stepId === null) continue;
      const entry = byStep.get(event.stepId) ?? { node: null, workspaceNode: null, lastAction: null, lastAt: null, toolCalls: 0, rung: null, round: null };
      if (event.modelNode ?? event.node) entry.node = event.modelNode ?? event.node;
      if (event.workspaceNode) entry.workspaceNode = event.workspaceNode;
      // The latest rung and round the log names; a climb starts the new rung's rounds again.
      if (event.rung != null) entry.rung = event.rung;
      if (event.kind === "rung-changed") entry.round = null;
      else if (event.round != null) entry.round = event.round;
      byStep.set(event.stepId, entry);
    }
    return byStep;
  }, [events, plan?.events]);

  const log = useMemo(() => {
    // Tool calls and results carry no machine in the record: they belong to the machine that took the step's latest
    // model call before them.
    const current = new Map<number, string>();
    const lines: LogLine[] = events.map((event) => {
      if (event.step !== null && event.node && (event.kind === "route" || event.kind === "assistant-output")) current.set(event.step, event.node);
      return {
        key: `r${event.id}`,
        at: Date.parse(event.at),
        step: event.step,
        node: event.node ?? (event.step === null ? null : current.get(event.step) ?? null),
        kind: event.kind,
        text: event.text,
        tone: recordTone(event),
      };
    });
    (plan?.events ?? []).forEach((event: PlanRunEvent, index) => {
      lines.push({
        key: `p${index}`,
        at: Date.parse(event.atUtc),
        step: event.stepId,
        node: event.modelNode ?? event.node,
        kind: event.kind,
        text: runText(event),
        tone: event.kind === "round-classified"
          ? event.failureClass === "Passed" ? "good" : event.failureClass === "NoOp" || event.failureClass === "Stall" ? "warn" : "info"
          : RUN_TONE[event.kind] ?? "info",
      });
    });
    return lines.sort((a, b) => a.at - b.at || a.key.localeCompare(b.key));
  }, [events, plan?.events]);

  return { plan, nodes, events, activity, log, error, connected };
}
