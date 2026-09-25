"use client";

import { useCallback, useEffect, useState } from "react";
import { CopyCommand, DownloadButton, type SetupCheck, type SetupReport, announceFleetChanged } from "./setupApi";

type NodeLite = { name: string; url: string; model: string; vision: boolean; fallback: boolean };

const AREAS: { key: SetupCheck["area"]; title: string }[] = [
  { key: "machines", title: "Machines and models" },
  { key: "sandbox", title: "Code sandbox" },
  { key: "tools", title: "Tools" },
  { key: "network", title: "Network and security" },
];

const ICONS: Record<SetupCheck["status"], { mark: string; className: string }> = {
  ok: { mark: "✓", className: "text-emerald-400 border-emerald-600/50" },
  warn: { mark: "!", className: "text-amber-300 border-amber-500/50" },
  fail: { mark: "✕", className: "text-red-400 border-red-500/50" },
  info: { mark: "i", className: "text-neutral-400 border-neutral-600" },
};

function isLocalAddress(url: string, report: SetupReport): boolean {
  try {
    const host = new URL(url).hostname.toLowerCase().replace(/^\[|\]$/g, "");
    return (
      host === "localhost" ||
      host === "127.0.0.1" ||
      host === "::1" ||
      host === report.hub.hostname.toLowerCase() ||
      report.hub.addresses.includes(host)
    );
  } catch {
    return false;
  }
}

const tagged = (model: string) => (model.includes(":") ? model : `${model}:latest`);

