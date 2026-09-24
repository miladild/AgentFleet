"use client";

import { useCallback, useEffect, useState } from "react";
import { useAgent } from "@copilotkit/react-core/v2";

type ContextEvent = {
  id: number;
  taskId: string | null;
  kind: string;
  actor: string | null;
  node: string | null;
  atUtc: string;
  payload: Record<string, unknown> & { message?: { content?: unknown }; role?: string };
  pinned: boolean;
};

type Artifact = {
  id: string;
  path: string;
  version: number;
  sha256: string | null;
  exists: boolean;
  matchesRecordedHash: boolean;
};

type Handoff = {
  id: string;
  createdAtUtc: string;
  envelope: {
    fromAgent: string | null;
    toAgent: string | null;
    goal: string;
    currentTask: string;
    nextAction: string;
    completionCondition: string;
    completedWork: string[];
    verificationEvidence: string[];
  };
};

type Inspection = {
  context: { id: string; title: string; eventCount: number; messageCount: number; updatedAtUtc: string };
  recentEvents: ContextEvent[];
  handoffs: Handoff[];
  artifacts: Artifact[];
  latestCheckpoint: { kind: string; sequence: number; createdAtUtc: string } | null;
  latestSummary: { version: number; text: string; startSequence: number; endSequence: number } | null;
  preview: { text: string; eventIds: number[]; artifactIds: string[]; wasTruncated: boolean };
  pinned?: ContextEvent[] | null;
};

type Delivery = { id: number; node: string | null; atUtc: string; payload: { text?: string; eventIds?: number[]; taskId?: string | null } };

const short = (text: unknown, limit = 140) => {
  const value = String(text ?? "").replace(/\s+/g, " ").trim();
  return value.length > limit ? `${value.slice(0, limit)}...` : value;
};

function describe(e: ContextEvent): string {
  const p = e.payload;
  switch (e.kind) {
    case "message":
    case "message-updated": {
      const content = p.message?.content;
      const text = typeof content === "string" ? content : Array.isArray(content) ? content.map((c) => (c as { text?: string }).text ?? "").join("") : "";
      return `${p.role ?? "message"}: ${short(text)}`;
    }
    case "decision":
      return short(p.text);
    case "tool-call":
      return `${p.name} ${short(p.arguments, 100)}`;
    case "tool-result":
      return `${p.name} -> ${short(p.result, 100)}`;
    case "route":
      return `sent to ${p.node} (${p.reason})`;
    case "verification":
      return `${p.passed ? "passed" : "FAILED"} ${short(p.command, 80)}`;
    case "plan-transition":
      return `${p.to}: ${short(p.note, 120)}`;
    case "error":
      return short(p.message);
    case "artifact-published":
    case "artifact-diverged":
      return short(p.path);
    default:
      return short(JSON.stringify(p), 100);
  }
}

const KIND_STYLE: Record<string, string> = {
  decision: "text-amber-300",
  "artifact-diverged": "text-red-400",
  error: "text-red-400",
  verification: "text-emerald-300",
  route: "text-sky-300",
};

/** The durable record of the current chat: what is pinned, what the next agent would receive, what each
 * agent actually received, the files earlier work produced (and whether they still match), and the timeline. */
