"use client";

import { useCallback, useEffect, useState } from "react";
import { useAgent } from "@copilotkit/react-core/v2";
import { MermaidDiagram } from "./MermaidDiagram";

export type PlanStep = {
  id: number;
  title: string;
  detail: string;
  files: string[];
  verify: string | null;
  tier: string;
  parallelGroup?: string | null;
  status: string;
  note: string | null;
  attempts: number;
};

export type PlanRunEvent = {
  atUtc: string;
  stepId: number | null;
  attempt: number | null;
  kind: string;
  tier: string | null;
  node: string | null;
  detail: string;
};

export type Plan = {
  id: string;
  title: string;
  goal: string;
  status: string;
  workingDirectory: string | null;
  assumptions: string[];
  openQuestions: string[];
  risks: string[];
  diagram: string | null;
  diagramNote: string | null;
  steps: PlanStep[];
  updatedUtc: string;
  events?: PlanRunEvent[] | null;
  exportedPlanPath?: string | null;
  contextId?: string | null;
};

const STATUS_LABEL: Record<string, { text: string; className: string }> = {
  "awaiting-approval": { text: "Waiting for your approval", className: "bg-amber-500/15 text-amber-300 border-amber-500/40" },
  approved: { text: "Approved", className: "bg-sky-500/15 text-sky-300 border-sky-500/40" },
  running: { text: "Running", className: "bg-sky-500/15 text-sky-300 border-sky-500/40" },
  blocked: { text: "Blocked", className: "bg-red-500/15 text-red-300 border-red-500/40" },
  done: { text: "Done", className: "bg-emerald-500/15 text-emerald-300 border-emerald-500/40" },
  rejected: { text: "Rejected", className: "bg-neutral-700/40 text-neutral-400 border-neutral-600" },
};

const STEP_ICON: Record<string, { glyph: string; className: string }> = {
  pending: { glyph: "○", className: "text-neutral-500" },
  running: { glyph: "◐", className: "text-sky-400" },
  done: { glyph: "✓", className: "text-emerald-400" },
  failed: { glyph: "✗", className: "text-red-400" },
};

const LIVE_STATUSES = new Set(["awaiting-approval", "approved", "running", "blocked"]);

function usePlan(planId: string) {
  const [plan, setPlan] = useState<Plan | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const res = await fetch(`/api/plans/${planId}`, { cache: "no-store" });
      if (!res.ok) {
        setError(res.status === 404 ? "This plan no longer exists." : "Could not load the plan.");
        return null;
      }
      const data: Plan = await res.json();
      setPlan(data);
      setError(null);
      return data;
    } catch {
      setError("Could not reach the backend.");
      return null;
    }
  }, [planId]);

  // Poll while the plan can still change, so step progress appears as the agent works.
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const tick = async () => {
      const data = await refresh();
      if (!stopped && (data === null || LIVE_STATUSES.has(data.status))) timer = setTimeout(tick, 3000);
    };
    tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [refresh]);

  return { plan, error, refresh };
}

