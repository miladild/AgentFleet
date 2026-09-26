"use client";

import { useEffect, useState } from "react";
import { LivePlanning } from "./LivePlanning";
import { LiveRun, type LiveCurrent } from "./LiveRun";

type PlanSummary = { id: string; title: string; status: string; stepsDone: number; stepsTotal: number; updatedUtc: string };

const STATUS_COLOR: Record<string, string> = {
  running: "var(--cyan)",
  approved: "var(--yellow)",
  "awaiting-approval": "var(--yellow)",
  blocked: "var(--red)",
  done: "var(--green)",
  rejected: "var(--muted)",
};

/**
 * /live: follows the fleet. A plan running, else a plan being worked out (the strongest machine reading the code), else
 * one waiting for approval, else the newest; when that changes, the page changes with it. With ?context= it follows
 * one conversation (the VS Code chat that asked for the plan); with ?pick it lists the plans instead.
 */
export function LiveFollow() {
  const [mode, setMode] = useState<{ context: string | null; pick: boolean } | null>(null);
  const [current, setCurrent] = useState<LiveCurrent | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    setMode({ context: params.get("context"), pick: params.get("pick") !== null });
  }, []);

  useEffect(() => {
    if (!mode || mode.pick) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      if (stopped || document.hidden) return;
      try {
        const res = await fetch(`/api/live/current${mode.context ? `?context=${encodeURIComponent(mode.context)}` : ""}`, { cache: "no-store" });
        const next: LiveCurrent = await res.json();
        if (stopped) return;
        setError(null);
        // Only a different target swaps the view; the same one keeps its own feed running.
        setCurrent((previous) => (previous && sameTarget(previous, next) ? previous : next));
      } catch {
        if (!stopped) setError("Could not reach the web server.");
      } finally {
        clearTimeout(timer);
        if (!stopped && !document.hidden) timer = setTimeout(tick, 4000);
      }
    };
    const onVisibility = () => {
      if (!document.hidden) tick();
    };
    document.addEventListener("visibilitychange", onVisibility);
    tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [mode]);

  if (mode && !mode.pick && current?.kind === "plan") return <LiveRun key={current.planId} planId={current.planId} following />;
  if (mode && !mode.pick && current?.kind === "planning") return <LivePlanning key={current.contextId} contextId={current.contextId} following />;
  if (!mode || (!mode.pick && current === null)) {
    return (
      <div className="live-root" style={{ gridTemplateRows: "auto minmax(0, 1fr)" }}>
        <div className="live-bar">
          <a className="mod logo" href="/">◢ FLEET</a>
        </div>
        <div className="live-empty">
          <span className={error ? undefined : "live-glow-text"} style={error ? { color: "var(--red)" } : undefined}>
            {error ?? "looking for what the fleet is doing…"}
          </span>
        </div>
      </div>
    );
  }
  return <PlanList />;
}

function sameTarget(a: LiveCurrent, b: LiveCurrent) {
  if (a.kind === "plan" && b.kind === "plan") return a.planId === b.planId;
  if (a.kind === "planning" && b.kind === "planning") return a.contextId === b.contextId;
  return a.kind === b.kind;
}

function PlanList() {
  const [plans, setPlans] = useState<PlanSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetch("/api/plans", { cache: "no-store" })
      .then((res) => (res.ok ? res.json() : Promise.reject(new Error())))
      .then((data: PlanSummary[]) => setPlans(data))
      .catch(() => setError("Could not reach the backend."));
  }, []);

  return (
    <div className="live-root" style={{ gridTemplateRows: "auto minmax(0, 1fr)" }}>
      <div className="live-bar">
        <a className="mod logo" href="/">◢ FLEET</a>
        <span className="mod title">
          <span>plans</span>
        </span>
        <a className="mod" href="/live" title="Follow what the fleet is doing now">◎ follow</a>
      </div>
      <div style={{ padding: 16, overflow: "auto" }}>
        {error && (
          <div className="live-empty">
            <span style={{ color: "var(--red)" }}>{error}</span>
          </div>
        )}
        {!error && plans === null && (
          <div className="live-empty">
            <span className="live-glow-text">loading the plans…</span>
          </div>
        )}
        {plans?.length === 0 && <div className="live-empty">no plans yet</div>}
        {plans && plans.length > 0 && (
          <section className="live-tile focus" style={{ maxWidth: 820, margin: "0 auto" }}>
            <header>
              <b>ls</b> ~/plans
            </header>
            <div style={{ padding: 8 }}>
              {plans.map((plan) => (
                <a
                  key={plan.id}
                  href={`/live/${plan.id}`}
                  className="live-machine"
                  style={{ display: "flex", gap: 12, marginBottom: 6, textDecoration: "none", ["--m" as string]: STATUS_COLOR[plan.status] }}
                >
                  <span style={{ color: STATUS_COLOR[plan.status] ?? "var(--fg)", width: 150 }}>{plan.status.replace("-", " ")}</span>
                  <span style={{ flex: 1, color: "var(--fg)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{plan.title}</span>
                  <span style={{ color: "var(--muted)" }}>
                    {plan.stepsDone}/{plan.stepsTotal} · {new Date(plan.updatedUtc).toLocaleString()}
                  </span>
                </a>
              ))}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}
