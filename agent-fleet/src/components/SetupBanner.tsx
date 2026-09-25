"use client";

import { useCallback, useEffect, useState } from "react";
import { openConfig, type SetupReport } from "./setupApi";

/**
 * Shown above the chat list when the fleet is not ready to answer yet, or on its very first run: says what is
 * missing in a line and opens the Setup tab, where each item has its fix.
 */
export function SetupBanner() {
  const [report, setReport] = useState<SetupReport | null>(null);
  const [offline, setOffline] = useState(false);

  const load = useCallback(async () => {
    try {
      const res = await fetch("/api/setup", { cache: "no-store" });
      if (!res.ok) throw new Error();
      setReport(await res.json());
      setOffline(false);
    } catch {
      setOffline(true);
    }
  }, []);

  useEffect(() => {
    void load();
    const again = () => void load();
    window.addEventListener("fleet:changed", again);
    return () => window.removeEventListener("fleet:changed", again);
  }, [load]);

  // While the backend is still starting (or stopped), look again every few seconds.
  useEffect(() => {
    if (!offline) return;
    const timer = setInterval(() => void load(), 4000);
    return () => clearInterval(timer);
  }, [offline, load]);

  if (offline) {
    return (
      <div className="rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm text-amber-100">
        The fleet&apos;s backend is not answering yet. If you just started it, give it a few seconds. If this stays, its log is in the
        backend folder (logs), and scripts/Test-Fleet.ps1 says what is wrong.
      </div>
    );
  }

  if (!report) return null;
  const failing = report.checks.filter((check) => check.status === "fail");
  if (failing.length === 0 && !(report.firstRun && !report.ready)) return null;

  return (
    <div className="rounded-md border border-sky-500/40 bg-sky-500/10 px-3 py-2.5 space-y-1.5">
      <p className="text-sm font-medium text-sky-100">
        {report.ready ? "Almost there" : report.firstRun ? "Welcome! A few steps before the first chat" : "The fleet cannot answer yet"}
      </p>
      <ul className="text-xs text-sky-100/80 list-disc pl-4 space-y-0.5">
        {failing.slice(0, 3).map((check) => (
          <li key={check.id}>{check.title}</li>
        ))}
        {failing.length > 3 && <li>and {failing.length - 3} more</li>}
      </ul>
      <button
        type="button"
        onClick={() => openConfig("setup")}
        className="text-xs px-3 py-1 rounded-md bg-sky-500/20 text-sky-100 border border-sky-400/50 hover:bg-sky-500/30"
      >
        Open setup
      </button>
    </div>
  );
}
