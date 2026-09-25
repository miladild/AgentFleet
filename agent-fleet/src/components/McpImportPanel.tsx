"use client";

import { useEffect, useState } from "react";

type Candidate = { name: string; type: string; summary: string; hasInlineSecret: boolean; usesInputs: boolean; alreadyAdded: boolean };
type Source = { app: string; path: string; servers: Candidate[]; error: string | null };

/**
 * Lists the MCP servers already set up in VS Code, Claude Desktop, Claude Code, Cursor and Windsurf on the hub, and
 * adds the ticked ones to the fleet. Secret values stay on the backend: this list only shows that one is there.
 */
export function McpImportPanel({ onImported }: { onImported: () => void }) {
  const [sources, setSources] = useState<Source[] | null>(null);
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  async function load() {
    try {
      const res = await fetch("/api/tools/import", { cache: "no-store" });
      setSources(res.ok ? await res.json() : []);
    } catch {
      setSources([]);
      setMessage({ text: "Could not reach the backend.", error: true });
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function importFrom(source: Source) {
    const names = source.servers.filter((s) => picked[`${source.path}|${s.name}`]).map((s) => s.name);
    if (names.length === 0) return setMessage({ text: "Tick the servers to add first.", error: true });
    setBusy(source.path);
    setMessage(null);
    try {
      const res = await fetch("/api/tools/import", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ path: source.path, names }),
      });
      const data = await res.json();
      if (!res.ok) {
        setMessage({ text: data.error ?? `HTTP ${res.status}`, error: true });
        return;
      }
      const failed = (data.statuses ?? []).filter((s: { connected: boolean; enabled: boolean }) => s.enabled && !s.connected);
      setMessage({
        text: `Added ${data.imported.join(", ")} from ${source.app}.${failed.length ? ` ${failed.length} did not start: see its card above.` : " Connected."}`,
        error: failed.length > 0,
      });
      setPicked({});
      onImported();
      await load();
    } catch {
      setMessage({ text: "Could not reach the backend.", error: true });
    } finally {
      setBusy(null);
    }
  }

  if (sources === null) return <p className="text-xs text-neutral-500">Looking for other apps&apos; MCP settings...</p>;

  return (
    <div className="space-y-3">
      <p className="text-xs text-neutral-500">
        MCP servers you already set up in VS Code, Claude Desktop, Claude Code, Cursor or Windsurf on this computer (for the account the
        backend runs as). Tick the ones the fleet should have too.
      </p>
      {sources.length === 0 && <p className="text-xs text-neutral-600">None found. Paste a server&apos;s JSON instead, or pick one from the list.</p>}
      {sources.map((source) => (
        <div key={source.path} className="border border-neutral-800 rounded-md p-2 space-y-1.5">
          <div className="flex items-baseline justify-between gap-2">
            <span className="text-sm text-neutral-100">{source.app}</span>
            <span className="text-[10px] text-neutral-600 font-mono truncate" title={source.path}>{source.path}</span>
          </div>
          {source.error && <p className="text-[11px] text-red-300">{source.error}</p>}
          {source.servers.map((server) => {
            const key = `${source.path}|${server.name}`;
            return (
              <label key={key} className={`flex items-start gap-2 text-xs ${server.alreadyAdded ? "opacity-50" : ""}`}>
                <input
                  type="checkbox"
                  className="mt-0.5"
                  disabled={server.alreadyAdded}
                  checked={!!picked[key]}
                  onChange={(e) => setPicked({ ...picked, [key]: e.target.checked })}
                />
                <span className="min-w-0">
                  <span className="font-mono text-neutral-200">{server.name}</span>
                  <span className="text-[10px] uppercase tracking-wide text-neutral-500"> {server.type}</span>
                  {server.alreadyAdded && <span className="text-[11px] text-neutral-500"> already added</span>}
                  <span className="block text-[11px] text-neutral-500 font-mono break-all">{server.summary}</span>
                  {server.hasInlineSecret && (
                    <span className="block text-[11px] text-amber-300">
                      Has a secret written into it. It would be copied into fleet.config.json as it is; better, set it as an environment
                      variable on the hub afterwards and replace it with {"${env:NAME}"} (Edit).
                    </span>
                  )}
                  {server.usesInputs && (
                    <span className="block text-[11px] text-amber-300">
                      Uses {"${input:...}"}, which VS Code asks you for. The fleet cannot ask: after adding it, Edit it and use {"${env:NAME}"} instead.
                    </span>
                  )}
                </span>
              </label>
            );
          })}
          {source.servers.some((s) => !s.alreadyAdded) && (
            <button
              type="button"
              onClick={() => importFrom(source)}
              disabled={busy !== null}
              className="text-xs px-3 py-1 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-50"
            >
              {busy === source.path ? "Adding..." : "Add the ticked ones"}
            </button>
          )}
        </div>
      ))}
      {message && <p className={`text-xs ${message.error ? "text-amber-300" : "text-emerald-300"}`}>{message.text}</p>}
    </div>
  );
}
