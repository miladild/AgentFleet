"use client";

import { useEffect, useState } from "react";
import { PlanCard } from "./PlanCard";

type PlanSummary = {
  id: string;
  title: string;
  status: string;
  stepsDone: number;
  stepsTotal: number;
  updatedUtc: string;
};

type PlanNotification = PlanSummary & {
  notificationKey: string;
};

const NOTIFICATION_STORAGE_KEY = "agent-fleet.seen-plan-notifications.v1";
const NOTIFICATION_MAX_AGE_MS = 36 * 60 * 60 * 1000;

function readSeenNotifications(): Set<string> {
  try {
    const stored = JSON.parse(localStorage.getItem(NOTIFICATION_STORAGE_KEY) ?? "[]");
    return new Set(Array.isArray(stored) ? stored.filter((value): value is string => typeof value === "string") : []);
  } catch {
    return new Set();
  }
}

const DOT: Record<string, string> = {
  "awaiting-approval": "bg-amber-400",
  approved: "bg-sky-400",
  running: "bg-sky-400",
  blocked: "bg-red-400",
  done: "bg-emerald-400",
  rejected: "bg-neutral-500",
};

/** Every plan the agent has proposed, newest first, so a run left going overnight can be
 * checked in the morning: what was planned, what got done, and where it stopped. */
export function PlansPanel() {
  const [open, setOpen] = useState(false);
  const [plans, setPlans] = useState<PlanSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [notifications, setNotifications] = useState<PlanNotification[]>([]);

  useEffect(() => {
    let stopped = false;
    const loadRecentCompletions = () =>
      fetch("/api/plans", { cache: "no-store" })
        .then((res) => (res.ok ? res.json() : Promise.reject(new Error("Could not load plans"))))
        .then((data: PlanSummary[]) => {
          if (stopped) return;
          const seen = readSeenNotifications();
          const cutoff = Date.now() - NOTIFICATION_MAX_AGE_MS;
          const recent = data
            .filter((plan) => plan.status === "done" || plan.status === "blocked")
            .filter((plan) => Date.parse(plan.updatedUtc) >= cutoff)
            .map((plan) => ({ ...plan, notificationKey: `${plan.id}:${plan.status}:${plan.updatedUtc}` }))
            .filter((plan) => !seen.has(plan.notificationKey));
          setNotifications((current) => {
            const existing = new Set(current.map((notice) => notice.notificationKey));
            return [...current, ...recent.filter((notice) => !existing.has(notice.notificationKey))];
          });
        })
        .catch(() => {});

    loadRecentCompletions();
    const interval = setInterval(loadRecentCompletions, 15_000);
    return () => {
      stopped = true;
      clearInterval(interval);
    };
  }, []);

  useEffect(() => {
    if (!open) return;
    let stopped = false;
    const load = () =>
      fetch("/api/plans", { cache: "no-store" })
        .then((res) => res.json())
        .then((data: PlanSummary[]) => !stopped && setPlans(data))
        .catch(() => {});
    load();
    const interval = setInterval(load, 4000);
    return () => {
      stopped = true;
      clearInterval(interval);
    };
  }, [open]);

  const waiting = plans.filter((p) => p.status === "awaiting-approval").length;

  const dismissNotification = (notice: PlanNotification) => {
    try {
      const seen = readSeenNotifications();
      seen.add(notice.notificationKey);
      localStorage.setItem(NOTIFICATION_STORAGE_KEY, JSON.stringify(Array.from(seen).slice(-100)));
    } catch {
      // The notice can still be dismissed for this page even if browser storage is disabled.
    }
    setNotifications((current) => current.filter((item) => item.notificationKey !== notice.notificationKey));
  };

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="Plans the agent has proposed, and how far each one got"
        className="absolute top-4 left-[15.5rem] rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
      >
        Plans
        {waiting > 0 && (
          <span className="ml-2 inline-block min-w-4 rounded-full bg-amber-500/20 text-amber-300 text-[11px] px-1.5">
            {waiting}
          </span>
        )}
      </button>

      {notifications.length > 0 && (
        <aside
          aria-live="polite"
          aria-label="Recent plan results"
          className="fixed top-16 left-4 z-[1301] w-[min(26rem,calc(100vw-2rem))] space-y-2"
        >
          {notifications.map((notice) => (
            <div key={notice.notificationKey} className="rounded-lg border border-neutral-700 bg-neutral-900 p-4 text-neutral-100 shadow-xl">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className={`text-xs font-semibold uppercase tracking-wide ${notice.status === "done" ? "text-emerald-400" : "text-red-400"}`}>
                    Plan {notice.status === "done" ? "finished" : "blocked"}
                  </p>
                  <p className="mt-1 truncate text-sm font-medium" title={notice.title}>{notice.title}</p>
                  <p className="mt-1 text-xs text-neutral-400">
                    {notice.stepsDone} of {notice.stepsTotal} steps - {new Date(notice.updatedUtc).toLocaleString()}
                  </p>
                </div>
                <button
                  type="button"
                  aria-label="Dismiss plan notification"
                  onClick={() => dismissNotification(notice)}
                  className="shrink-0 text-neutral-400 hover:text-neutral-100"
                >
                  ×
                </button>
              </div>
              <button
                type="button"
                onClick={() => {
                  dismissNotification(notice);
                  setSelected(notice.id);
                  setOpen(true);
                }}
                className="mt-3 rounded-md bg-neutral-800 px-3 py-1.5 text-xs text-neutral-200 hover:bg-neutral-700"
              >
                Open plan
              </button>
            </div>
          ))}
        </aside>
      )}

      {open && (
        <div
          className="fixed inset-y-0 left-0 right-[420px] z-50 flex items-center justify-center bg-black/60 p-6"
          onClick={() => setOpen(false)}
        >
          <div
            className="bg-neutral-900 text-neutral-100 rounded-lg max-w-2xl w-full max-h-[85vh] overflow-y-auto p-6"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-sm font-medium text-neutral-300">
                {selected ? "Plan" : "Plans"}
              </h2>
              <div className="flex items-center gap-4">
                {selected && (
                  <button
                    type="button"
                    onClick={() => setSelected(null)}
                    className="text-neutral-400 hover:text-neutral-100"
                  >
                    Back to list
                  </button>
                )}
                <button type="button" onClick={() => setOpen(false)} className="text-neutral-400 hover:text-neutral-100">
                  Close
                </button>
              </div>
            </div>

            {selected ? (
              <PlanCard planId={selected} />
            ) : plans.length === 0 ? (
              <p className="text-sm text-neutral-500">
                No plans yet. Turn on Plan mode and ask for a change: the agent explores, proposes a plan, and waits
                for your approval before touching anything.
              </p>
            ) : (
              <ul className="space-y-1.5">
                {plans.map((plan) => (
                  <li key={plan.id}>
                    <button
                      type="button"
                      onClick={() => setSelected(plan.id)}
                      className="w-full text-left flex items-center gap-3 bg-neutral-800/50 border border-neutral-800 rounded-md px-3 py-2 hover:bg-neutral-800 transition-colors"
                    >
                      <span className={`w-2 h-2 rounded-full shrink-0 ${DOT[plan.status] ?? "bg-neutral-500"}`} />
                      <span className="min-w-0 flex-1">
                        <span className="block text-sm text-neutral-200 truncate">{plan.title}</span>
                        <span className="block text-[11px] text-neutral-500">
                          {plan.status.replace("-", " ")} - {plan.stepsDone} of {plan.stepsTotal} steps -{" "}
                          {new Date(plan.updatedUtc).toLocaleString()}
                        </span>
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}
    </>
  );
}
