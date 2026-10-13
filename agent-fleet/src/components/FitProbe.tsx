"use client";

import { useCallback, useEffect, useState } from "react";
import { fitResultStatus, fitStaleReason, formatFitLine, formatFitSize, type FitLatestResult, type FitSizeResult } from "./fitPresentation";
import { formatWorkLine, type WorkScorecards } from "./workPresentation";

const CONTEXTS = [12288, 16384, 24576, 32768];

type FitNode = {
  name: string;
  model: string;
  vision: boolean;
  api?: string | null;
  contextLength?: number | null;
};

type FitJob = {
  id: string;
  node: string;
  model: string;
  state: string;
  currentContext: number | null;
  results: FitSizeResult[];
  recommendation: FitLatestResult["recommendation"];
  startedAtUtc: string;
  finishedAtUtc: string | null;
  reason: string | null;
};

export function FitProbe({
  node,
  version,
  defaultContextLength,
  fallbackNodeName,
  open,
  measureSignal,
  onOpenChange,
  onContextSaved,
  onReload,
}: {
  node: FitNode;
  version: string;
  defaultContextLength: number;
  fallbackNodeName: string | null;
  open: boolean;
  measureSignal: number;
  onOpenChange: (open: boolean) => void;
  onContextSaved: (contextLength: number, version: string) => void;
  onReload: () => void;
}) {
  const [latest, setLatest] = useState<FitLatestResult | null>(null);
  const [jobId, setJobId] = useState<string | null>(null);
  const [job, setJob] = useState<FitJob | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveConflict, setSaveConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const [work, setWork] = useState<WorkScorecards | null>(null);
  const [workError, setWorkError] = useState(false);

  const isHub = node.name === "hub";
  const unavailableReason = node.vision
    ? "Context fit is not available for a vision machine."
    : node.api?.toLowerCase() === "openai"
      ? "Context fit is not available for this machine's API."
      : null;

  const loadLatest = useCallback(async () => {
    try {
      const response = await fetch(`/api/lab/fit/latest?node=${encodeURIComponent(node.name)}`, { cache: "no-store" });
      if (response.status === 404) {
        setLatest(null);
        setLoadError(null);
        return;
      }
      if (!response.ok) throw new Error("Could not load the last measurement.");
      setLatest(await response.json());
      setLoadError(null);
    } catch {
      setLoadError("Could not load the last context-fit measurement.");
    }
  }, [node.name]);

  useEffect(() => {
    void loadLatest();
  }, [loadLatest]);

  useEffect(() => {
    let active = true;
    void fetch("/api/lab/scorecards?days=7", { cache: "no-store" })
      .then(async (response) => {
        if (!response.ok) throw new Error("Could not load work results.");
        return await response.json() as WorkScorecards;
      })
      .then((result) => { if (active) { setWork(result); setWorkError(false); } })
      .catch(() => { if (active) setWorkError(true); });
    return () => { active = false; };
  }, []);

  const refreshJob = useCallback(async (id: string) => {
    try {
      const response = await fetch("/api/lab/fit", { cache: "no-store" });
      if (!response.ok) throw new Error("Could not load the measurement.");
      const jobs: FitJob[] = await response.json();
      const next = jobs.find((candidate) => candidate.id === id) ?? null;
      setJob(next);
      if (!next) setLoadError("This measurement is no longer in the recent list.");
      else if (next.state !== "running") await loadLatest();
    } catch {
      setLoadError("Could not load the measurement progress.");
    }
  }, [loadLatest]);

  useEffect(() => {
    if (!jobId || job?.state !== "running") return;
    const timer = setInterval(() => void refreshJob(jobId), 2000);
    return () => clearInterval(timer);
  }, [jobId, job?.state, refreshJob]);

  const start = useCallback(async (confirmedHub = false) => {
    setStartError(null);
    setLoadError(null);
    setConfirming(false);
    setStarting(true);
    onOpenChange(true);
    try {
      const response = await fetch("/api/lab/fit", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ node: node.name, ...(isHub && confirmedHub ? { confirmHub: true } : {}) }),
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        setStartError(data.reason ?? "The measurement could not be started.");
        return;
      }
      setJobId(data.id);
      await refreshJob(data.id);
    } catch {
      setStartError("Could not reach the backend to start a measurement.");
    } finally {
      setStarting(false);
    }
  }, [isHub, node.name, onOpenChange, refreshJob]);

  const requestMeasure = useCallback(() => {
    onOpenChange(true);
    if (unavailableReason) return;
    if (isHub) {
      setConfirming(true);
      setStartError(null);
    } else {
      void start(false);
    }
  }, [isHub, onOpenChange, start, unavailableReason]);

  useEffect(() => {
    if (measureSignal > 0) requestMeasure();
  }, [measureSignal, requestMeasure]);

  async function cancel() {
    if (!jobId) return;
    setLoadError(null);
    const response = await fetch(`/api/lab/fit/${jobId}/cancel`, { method: "POST" });
    if (!response.ok) {
      setLoadError("Could not stop the measurement.");
      return;
    }
    await refreshJob(jobId);
  }

  async function useRecommendation() {
    if (!latest || latest.recommendation.context === null || latest.stale || latest.model !== node.model) return;
    setSaveError(null);
    setSaveConflict(false);
    setSaving(true);
    try {
      const response = await fetch(`/api/fleet-config/nodes/${encodeURIComponent(node.name)}/context`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ contextLength: latest.recommendation.context, ifVersion: version }),
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        setSaveError(data.reason ?? "The context setting could not be saved.");
        setSaveConflict(response.status === 409);
        return;
      }
      onContextSaved(latest.recommendation.context, data.version);
    } catch {
      setSaveError("Could not reach the backend to save the context setting.");
    } finally {
      setSaving(false);
    }
  }

  const staleReason = fitStaleReason(latest);
  const recommended = latest?.recommendation.context ?? null;
  const current = node.contextLength ?? defaultContextLength;
  const modelChanged = latest !== null && latest.model !== node.model;
  const completed = job?.results.length ?? 0;
  const progress = Math.max(0, Math.min(CONTEXTS.length, completed));
  const position = job?.currentContext
    ? CONTEXTS.indexOf(job.currentContext) + 1
    : Math.min(CONTEXTS.length, Math.max(1, completed));
  const remaining = job && completed > 0 && job.state === "running"
    ? `${Math.max(1, Math.ceil(((Date.now() - new Date(job.startedAtUtc).getTime()) / completed) * (CONTEXTS.length - completed) / 60000))}`
    : "unknown";

  return (
    <div className="mt-2 border-t border-neutral-700 pt-2 space-y-1.5">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-[11px] text-neutral-200">{formatFitLine(latest)}</p>
          <p className="text-[10px] text-neutral-500">Speed only, not coding skill.</p>
          <p className="text-[11px] text-neutral-300">
            {workError ? "Work last 7 days: could not load." : work === null ? "Work last 7 days: loading." : formatWorkLine(work, node.name, node.model, fallbackNodeName)}
          </p>
          {staleReason && <p className="text-[11px] text-amber-200">{staleReason}</p>}
          {modelChanged && !staleReason && <p className="text-[11px] text-amber-200">The model changed since this was measured.</p>}
          {unavailableReason && <p className="text-[11px] text-neutral-400">{unavailableReason}</p>}
          {loadError && <p className="text-[11px] text-red-300">{loadError}</p>}
        </div>
        {unavailableReason ? null : (
          <button type="button" onClick={requestMeasure} disabled={starting} className="shrink-0 text-[11px] text-sky-300 hover:underline disabled:opacity-50">
            {starting ? "Starting measurement" : latest?.stale ? "Measure again" : "Measure"}
          </button>
        )}
      </div>
      {startError && <p className="text-[11px] text-red-300">{startError}</p>}
      {confirming && isHub && (
        <div className="rounded border border-amber-700/50 bg-amber-950/20 p-2 space-y-1.5">
          <p className="text-[11px] text-amber-100">Loads {node.model} on this computer for about 5 minutes; graphics-heavy apps may slow down.</p>
          <div className="flex gap-3">
            <button type="button" onClick={() => void start(true)} disabled={starting} className="text-[11px] text-sky-300 hover:underline">Measure now</button>
            <button type="button" onClick={() => setConfirming(false)} className="text-[11px] text-neutral-400 hover:underline">Not now</button>
          </div>
        </div>
      )}
      {open && (
        <section aria-label="Context fit measurement" className="rounded border border-neutral-700 bg-neutral-900/70 p-2 space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-xs font-medium text-neutral-200">Context fit</h3>
            <button type="button" onClick={() => onOpenChange(false)} className="text-[11px] text-neutral-400 hover:underline">Close</button>
          </div>
          {job && (
            <>
              <p className="text-[11px] text-neutral-300">
                {job.state === "running"
                  ? `Size ${position} of ${CONTEXTS.length}, about ${remaining === "unknown" ? "unknown" : `${remaining} min`} left`
                  : `Measurement ${job.state}${job.reason ? `: ${job.reason}` : "."}`}
              </p>
              <div role="progressbar" aria-label="Context fit measurement progress" aria-valuemin={0} aria-valuemax={CONTEXTS.length} aria-valuenow={progress} className="h-1.5 rounded bg-neutral-700">
                <div className="h-full rounded bg-sky-500" style={{ width: `${(progress / CONTEXTS.length) * 100}%` }} />
              </div>
              <ul className="space-y-1 text-[11px] text-neutral-300">
                {CONTEXTS.map((context) => {
                  const result = job.results.find((item) => item.context === context);
                  return <li key={context}>{formatFitSize(context)} — {fitResultStatus(result, job.currentContext === context)}</li>;
                })}
              </ul>
              {job.state === "running" && <button type="button" onClick={() => void cancel()} className="text-[11px] text-red-300 hover:underline">Cancel measuring</button>}
            </>
          )}
          {!job && <p className="text-[11px] text-neutral-400">Choose Measure to begin.</p>}
          {latest && (
            <div className="text-[11px] text-neutral-300 space-y-1">
              {recommended !== null && <p>Suggest {formatFitSize(recommended)} (now {formatFitSize(current)}).</p>}
              {modelChanged && <p>The suggestion is hidden because the edited model differs from the measured model.</p>}
              {recommended !== null && !latest.stale && !modelChanged && recommended !== current && (
                <button type="button" onClick={() => void useRecommendation()} disabled={saving} className="text-sky-300 hover:underline disabled:opacity-50">
                  {saving ? "Saving context" : `Use ${formatFitSize(recommended)} and save`}
                </button>
              )}
              {saveError && <p className="text-red-300">{saveError}</p>}
              {saveConflict && <button type="button" onClick={onReload} className="text-sky-300 hover:underline">Reload</button>}
              <details className="text-[10px] text-neutral-400">
                <summary className="cursor-pointer text-sky-300">How this was measured</summary>
                <p className="mt-1">The model was loaded at each tested context size, asked for a short reply, and unloaded again. This measures speed only, not coding skill. The results are saved on this computer.</p>
              </details>
            </div>
          )}
        </section>
      )}
    </div>
  );
}
