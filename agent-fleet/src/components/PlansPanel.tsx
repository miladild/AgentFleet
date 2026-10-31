"use client";

import { useEffect, useRef, useState } from "react";
import { PlanCard } from "./PlanCard";
import { planEvidenceClass, planEvidenceLabel, type PlanEvidence } from "./planEvidence";
import { mergeNotifications, notificationBadge, rememberDismissed } from "./planNotifications";

type PlanSummary = {
  id: string;
  title: string;
  status: string;
  stepsDone: number;
  stepsTotal: number;
  updatedUtc: string;
  evidence?: PlanEvidence | null;
  parkedSteps?: ParkedStepSummary[] | null;
};

type ParkedStepSummary = {
  stepId: number;
  title: string;
  parkedAtUtc: string | null;
  cause: string | null;
};

type PlanNotification = PlanSummary & {
  notificationKey: string;
  notificationType: "result" | "parked";
  parkedStep?: ParkedStepSummary;
};

const NOTIFICATION_STORAGE_KEY = "agent-fleet.seen-plan-notifications.v1";
const NOTIFICATION_MAX_AGE_MS = 36 * 60 * 60 * 1000;

function readSeenList(): string[] {
  try {
    const stored = JSON.parse(localStorage.getItem(NOTIFICATION_STORAGE_KEY) ?? "[]");
    return Array.isArray(stored) ? stored.filter((value): value is string => typeof value === "string") : [];
  } catch {
    return [];
  }
}

