"use client";

import { useEffect, useState, useSyncExternalStore } from "react";

export type ModelPull = {
  id: string;
  url: string;
  model: string;
  state: "running" | "done" | "failed" | "cancelled";
  status: string;
  completed: number;
  total: number;
  error: string | null;
};

export type SetupAction = { kind: "pull" | "start-ollama" | "tab"; label: string; url?: string | null; model?: string | null; tab?: string | null };

export type SetupCheck = {
  id: string;
  area: "machines" | "sandbox" | "tools" | "network";
  status: "ok" | "warn" | "fail" | "info";
  title: string;
  detail: string;
  fix?: string | null;
  command?: string | null;
  action?: SetupAction | null;
};

export type ModelSuggestion = { model: string; downloadGb: number | null; why: string; recommended: boolean };

export type HubInfo = { hostname: string; osFamily: "windows" | "linux" | "macos"; addresses: string[]; backendOnLan: boolean; backendUrls: string[] };

export type SetupReport = {
  firstRun: boolean;
  ready: boolean;
  hardware: {
    os: string;
    osFamily: string;
    ramGb: number;
    gpu: string | null;
    vramGb: number;
    unifiedMemory: boolean;
    modelsFolder: string;
    freeDiskGb: number | null;
  };
  suggestions: ModelSuggestion[];
  triageSuggestion: string;
  hub: HubInfo;
  checks: SetupCheck[];
};

/** Opens the Config panel on a tab from anywhere on the page. */
export function openConfig(tab = "setup") {
  window.dispatchEvent(new CustomEvent("fleet:open-config", { detail: { tab } }));
}

/** Tells listeners (the first-run banner, the node list) that the fleet changed and they should look again. */
export function announceFleetChanged() {
  window.dispatchEvent(new Event("fleet:changed"));
}

export function sameMachine(a: string, b: string): boolean {
  try {
    const withScheme = (value: string) => (/^https?:\/\//i.test(value) ? value : `http://${value}`);
    const left = new URL(withScheme(a));
    const right = new URL(withScheme(b));
    const port = (url: URL) => url.port || (url.protocol === "http:" ? "11434" : "443");
    return left.hostname.toLowerCase() === right.hostname.toLowerCase() && port(left) === port(right);
  } catch {
    return false;
  }
}

// One shared poller for every download on the page: fast while something downloads, slow otherwise, off
// when nothing on the page shows downloads.
let pulls: ModelPull[] = [];
const listeners = new Set<() => void>();
let timer: ReturnType<typeof setTimeout> | null = null;

async function refreshPulls() {
  try {
    const res = await fetch("/api/setup/pulls", { cache: "no-store" });
    if (res.ok) {
      const next: ModelPull[] = await res.json();
      const finished = next.some((p) => p.state === "done" && !pulls.some((old) => old.id === p.id && old.state === "done"));
      pulls = next;
      listeners.forEach((listener) => listener());
      if (finished) announceFleetChanged();
    }
  } catch {
    // The backend is restarting; try again on the next tick.
  }
  schedule();
}

function schedule() {
  if (timer) clearTimeout(timer);
  timer = null;
  if (listeners.size === 0) return;
  timer = setTimeout(refreshPulls, pulls.some((p) => p.state === "running") ? 1000 : 5000);
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  if (listeners.size === 1) void refreshPulls();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer) {
      clearTimeout(timer);
      timer = null;
    }
  };
}

const EMPTY: ModelPull[] = [];

export function usePulls(): ModelPull[] {
  return useSyncExternalStore(subscribe, () => pulls, () => EMPTY);
}

export async function startPull(url: string, model: string): Promise<string | null> {
  const res = await fetch("/api/setup/pull", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ url, model }),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) return data.error ?? `HTTP ${res.status}`;
  await refreshPulls();
  return null;
}

function gb(bytes: number) {
  return (bytes / 1024 / 1024 / 1024).toFixed(bytes > 10 * 1024 ** 3 ? 0 : 1);
}

/**
 * Downloads a model onto a machine and shows its progress. Keeps going if the panel is closed; opening it
 * again picks the download back up.
 */
export function DownloadButton({
  url,
  model,
  label,
  sizeGb,
  onDone,
  compact = false,
}: {
  url: string;
  model: string;
  label?: string;
  sizeGb?: number | null;
  onDone?: () => void;
  compact?: boolean;
}) {
  const all = usePulls();
  const pull = [...all].reverse().find((p) => p.model === model && sameMachine(p.url, url));
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [reported, setReported] = useState<string | null>(null);

  useEffect(() => {
    if (pull?.state === "done" && reported !== pull.id) {
      setReported(pull.id);
      onDone?.();
    }
  }, [pull, reported, onDone]);

  async function start() {
    setStarting(true);
    setError(await startPull(url, model));
    setStarting(false);
  }

  if (pull?.state === "running") {
    const percent = pull.total > 0 ? Math.floor((pull.completed / pull.total) * 100) : 0;
    return (
      <div className={`space-y-1 ${compact ? "min-w-48" : "min-w-64"}`}>
        <div className="flex items-center justify-between gap-2 text-[11px] text-neutral-400">
          <span className="truncate">
            {pull.total > 0 ? `${gb(pull.completed)} of ${gb(pull.total)} GB · ${percent}%` : pull.status}
          </span>
          <button
            type="button"
            onClick={() => fetch(`/api/setup/pulls/${pull.id}/cancel`, { method: "POST" }).then(refreshPulls)}
            className="text-neutral-500 hover:text-red-300"
          >
            Cancel
          </button>
        </div>
        <div className="h-1.5 rounded-full bg-neutral-800 overflow-hidden">
          <div className="h-full bg-sky-500 transition-all" style={{ width: `${Math.max(percent, 2)}%` }} />
        </div>
      </div>
    );
  }

  if (pull?.state === "done") {
    return <span className="text-[11px] text-emerald-400">Downloaded {model}.</span>;
  }

  const failed = pull?.state === "failed" ? pull.error : null;
  return (
    <div className="space-y-1">
      <button
        type="button"
        onClick={start}
        disabled={starting}
        className="text-xs px-3 py-1 rounded-md bg-sky-600/20 text-sky-200 border border-sky-600/40 hover:bg-sky-600/30 disabled:opacity-50"
      >
        {starting ? "Starting..." : (label ?? `Download ${model}`)}
        {sizeGb ? <span className="text-sky-300/70"> ({sizeGb} GB)</span> : null}
      </button>
      {(error || failed) && <p className="text-[11px] text-red-300">{error ?? failed}</p>}
      {pull?.state === "cancelled" && <p className="text-[11px] text-neutral-500">Cancelled. Downloading again continues where it stopped.</p>}
    </div>
  );
}

/** A command to copy, with a Copy button. */
export function CopyCommand({ command }: { command: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="flex items-start gap-2 bg-neutral-950 border border-neutral-800 rounded px-2 py-1.5">
      <code className="flex-1 text-[11px] text-neutral-200 font-mono whitespace-pre-wrap break-all">{command}</code>
      <button
        type="button"
        onClick={() => {
          void navigator.clipboard?.writeText(command).then(() => {
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          });
        }}
        className="shrink-0 text-[11px] text-sky-400 hover:underline"
      >
        {copied ? "Copied" : "Copy"}
      </button>
    </div>
  );
}
