"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { CopyCommand, announceFleetChanged } from "./setupApi";
import {
  addressLabel,
  anyJobActive,
  confirmQuestion,
  jobFor,
  jobLine,
  latestLine,
  platformName,
  updateAllBlockedReason,
  updatableMachines,
  versionLine,
  type OllamaMachine,
  type OllamaUpdateJob,
  type OllamaVersionsReport,
} from "./ollamaUpdatePresentation";

const BUTTON = "text-xs px-3 py-1 rounded-md bg-sky-600/20 text-sky-200 border border-sky-600/40 hover:bg-sky-600/30 disabled:opacity-50 disabled:cursor-not-allowed";

function ManualSteps({ machine }: { machine: OllamaMachine }) {
  if (machine.manualSteps.length === 0) return null;
  return (
    <details className="text-[11px] text-neutral-400">
      <summary className="cursor-pointer text-sky-400 hover:underline">Update it by hand</summary>
      <div className="space-y-2 pt-1.5">
        {machine.manualSteps.map((step) => (
          <div key={step.platform} className="space-y-1">
            <p className="text-neutral-300">
              {platformName(step.platform)}:{" "}
              <a href={step.url} target="_blank" rel="noreferrer" className="font-mono text-sky-400 hover:underline">
                {step.asset}
              </a>
            </p>
            <p>
              SHA-256 of the file:{" "}
              {step.sha256 ? <span className="font-mono break-all text-neutral-300">{step.sha256}</span> : "not listed by the release, so it cannot be verified"}
            </p>
            {step.commands.map((command) => (
              <CopyCommand key={command} command={command} />
            ))}
            <p className="text-neutral-500">{step.note}</p>
          </div>
        ))}
      </div>
    </details>
  );
}

/**
 * Which version of Ollama each machine runs, and a way to update it from here. Nothing contacts GitHub until Check for
 * updates is pressed, and nothing is downloaded or installed until an Update button is pressed and confirmed.
 */
export function OllamaVersions() {
  const [report, setReport] = useState<OllamaVersionsReport | null>(null);
  const [jobs, setJobs] = useState<OllamaUpdateJob[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [checking, setChecking] = useState(false);
  const [starting, setStarting] = useState(false);
  const wasActive = useRef(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await fetch("/api/setup/ollama-versions", { cache: "no-store" });
      if (!res.ok) throw new Error();
      setReport(await res.json());
      setError(null);
    } catch {
      setError("Could not read the Ollama versions from the backend.");
    } finally {
      setLoading(false);
    }
  }, []);

  const loadJobs = useCallback(async () => {
    try {
      const res = await fetch("/api/setup/ollama-updates", { cache: "no-store" });
      if (res.ok) setJobs(await res.json());
    } catch {
      // The backend is restarting; the next tick tries again.
    }
  }, []);

  useEffect(() => {
    void load();
    void loadJobs();
  }, [load, loadJobs]);

  const active = anyJobActive(jobs);
  useEffect(() => {
    if (!active) return;
    const timer = setInterval(() => void loadJobs(), 1500);
    return () => clearInterval(timer);
  }, [active, loadJobs]);

  // When the last running update ends, read the versions again: the row shows the version the machine really reports.
  useEffect(() => {
    if (wasActive.current && !active) {
      void load();
      announceFleetChanged();
    }
    wasActive.current = active;
  }, [active, load]);

  async function check() {
    setChecking(true);
    setError(null);
    try {
      const res = await fetch("/api/setup/ollama-versions/check", { method: "POST" });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) setError(data.error ?? `HTTP ${res.status}`);
      await load();
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setChecking(false);
    }
  }

  async function update(machines: OllamaMachine[], all: boolean) {
    if (!report?.latest) return;
    if (!window.confirm(confirmQuestion(machines, report.latest.version))) return;
    setStarting(true);
    setError(null);
    try {
      const res = await fetch("/api/setup/ollama-update", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(all ? { all: true } : { url: machines[0].url }),
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) setError(data.error ?? `HTTP ${res.status}`);
      await loadJobs();
      await load();
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setStarting(false);
    }
  }

  const allBlocked = report ? updateAllBlockedReason(report) : "Reading the machines...";

  return (
    <div className="bg-neutral-800/50 border border-neutral-800 rounded-md p-3 space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-xs uppercase tracking-wide text-neutral-500 flex-1">Ollama versions</h3>
        <button type="button" onClick={() => void load()} disabled={loading} className="text-xs text-neutral-400 hover:text-neutral-200 disabled:opacity-50">
          {loading ? "Reading..." : "Refresh"}
        </button>
        <button type="button" onClick={() => void check()} disabled={checking} className={BUTTON}>
          {checking ? "Asking GitHub..." : "Check for updates"}
        </button>
        <button
          type="button"
          onClick={() => report && void update(updatableMachines(report), true)}
          disabled={starting || allBlocked !== null}
          title={allBlocked ?? "Update every machine that can be updated, one at a time"}
          className={BUTTON}
        >
          Update all
        </button>
      </div>

      <p className="text-[11px] text-neutral-500">
        {report ? latestLine(report.latest, new Date()) : "Reading the machines..."} The fleet asks GitHub only when you press Check for updates.
      </p>
      {report?.planRunning && (
        <p className="text-[11px] text-amber-300">A plan is running or approved, so updates are off until it has finished: an update restarts Ollama.</p>
      )}
      {error && <p className="text-[11px] text-red-300">{error}</p>}

      <div className="divide-y divide-neutral-800">
        {report?.machines.map((machine) => {
          const job = jobFor(jobs, machine.url);
          const showJob = job && (job.state === "queued" || job.state === "running" || job.state === "failed" || job.state === "done");
          return (
            <div key={machine.url} className="py-2 space-y-1">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className="font-mono text-xs text-neutral-100">{machine.nodes.join(", ")}</span>
                <span className="font-mono text-[11px] text-neutral-500">{addressLabel(machine.url)}</span>
                <span className={`text-[11px] flex-1 min-w-32 ${machine.answers ? "text-neutral-300" : "text-amber-300"}`}>
                  {versionLine(machine, report.latest)}
                </span>
                <button
                  type="button"
                  onClick={() => void update([machine], false)}
                  disabled={starting || !machine.canUpdate}
                  title={machine.blockedReason ?? `Update to ${report.latest?.version ?? "the newest release"}`}
                  className={BUTTON}
                >
                  Update
                </button>
              </div>
              {!machine.canUpdate && !machine.upToDate && report.latest && machine.blockedReason && (
                <p className="text-[11px] text-neutral-500">{machine.blockedReason}</p>
              )}
              {showJob && (
                <p className={`text-[11px] ${job.state === "failed" ? "text-red-300" : job.state === "done" ? "text-emerald-400" : "text-sky-300"}`}>
                  {jobLine(job)}
                </p>
              )}
              <ManualSteps machine={machine} />
            </div>
          );
        })}
        {report && report.machines.length === 0 && <p className="py-2 text-[11px] text-neutral-500">No Ollama machine is configured.</p>}
      </div>
    </div>
  );
}