function readSeenNotifications(): Set<string> {
  return new Set(readSeenList());
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
  const [bellOpen, setBellOpen] = useState(false);
  const bellRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!bellOpen) return;
    const closeOutside = (event: MouseEvent) => {
      if (bellRef.current && !bellRef.current.contains(event.target as Node)) setBellOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setBellOpen(false);
    };
    document.addEventListener("mousedown", closeOutside);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("mousedown", closeOutside);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [bellOpen]);

  useEffect(() => {
    let stopped = false;
    const loadRecentCompletions = () =>
      fetch("/api/plans", { cache: "no-store" })
        .then((res) => (res.ok ? res.json() : Promise.reject(new Error("Could not load plans"))))
        .then((data: PlanSummary[]) => {
          if (stopped) return;
          const seen = readSeenNotifications();
          const cutoff = Date.now() - NOTIFICATION_MAX_AGE_MS;
          const results = data
            .filter((plan) => plan.status === "done" || plan.status === "blocked")
            .filter((plan) => Date.parse(plan.updatedUtc) >= cutoff)
            .map((plan) => ({ ...plan, notificationType: "result" as const, notificationKey: `${plan.id}:${plan.status}:${plan.updatedUtc}` }));
          const parked = data.flatMap((plan) => (plan.parkedSteps ?? []).map((step) => ({
            ...plan,
            notificationType: "parked" as const,
            parkedStep: step,
            notificationKey: `${plan.id}:parked:${step.stepId}:${step.parkedAtUtc ?? "unknown"}`,
          })));
          const recent = [...results, ...parked]
            .filter((plan) => !seen.has(plan.notificationKey));
          setNotifications((current) => mergeNotifications(current, recent));
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

  const dismissNotifications = (dismissed: PlanNotification[]) => {
    try {
      localStorage.setItem(NOTIFICATION_STORAGE_KEY, JSON.stringify(rememberDismissed(readSeenList(), dismissed.map((notice) => notice.notificationKey))));
    } catch {
      // The notices can still be dismissed for this page even if browser storage is disabled.
    }
    const keys = new Set(dismissed.map((notice) => notice.notificationKey));
    setNotifications((current) => current.filter((item) => !keys.has(item.notificationKey)));
  };

  const dismissNotification = (notice: PlanNotification) => dismissNotifications([notice]);
  const badge = notificationBadge(notifications.length);

  return (
    <>
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => setOpen(true)}
          title="Plans the agent has proposed, and how far each one got"
          className="rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
        >
          Plans
          {waiting > 0 && (
            <span className="ml-2 inline-block min-w-4 rounded-full bg-amber-500/20 text-amber-300 text-[11px] px-1.5">
              {waiting}
            </span>
          )}
        </button>
        <a
          href="/live"
          target="_blank"
          rel="noreferrer"
          title="Watch the fleet live in a page of its own: a plan being worked out, or one running"
          className="rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-cyan-300 border border-cyan-500/40 hover:bg-neutral-700 transition-colors"
        >
          ◉ Live
        </a>
        <div ref={bellRef} className="relative">
          <button
            type="button"
            onClick={() => setBellOpen((current) => !current)}
            aria-label={badge ? `Notifications, ${notifications.length} new` : "Notifications"}
            aria-expanded={bellOpen}
            title="Plan results and parked steps you have not looked at yet"
            className="relative rounded-full w-9 h-9 flex items-center justify-center bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
          >
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
              <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
            </svg>
            {badge && (
              <span className="absolute -top-1 -right-1 min-w-4 rounded-full bg-amber-500 text-neutral-950 text-[10px] font-semibold leading-4 px-1 text-center">
                {badge}
              </span>
            )}
          </button>

          {bellOpen && (
            <aside
              aria-live="polite"
              aria-label="Recent plan results"
              className="absolute top-full left-0 mt-2 z-[1301] w-[min(26rem,calc(100vw-2rem))] max-h-[70vh] overflow-y-auto rounded-lg border border-neutral-700 bg-neutral-900 p-3 shadow-xl space-y-2"
            >
              <div className="flex items-center justify-between px-1">
                <p className="text-xs font-semibold uppercase tracking-wide text-neutral-400">Notifications</p>
                {notifications.length > 0 && (
                  <button
                    type="button"
                    onClick={() => dismissNotifications(notifications)}
                    className="text-xs text-neutral-300 hover:text-neutral-100 underline underline-offset-2"
                  >
                    Clear all
                  </button>
                )}
              </div>
              {notifications.length === 0 && <p className="px-1 py-2 text-sm text-neutral-500">Nothing new.</p>}
          {notifications.map((notice) => (
            <div key={notice.notificationKey} className="rounded-lg border border-neutral-700 bg-neutral-900 p-4 text-neutral-100 shadow-xl">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className={`text-xs font-semibold uppercase tracking-wide ${notice.notificationType === "parked" ? "text-amber-400" : notice.status === "done" ? "text-emerald-400" : "text-red-400"}`}>
                    {notice.notificationType === "parked" ? "Step parked" : `Plan ${notice.status === "done" ? "finished" : "blocked"}`}
                  </p>
                  <p className="mt-1 truncate text-sm font-medium" title={notice.title}>{notice.title}</p>
                  <p className="mt-1 text-xs text-neutral-400">
                    {notice.notificationType === "parked" && notice.parkedStep
                      ? `Step ${notice.parkedStep.stepId} (${notice.parkedStep.title}) needs attention${notice.parkedStep.cause ? ` · ${notice.parkedStep.cause}` : ""}. The plan continues.`
                      : `${notice.stepsDone} of ${notice.stepsTotal} steps - ${new Date(notice.updatedUtc).toLocaleString()}`}
                  </p>
                  {notice.status === "done" && planEvidenceLabel(notice.evidence) && (
                    <span className={`mt-2 inline-flex rounded-full border px-2 py-0.5 text-[10px] ${planEvidenceClass(notice.evidence)}`}>
                      {planEvidenceLabel(notice.evidence)}
                    </span>
                  )}
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
                  setBellOpen(false);
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
        </div>
      </div>

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
                  <li key={plan.id} className="flex items-stretch gap-1.5">
                    <button
                      type="button"
                      onClick={() => setSelected(plan.id)}
                      className="min-w-0 flex-1 text-left flex items-center gap-3 bg-neutral-800/50 border border-neutral-800 rounded-md px-3 py-2 hover:bg-neutral-800 transition-colors"
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
                    {plan.status !== "rejected" && (
                      <a
                        href={`/live/${plan.id}`}
                        target="_blank"
                        rel="noreferrer"
                        title="Watch this plan live"
                        className="shrink-0 flex items-center rounded-md px-3 text-xs text-cyan-300 bg-neutral-800/50 border border-neutral-800 hover:bg-neutral-800"
                      >
                        ◉ Live
                      </a>
                    )}
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