function Section({ title, items }: { title: string; items: string[] }) {
  if (items.length === 0) return null;
  return (
    <div>
      <div className="text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">{title}</div>
      <ul className="list-disc pl-4 text-xs text-neutral-300 space-y-0.5">
        {items.map((item, i) => (
          <li key={i}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

const EVENT_STYLE: Record<string, string> = {
  "parallel-started": "text-violet-300",
  "parallel-fallback": "text-amber-400",
  "work-restored": "text-amber-400",
  waiting: "text-amber-400",
  "check-passed": "text-emerald-400",
  "step-done": "text-emerald-400",
  "plan-done": "text-emerald-400",
  "check-failed": "text-red-400",
  "model-failed": "text-red-400",
  "attempt-timed-out": "text-red-400",
  "plan-blocked": "text-red-400",
  stopped: "text-amber-400",
};

/** What the plan runner did and when: the answer to "what happened while I was asleep". */
function RunLog({ planId, events }: { planId: string; events: PlanRunEvent[] }) {
  return (
    <details className="group">
      <summary className="cursor-pointer text-[10px] uppercase tracking-wide text-neutral-500 select-none">
        Run log ({events.length} events)
      </summary>
      <div className="mt-1 max-h-72 overflow-y-auto rounded border border-neutral-800 bg-neutral-900/60 p-2 space-y-1">
        {events.map((e, i) => {
          const where = e.stepId == null ? "" : `step ${e.stepId}${e.attempt == null ? "" : `.${e.attempt}`}`;
          const via = [e.tier, e.node].filter(Boolean).join(" / ");
          return (
            <div key={i} className="text-[11px] leading-snug">
              <span className="text-neutral-600">{new Date(e.atUtc).toLocaleTimeString()}</span>{" "}
              <span className="text-neutral-500">{where}</span>{" "}
              <span className={EVENT_STYLE[e.kind] ?? "text-neutral-300"}>{e.kind.replace(/-/g, " ")}</span>
              {via && <span className="text-neutral-600"> ({via})</span>}
              {e.detail && (
                <div className="font-mono text-[10px] text-neutral-400 whitespace-pre-wrap break-words pl-3 max-h-24 overflow-y-auto">
                  {e.detail}
                </div>
              )}
            </div>
          );
        })}
        <a
          href={`/api/plans/${planId}/report`}
          target="_blank"
          rel="noreferrer"
          className="inline-block pt-1 text-[10px] text-sky-400 hover:underline"
        >
          Open the full report
        </a>
      </div>
    </details>
  );
}

/** A plan as the user sees it: goal, assumptions, diagram, and steps with live status, plus
 * the approve and reject buttons that unlock the agent's write tools. */
export function PlanCard({ planId }: { planId: string }) {
  const { plan, error, refresh } = usePlan(planId);
  const { agent } = useAgent();
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [exportToProject, setExportToProject] = useState(false);

  async function approve() {
    setBusy(true);
    setActionError(null);
    try {
      const query = exportToProject ? "?exportToProject=true" : "";
      const res = await fetch(`/api/plans/${planId}/approve${query}`, { method: "POST" });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setActionError(data.error ?? "Could not approve the plan.");
        return;
      }
      const approved = (await res.json().catch(() => null)) as Plan | null;
      await refresh();
      // Approving starts the plan runner on the backend. It carries the plan out in the background,
      // one verified step at a time, so there is no chat turn to resume: this card shows the progress.
      // The marker keeps a later question in this conversation tied to the plan.
      agent.addMessage({
        id: crypto.randomUUID(),
        role: "assistant",
        content: `Plan approved [plan:${planId}]. It is running in the background: each step is checked before the next one starts, and the plan card above shows how far it has got.${approved?.exportedPlanPath ? ` A Markdown copy is saved at ${approved.exportedPlanPath}.` : ""}`,
      });
    } catch {
      setActionError("Could not reach the backend.");
    } finally {
      setBusy(false);
    }
  }

  async function stop() {
    setBusy(true);
    setActionError(null);
    try {
      const res = await fetch(`/api/plans/${planId}/stop`, { method: "POST" });
      if (!res.ok) setActionError("Could not stop the plan.");
      await refresh();
    } finally {
      setBusy(false);
    }
  }

  async function reject() {
    setBusy(true);
    setActionError(null);
    try {
      const res = await fetch(`/api/plans/${planId}/reject`, { method: "POST" });
      if (!res.ok) setActionError("Could not reject the plan.");
      await refresh();
    } finally {
      setBusy(false);
    }
  }

  if (!plan) {
    return (
      <div className="my-2 rounded-md border border-neutral-800 bg-neutral-950 px-3 py-2 text-xs text-neutral-500">
        {error ?? "Loading plan..."}
      </div>
    );
  }

  const status = STATUS_LABEL[plan.status] ?? { text: plan.status, className: "border-neutral-600 text-neutral-400" };
  const done = plan.steps.filter((s) => s.status === "done").length;
  const canApprove = plan.status === "awaiting-approval" || plan.status === "blocked";
  const canStop = plan.status === "approved" || plan.status === "running";

  return (
    <div className="my-2 rounded-md border border-neutral-700 bg-neutral-950 overflow-hidden text-sm">
      <div className="px-3 py-2 bg-neutral-900 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="font-medium text-neutral-100">{plan.title}</div>
          {plan.workingDirectory && (
            <div className="text-[11px] font-mono text-neutral-500 truncate">{plan.workingDirectory}</div>
          )}
        </div>
        <span className="shrink-0 flex items-center gap-2">
          {plan.status !== "rejected" && (
            <a
              href={`/live/${plan.id}`}
              target="_blank"
              rel="noreferrer"
              title="Watch it live in a page of its own: the plan, then its run"
              className="text-[11px] px-2 py-0.5 rounded-full border border-cyan-400/50 text-cyan-300 hover:bg-cyan-400/10"
            >
              ◉ Live
            </a>
          )}
          <span className={`text-[11px] px-2 py-0.5 rounded-full border ${status.className}`}>{status.text}</span>
        </span>
      </div>

      <div className="px-3 py-2 space-y-3">
        {plan.goal && <p className="text-xs text-neutral-300">{plan.goal}</p>}
        <Section title="Open questions" items={plan.openQuestions} />
        <Section title="Assumptions" items={plan.assumptions} />
        <Section title="Risks" items={plan.risks} />

        {plan.diagram && <MermaidDiagram code={plan.diagram} />}
        {plan.diagramNote && <p className="text-[11px] text-neutral-500">{plan.diagramNote}</p>}

        <div>
          <div className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">
            Steps ({done} of {plan.steps.length} done)
          </div>
          <ol className="space-y-2">
            {plan.steps.map((step) => {
              const icon = STEP_ICON[step.status] ?? STEP_ICON.pending;
              return (
                <li key={step.id} className="flex gap-2">
                  <span className={`w-4 text-center ${icon.className}`} title={step.status}>
                    {icon.glyph}
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="text-xs text-neutral-200">
                      {step.id}. {step.title}{" "}
                      <span className="text-[10px] text-neutral-500">({step.tier})</span>
                      {step.parallelGroup && (
                        <span className="ml-1 text-[10px] text-violet-300">parallel: {step.parallelGroup}</span>
                      )}
                    </div>
                    {step.detail && <div className="text-[11px] text-neutral-400">{step.detail}</div>}
                    {step.files.length > 0 && (
                      <div className="text-[11px] font-mono text-neutral-500 break-all">{step.files.join(", ")}</div>
                    )}
                    {step.verify && (
                      <div className="text-[11px] font-mono text-neutral-500">verify: {step.verify}</div>
                    )}
                    {step.note && (
                      <div className={`text-[11px] ${step.status === "failed" ? "text-red-400" : "text-neutral-400"}`}>
                        {step.note}
                      </div>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
        </div>

        {plan.events && plan.events.length > 0 && <RunLog planId={plan.id} events={plan.events} />}

        {plan.contextId && (
          <button
            type="button"
            onClick={() => window.dispatchEvent(new CustomEvent("fleet:open-context", { detail: plan.contextId }))}
            title="Decisions, handoffs, files and what each step was given"
            className="text-[11px] text-sky-400 hover:underline"
          >
            Open this plan's durable context
          </button>
        )}
      </div>

      {(canApprove || canStop) && (
        <div className="px-3 py-2 border-t border-neutral-800 flex flex-wrap items-center gap-2">
          {canApprove && (
            <button
              type="button"
              onClick={approve}
              disabled={busy}
              className="rounded-full px-3 py-1 text-xs font-medium bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 transition-colors disabled:opacity-50"
            >
              {plan.status === "blocked" ? "Approve and resume" : "Approve and run"}
            </button>
          )}
          {canApprove && plan.workingDirectory && (
            <label className="flex items-center gap-1.5 text-[11px] text-neutral-400">
              <input
                type="checkbox"
                checked={exportToProject}
                onChange={(event) => setExportToProject(event.target.checked)}
                disabled={busy}
                className="accent-sky-500"
              />
              Save a Markdown copy under <code className="text-neutral-300">.agent-fleet/plans</code>
            </label>
          )}
          {canStop && (
            <button
              type="button"
              onClick={stop}
              disabled={busy}
              className="rounded-full px-3 py-1 text-xs bg-red-600/15 text-red-300 border border-red-600/40 hover:bg-red-600/25 transition-colors disabled:opacity-50"
            >
              Stop
            </button>
          )}
          {plan.status === "awaiting-approval" && (
            <button
              type="button"
              onClick={reject}
              disabled={busy}
              className="rounded-full px-3 py-1 text-xs bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors disabled:opacity-50"
            >
              Reject
            </button>
          )}
          {actionError && <span className="text-xs text-red-400">{actionError}</span>}
          <span className="ml-auto text-[10px] text-neutral-600">
            {canStop ? "Checks finish before later steps start." : "Nothing changes until you approve."}
          </span>
        </div>
      )}
    </div>
  );
}