export function ContextPanel() {
  const { agent } = useAgent();
  const [open, setOpen] = useState(false);
  const [data, setData] = useState<Inspection | null>(null);
  const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [missing, setMissing] = useState(false);
  const [text, setText] = useState("");
  const [asConstraint, setAsConstraint] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  // A plan can open its own context here (the Plans panel dispatches this event), so a run left going
  // overnight can be inspected in the morning without finding the chat it started in.
  const [viewing, setViewing] = useState<string | null>(null);
  const chatThreadId = agent.threadId;
  const threadId = viewing ?? chatThreadId;

  useEffect(() => {
    const onOpen = (event: Event) => {
      const id = (event as CustomEvent<string>).detail;
      if (typeof id === "string" && id) {
        setViewing(id);
        setData(null);
        setOpen(true);
      }
    };
    window.addEventListener("fleet:open-context", onOpen);
    return () => window.removeEventListener("fleet:open-context", onOpen);
  }, []);

  const load = useCallback(async () => {
    if (!threadId) return;
    try {
      const res = await fetch(`/api/contexts/${threadId}`, { cache: "no-store" });
      if (res.status === 404) {
        setMissing(true);
        setData(null);
        return;
      }
      if (!res.ok) return;
      setMissing(false);
      setData(await res.json());
      const list = await fetch(`/api/contexts/${threadId}/deliveries?limit=8`, { cache: "no-store" });
      if (list.ok) setDeliveries(await list.json());
    } catch {
      // Keep whatever was shown; the next poll will try again.
    }
  }, [threadId]);

  useEffect(() => {
    if (!open) return;
    load();
    const interval = setInterval(load, 5000);
    return () => clearInterval(interval);
  }, [open, load]);

  async function pin() {
    if (!text.trim() || !threadId) return;
    const res = await fetch(`/api/contexts/${threadId}/decisions`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ text, category: asConstraint ? "constraint" : "decision" }),
    });
    if (res.ok) {
      setText("");
      setNote("Pinned. Every agent that continues this work will receive it.");
      await load();
    } else {
      setNote("Could not pin that. Send a message in this chat first so it has a record.");
    }
  }

  async function unpin(eventId: number) {
    if (!threadId) return;
    const res = await fetch(`/api/contexts/${threadId}/events/${eventId}/pin?pinned=false`, { method: "POST", body: "{}" });
    setNote(res.ok ? "Unpinned. The record keeps it, but agents no longer receive it." : "Could not unpin that.");
    await load();
  }

  async function compact() {
    if (!threadId) return;
    const res = await fetch(`/api/contexts/${threadId}/compact`, { method: "POST", body: "{}" });
    const body = await res.json().catch(() => ({}));
    setNote(body.compacted ? "Older history summarised. Nothing was deleted." : (body.message ?? "Nothing to summarise yet."));
    await load();
  }

  const pinned = data?.pinned ?? [];
  const diverged = (data?.artifacts ?? []).filter((a) => !a.exists || !a.matchesRecordedHash).length;

  return (
    <>
      <button
        type="button"
        onClick={() => {
          setViewing(null);
          setData(null);
          setOpen(true);
        }}
        title="The durable record of this chat: pinned decisions, handoffs, files, and what each agent was given"
        className="absolute top-4 left-[21.25rem] rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
      >
        Context
      </button>

      {open && (
        <div
          className="fixed inset-y-0 left-0 right-[420px] z-[60] flex items-center justify-center bg-black/60 p-6"
          onClick={() => setOpen(false)}
        >
          <div
            className="bg-neutral-900 text-neutral-100 rounded-lg max-w-3xl w-full max-h-[85vh] overflow-y-auto p-6 space-y-5"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-sm font-medium text-neutral-300">{viewing ? "Context of a plan" : "Context"}</h2>
                <p className="text-[11px] font-mono text-neutral-600">{threadId}</p>
                {viewing && (
                  <button
                    type="button"
                    onClick={() => {
                      setViewing(null);
                      setData(null);
                    }}
                    className="text-[11px] text-sky-400 hover:underline"
                  >
                    Back to this chat
                  </button>
                )}
              </div>
              <div className="flex items-center gap-4">
                {data && (
                  <a href={`/api/contexts/${threadId}/export`} target="_blank" rel="noreferrer" className="text-xs text-sky-400 hover:underline">
                    Export JSON
                  </a>
                )}
                <button type="button" onClick={() => setOpen(false)} className="text-neutral-400 hover:text-neutral-100">
                  Close
                </button>
              </div>
            </div>

            {missing && (
              <p className="text-sm text-neutral-500">
                Nothing is recorded for this chat yet. Send a message, and everything that happens from then on (which machine
                answered, tool calls, decisions, plans) is kept here.
              </p>
            )}

            {data && (
              <>
                <p className="text-xs text-neutral-500">
                  {data.context.messageCount} messages, {data.context.eventCount} recorded events
                  {data.latestCheckpoint && <> - last checkpoint: {data.latestCheckpoint.kind}</>}
                  {diverged > 0 && <span className="text-red-400"> - {diverged} recorded file(s) changed or missing</span>}
                </p>

                <section>
                  <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">Pinned decisions and constraints</h3>
                  {pinned.length === 0 ? (
                    <p className="text-xs text-neutral-600">None yet. The assistant can pin one with record_decision, or you can.</p>
                  ) : (
                    <ul className="space-y-1">
                      {pinned.map((e) => (
                        <li key={e.id} className="text-xs text-neutral-300">
                          <span className="text-amber-300">[{String(e.payload.category ?? "decision")}]</span> {String(e.payload.text)}
                          {e.payload.reason ? <span className="text-neutral-500"> ({String(e.payload.reason)})</span> : null}
                          <span className="text-neutral-600"> - {e.actor === "user" ? "pinned by you" : "noted by the assistant"}</span>
                          <button
                            type="button"
                            onClick={() => unpin(e.id)}
                            className="ml-2 text-neutral-500 hover:text-red-400"
                            title="Stop sending this to agents (the record keeps it)"
                          >
                            unpin
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  <div className="mt-2 flex items-center gap-2">
                    <input
                      value={text}
                      onChange={(e) => setText(e.target.value)}
                      placeholder="Pin something every agent must respect..."
                      className="flex-1 text-xs px-3 py-1.5 rounded-md bg-neutral-950 border border-neutral-800 text-neutral-200 placeholder:text-neutral-600 focus:outline-none focus:border-neutral-600"
                    />
                    <label className="text-[11px] text-neutral-400 flex items-center gap-1">
                      <input type="checkbox" checked={asConstraint} onChange={(e) => setAsConstraint(e.target.checked)} /> constraint
                    </label>
                    <button type="button" onClick={pin} className="text-xs px-3 py-1.5 rounded-md bg-neutral-800 border border-neutral-700 hover:bg-neutral-700">
                      Pin
                    </button>
                  </div>
                </section>

                {data.handoffs[0] && (
                  <section>
                    <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">Latest handoff</h3>
                    <div className="text-xs text-neutral-300 space-y-0.5">
                      <div>
                        {data.handoffs[0].envelope.fromAgent ?? "an earlier step"} to {data.handoffs[0].envelope.toAgent ?? "whoever is next"}
                      </div>
                      <div className="text-neutral-400">Next: {short(data.handoffs[0].envelope.nextAction, 220)}</div>
                      <div className="text-neutral-400">Done when: {short(data.handoffs[0].envelope.completionCondition, 160)}</div>
                      {data.handoffs[0].envelope.completedWork.map((w, i) => (
                        <div key={i} className="text-neutral-500">- {short(w, 160)}</div>
                      ))}
                    </div>
                  </section>
                )}

                {data.artifacts.length > 0 && (
                  <section>
                    <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">Files earlier work recorded</h3>
                    <ul className="space-y-0.5">
                      {data.artifacts.map((a) => (
                        <li key={a.id} className="text-[11px] font-mono flex gap-2">
                          <span className={!a.exists ? "text-red-400" : a.matchesRecordedHash ? "text-emerald-400" : "text-amber-300"}>
                            {!a.exists ? "missing" : a.matchesRecordedHash ? "same" : "CHANGED"}
                          </span>
                          <span className="text-neutral-400 break-all">
                            {a.path} v{a.version}
                          </span>
                        </li>
                      ))}
                    </ul>
                  </section>
                )}

                <section>
                  <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">
                    What the next agent would receive
                    {data.preview.wasTruncated && <span className="text-amber-300"> (cut to fit)</span>}
                  </h3>
                  {data.preview.text ? (
                    <pre className="text-[11px] text-neutral-300 whitespace-pre-wrap bg-neutral-950 border border-neutral-800 rounded p-2 max-h-48 overflow-y-auto">
                      {data.preview.text}
                    </pre>
                  ) : (
                    <p className="text-xs text-neutral-600">Nothing yet: no pinned decision, handoff, file or check is recorded, so agents get only the conversation.</p>
                  )}
                </section>

                {deliveries.length > 0 && (
                  <section>
                    <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">What agents actually received</h3>
                    <ul className="space-y-1">
                      {[...deliveries].reverse().map((d) => (
                        <li key={d.id}>
                          <details>
                            <summary className="cursor-pointer text-[11px] text-neutral-400">
                              {new Date(d.atUtc).toLocaleTimeString()} - {d.node ?? "?"} - {d.payload.eventIds?.length ?? 0} events
                            </summary>
                            <pre className="mt-1 text-[10px] text-neutral-400 whitespace-pre-wrap bg-neutral-950 border border-neutral-800 rounded p-2 max-h-40 overflow-y-auto">
                              {d.payload.text}
                            </pre>
                          </details>
                        </li>
                      ))}
                    </ul>
                  </section>
                )}

                <section>
                  <h3 className="text-[10px] uppercase tracking-wide text-neutral-500 mb-1">Timeline (latest {data.recentEvents.length})</h3>
                  <div className="max-h-64 overflow-y-auto space-y-0.5">
                    {[...data.recentEvents].reverse().map((e) => (
                      <div key={e.id} className="text-[11px] leading-snug">
                        <span className="text-neutral-600">{new Date(e.atUtc).toLocaleTimeString()}</span>{" "}
                        <span className={KIND_STYLE[e.kind] ?? "text-neutral-300"}>{e.kind}</span>
                        {e.node && <span className="text-neutral-600"> [{e.node}]</span>}{" "}
                        <span className="text-neutral-400 break-words">{describe(e)}</span>
                      </div>
                    ))}
                  </div>
                </section>

                <div className="flex items-center gap-3">
                  <button type="button" onClick={compact} className="text-xs px-3 py-1.5 rounded-md bg-neutral-800 border border-neutral-700 hover:bg-neutral-700">
                    Summarise older history
                  </button>
                  {data.latestSummary && <span className="text-[11px] text-neutral-500">summary v{data.latestSummary.version} kept</span>}
                  {note && <span className="text-xs text-neutral-400">{note}</span>}
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </>
  );
}
