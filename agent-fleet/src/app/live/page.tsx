"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";

type PlanSummary = { id: string; title: string; status: string; stepsDone: number; stepsTotal: number; updatedUtc: string };

const STATUS_COLOR: Record<string, string> = {
  running: "var(--cyan)",
  approved: "var(--yellow)",
  "awaiting-approval": "var(--yellow)",
  blocked: "var(--red)",
  done: "var(--green)",
  rejected: "var(--muted)",
};

/** /live: straight to the plan that is running, or a list to pick one from. */
export default function LiveIndexPage() {
  const router = useRouter();
  const [plans, setPlans] = useState<PlanSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const pick = params.get("pick") !== null;
    fetch("/api/plans", { cache: "no-store" })
      .then((res) => (res.ok ? res.json() : Promise.reject(new Error())))
      .then((data: PlanSummary[]) => {
        const running = data.find((plan) => plan.status === "running" || plan.status === "approved");
        if (running && !pick) router.replace(`/live/${running.id}`);
        else setPlans(data);
      })
      .catch(() => setError("Could not reach the backend."));
  }, [router]);

  return (
    <div className="live-root" style={{ gridTemplateRows: "auto minmax(0, 1fr)" }}>
      <div className="live-bar">
        <a className="mod logo" href="/">◢ FLEET</a>
        <span className="mod title"><span>plans</span></span>
      </div>
      <div style={{ padding: 16, overflow: "auto" }}>
        {error && <div className="live-empty"><span style={{ color: "var(--red)" }}>{error}</span></div>}
        {!error && plans === null && <div className="live-empty"><span className="live-glow-text">looking for a running plan…</span></div>}
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