function CheckRow({
  check,
  onChanged,
  onOpenTab,
}: {
  check: SetupCheck;
  onChanged: () => void;
  onOpenTab: (tab: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const icon = ICONS[check.status];

  async function startOllama() {
    setBusy(true);
    setNote(null);
    try {
      const res = await fetch("/api/setup/start-ollama", { method: "POST" });
      const data = await res.json();
      setNote(data.error ?? data.message ?? null);
      onChanged();
    } catch {
      setNote("Could not reach the backend.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex gap-3 py-2">
      <span className={`shrink-0 w-5 h-5 rounded-full border flex items-center justify-center text-[11px] font-semibold ${icon.className}`}>
        {icon.mark}
      </span>
      <div className="min-w-0 flex-1 space-y-1">
        <p className="text-sm text-neutral-100">{check.title}</p>
        <p className="text-xs text-neutral-400">{check.detail}</p>
        {check.fix && check.status !== "ok" && <p className="text-xs text-neutral-300">{check.fix}</p>}
        {check.command && check.status !== "ok" && <CopyCommand command={check.command} />}
        {check.action && (check.status !== "ok" || check.action.kind === "tab") && (
          <div className="pt-0.5">
            {check.action.kind === "pull" && check.action.url && check.action.model ? (
              <DownloadButton url={check.action.url} model={check.action.model} label={check.action.label} onDone={onChanged} />
            ) : check.action.kind === "start-ollama" ? (
              <button
                type="button"
                onClick={startOllama}
                disabled={busy}
                className="text-xs px-3 py-1 rounded-md bg-sky-600/20 text-sky-200 border border-sky-600/40 hover:bg-sky-600/30 disabled:opacity-50"
              >
                {busy ? "Starting Ollama..." : check.action.label}
              </button>
            ) : check.action.kind === "tab" && check.action.tab ? (
              <button type="button" onClick={() => onOpenTab(check.action!.tab!)} className="text-xs text-sky-400 hover:underline">
                {check.action.label} →
              </button>
            ) : null}
          </div>
        )}
        {note && <p className="text-[11px] text-neutral-400">{note}</p>}
      </div>
    </div>
  );
}

/**
 * The Setup tab: what this computer can run, and a checklist of everything the fleet needs, each with the
 * fix, and a button where the fleet can do it itself (download a model, start Ollama).
 */
export function SetupSettings({
  nodes,
  onUseModel,
  onOpenTab,
}: {
  nodes: NodeLite[];
  onUseModel: (nodeName: string, model: string) => Promise<boolean>;
  onOpenTab: (tab: string) => void;
}) {
  const [report, setReport] = useState<SetupReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [installed, setInstalled] = useState<string[] | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await fetch("/api/setup", { cache: "no-store" });
      if (!res.ok) throw new Error();
      const data: SetupReport = await res.json();
      setReport(data);
      setError(null);
    } catch {
      setError("Could not reach the backend. If you just started the fleet, give it a few seconds.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
    const again = () => void load();
    window.addEventListener("fleet:changed", again);
    return () => window.removeEventListener("fleet:changed", again);
  }, [load]);

  const localNode = report ? nodes.find((node) => !node.vision && isLocalAddress(node.url, report)) : undefined;
  const localUrl = localNode?.url;

  // What is already downloaded on this computer's machine, to offer "use it" instead of a download.
  useEffect(() => {
    if (!localUrl) return;
    let cancelled = false;
    fetch(`/api/fleet-config/models?url=${encodeURIComponent(localUrl)}`)
      .then((res) => (res.ok ? res.json() : null))
      .then((list) => !cancelled && setInstalled(list ? (list.models ?? []).map((m: { name: string }) => m.name) : null))
      .catch(() => !cancelled && setInstalled(null));
    return () => {
      cancelled = true;
    };
  }, [localUrl, report]);

  if (!report) {
    return <p className="text-sm text-neutral-500">{error ?? "Checking your fleet..."}</p>;
  }

  const problems = report.checks.filter((c) => c.status === "fail" || c.status === "warn");
  const hw = report.hardware;
  const isInstalled = (model: string) => installed?.some((m) => tagged(m) === tagged(model)) ?? false;
  const viewedRemotely = typeof window !== "undefined" && !["localhost", "127.0.0.1", "[::1]"].includes(window.location.hostname);

  async function use(model: string) {
    if (localNode && (await onUseModel(localNode.name, model))) {
      announceFleetChanged();
      void load();
    }
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center gap-3">
        <div className="flex-1">
          <p className={`text-sm font-medium ${problems.some((p) => p.status === "fail") ? "text-amber-200" : "text-emerald-300"}`}>
            {problems.length === 0
              ? "Everything is set up."
              : `${problems.length} ${problems.length === 1 ? "thing needs" : "things need"} attention.`}
          </p>
          {report.firstRun && (
            <p className="text-xs text-neutral-400">Welcome. This is a new fleet: work down the list, most items have a button that does it for you.</p>
          )}
        </div>
        <button
          type="button"
          onClick={() => void load()}
          disabled={loading}
          className="text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-50"
        >
          {loading ? "Checking..." : "Check again"}
        </button>
      </div>

      <div className="bg-neutral-800/50 border border-neutral-800 rounded-md p-3 space-y-2">
        <h3 className="text-xs uppercase tracking-wide text-neutral-500">This computer</h3>
        <p className="text-xs text-neutral-300">
          {hw.ramGb.toFixed(0)} GB memory
          {hw.gpu ? ` · ${hw.gpu}${hw.vramGb > 0 ? ` (${hw.vramGb.toFixed(0)} GB)` : ""}` : " · no large graphics card found"}
          {hw.freeDiskGb !== null ? ` · ${hw.freeDiskGb.toFixed(0)} GB free for models` : ""}
        </p>
        {localNode ? (
          <div className="space-y-1.5">
            <p className="text-xs text-neutral-400">
              <span className="font-mono text-neutral-200">{localNode.name}</span> (this computer) uses{" "}
              <span className="font-mono text-neutral-200">{localNode.model}</span>
              {installed === null ? "." : isInstalled(localNode.model) ? ", which is downloaded." : ", which is not downloaded yet."} Models
              that suit this computer:
            </p>
            {report.suggestions.map((s) => {
              const inUse = tagged(s.model) === tagged(localNode.model);
              return (
                <div key={s.model} className="flex flex-wrap items-center gap-x-3 gap-y-1 bg-neutral-900/60 rounded px-2 py-1.5">
                  <span className="font-mono text-xs text-neutral-100">{s.model}</span>
                  {s.recommended && <span className="text-[10px] uppercase tracking-wide text-sky-300">recommended</span>}
                  <span className="text-[11px] text-neutral-500 flex-1 min-w-40">
                    {s.downloadGb ? `${s.downloadGb} GB. ` : ""}
                    {s.why}
                  </span>
                  {inUse && isInstalled(s.model) ? (
                    <span className="text-[11px] text-emerald-400">in use</span>
                  ) : isInstalled(s.model) ? (
                    <button type="button" onClick={() => void use(s.model)} className="text-xs text-sky-400 hover:underline">
                      Use it
                    </button>
                  ) : installed !== null ? (
                    <DownloadButton
                      compact
                      url={localNode.url}
                      model={s.model}
                      label={inUse ? "Download" : "Download and use"}
                      onDone={() => void use(s.model)}
                    />
                  ) : null}
                </div>
              );
            })}
            {installed && installed.length > 0 && (
              <p className="text-[11px] text-neutral-500">
                Already downloaded here:{" "}
                {installed.map((m, i) => (
                  <span key={m}>
                    {i > 0 && ", "}
                    {tagged(m) === tagged(localNode.model) ? (
                      <span className="font-mono text-neutral-300">{m}</span>
                    ) : (
                      <button type="button" onClick={() => void use(m)} className="font-mono text-sky-400 hover:underline" title="Use this model on this computer">
                        {m}
                      </button>
                    )}
                  </span>
                ))}
                . Click one to use it instead.
              </p>
            )}
          </div>
        ) : (
          <p className="text-xs text-neutral-500">None of your machines is this computer, so it only runs the fleet itself.</p>
        )}
      </div>

      {AREAS.map((area) => {
        const checks = report.checks.filter((c) => c.area === area.key);
        if (checks.length === 0 && !(area.key === "network" && viewedRemotely)) return null;
        return (
          <div key={area.key}>
            <h3 className="text-xs uppercase tracking-wide text-neutral-500">{area.title}</h3>
            <div className="divide-y divide-neutral-800">
              {checks.map((check) => (
                <CheckRow key={check.id} check={check} onChanged={() => void load()} onOpenTab={onOpenTab} />
              ))}
              {area.key === "network" && viewedRemotely && (
                <CheckRow
                  check={{
                    id: "web-remote",
                    area: "network",
                    status: "warn",
                    title: "This page was opened from another computer",
                    detail: `The web UI answers on the network at ${window.location.host}. There is no login, so anyone who can open this page can use the fleet, including its file and command tools.`,
                    fix: "Only keep it this way on a network you trust. Without -WebOnLan (or FLEET_WEB_HOST) the web UI answers this computer only.",
                  }}
                  onChanged={() => void load()}
                  onOpenTab={onOpenTab}
                />
              )}
            </div>
          </div>
        );
      })}

      <p className="text-[11px] text-neutral-600">
        The same checks run from a terminal with scripts/Test-Fleet.ps1. More in docs/getting-started.md and docs/troubleshooting.md.
      </p>
    </div>
  );
}
