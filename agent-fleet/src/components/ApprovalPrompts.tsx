"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { describeApproval, formatCountdown, sortApprovals, type PendingApproval } from "./approvalPresentation";

const POLL_MS = 2000;
const TICK_MS = 1000;
// A request that never returns would stop every later poll, or leave an answer hidden with no message, so each gives up.
const REQUEST_TIMEOUT_MS = 10000;

const FOCUS = "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sky-400 focus-visible:ring-offset-2 focus-visible:ring-offset-neutral-950";
const APPROVE = `rounded-full px-3 py-1 text-xs font-medium bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 transition-colors ${FOCUS}`;
const REJECT = `rounded-full px-3 py-1 text-xs bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors ${FOCUS}`;

// A waiting request as this page holds it: when it runs out on this browser's clock, so the countdown can run between polls.
type Shown = PendingApproval & { dueAtMs: number };

/**
 * The chat tool calls that wait for the user's answer. It is mounted in every page, so a request that arrives while the
 * user is on the other page is still seen. The list is read every two seconds while the tab is visible.
 */
export function ApprovalPrompts() {
  const [items, setItems] = useState<Shown[]>([]);
  const [now, setNow] = useState(() => Date.now());
  const [failures, setFailures] = useState<Record<string, string>>({});
  // Requests the user has answered. A poll that was already on its way can still list one, so it stays hidden until a
  // poll no longer lists it.
  const answered = useRef(new Set<string>());

  const load = useCallback(async () => {
    try {
      const response = await fetch("/api/approvals", { cache: "no-store", signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
      if (!response.ok) return;
      const data: unknown = await response.json();
      const received = Date.now();
      const pending = Array.isArray(data) ? (data as PendingApproval[]) : [];
      const listed = new Set(pending.map((item) => item.id));
      for (const id of answered.current) {
        if (!listed.has(id)) answered.current.delete(id);
      }
      setNow(received);
      setItems(sortApprovals(
        pending
          .filter((item) => !answered.current.has(item.id))
          .map((item) => ({ ...item, dueAtMs: received + item.secondsLeft * 1000 })),
      ));
    } catch {
      // Keep the cards as they are; the next poll tries again.
    }
  }, []);

  useEffect(() => {
    let stopped = false;
    let running = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      clearTimeout(timer);
      if (stopped || running || document.hidden) return;
      running = true;
      await load();
      running = false;
      if (!stopped && !document.hidden) timer = setTimeout(poll, POLL_MS);
    };
    // A hidden tab stops polling; coming back to it reads the list at once.
    const onVisibilityChange = () => {
      if (document.hidden) clearTimeout(timer);
      else void poll();
    };
    document.addEventListener("visibilitychange", onVisibilityChange);
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [load]);

  const hasItems = items.length > 0;
  useEffect(() => {
    if (!hasItems) return;
    const timer = setInterval(() => setNow(Date.now()), TICK_MS);
    return () => clearInterval(timer);
  }, [hasItems]);

  async function decide(item: Shown, decision: "approve" | "reject") {
    answered.current.add(item.id);
    setItems((current) => current.filter((candidate) => candidate.id !== item.id));

    let sent = false;
    try {
      const response = await fetch(`/api/approvals/${item.id}/${decision}`, { method: "POST", cache: "no-store", signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
      // A 404 means the request was already answered or ran out: it is gone either way, so it is not a failure.
      sent = response.ok || response.status === 404;
    } catch {
      sent = false;
    }

    if (!sent) {
      answered.current.delete(item.id);
      setItems((current) => (current.some((candidate) => candidate.id === item.id) ? current : sortApprovals([...current, item])));
      setFailures((current) => ({ ...current, [item.id]: "Could not send your answer. Try again." }));
    }
    await load();
  }

  if (items.length === 0) return null;

  return (
    <section
      aria-label="Approval requests"
      aria-live="assertive"
      className="fixed inset-x-4 top-4 z-[6100] flex max-h-[50vh] flex-col gap-3 overflow-y-auto sm:left-auto sm:w-96"
    >
      {items.map((item) => {
        const titleId = `approval-${item.id}`;
        return (
          <article
            key={item.id}
            aria-labelledby={titleId}
            className="space-y-2 rounded-md border border-amber-500/40 bg-neutral-950 p-3 text-sm shadow-lg"
          >
            <div className="flex items-center justify-between gap-3 text-[11px]">
              <span className="font-mono text-neutral-400">{item.tool}</span>
              <span role="timer" className="tabular-nums text-amber-300">
                Time left {formatCountdown(Math.ceil((item.dueAtMs - now) / 1000))}
              </span>
            </div>
            <p id={titleId} className="break-words text-neutral-100">{describeApproval(item)}</p>
            <pre className="max-h-32 overflow-auto whitespace-pre-wrap break-all rounded border border-neutral-800 bg-neutral-900 p-2 font-mono text-[11px] text-neutral-300">
              {item.arguments}
            </pre>
            {failures[item.id] && <p className="text-xs text-red-300">{failures[item.id]}</p>}
            <div className="flex gap-2">
              <button type="button" onClick={() => void decide(item, "approve")} className={APPROVE}>
                Approve
              </button>
              <button type="button" onClick={() => void decide(item, "reject")} className={REJECT}>
                Reject
              </button>
            </div>
          </article>
        );
      })}
    </section>
  );
}
