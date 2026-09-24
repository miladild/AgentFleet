"use client";

import { useEffect, useRef, useState } from "react";
import { useAgent, UseAgentUpdate } from "@copilotkit/react-core/v2";

type SessionSummary = {
  id: string;
  title: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
};

const ACTIVE_SESSION_KEY = "fleet-active-session";
const AUTOSAVE_DEBOUNCE_MS = 800;

function deriveTitle(messages: readonly unknown[]): string {
  const firstUser = messages.find(
    (m): m is { role: string; content: unknown } =>
      typeof m === "object" && m !== null && (m as { role?: unknown }).role === "user",
  );
  if (!firstUser) return "Untitled";

  let text = "";
  const content = firstUser.content;
  if (typeof content === "string") {
    text = content;
  } else if (Array.isArray(content)) {
    const textPart = content.find(
      (part): part is { type: string; text: string } =>
        typeof part === "object" && part !== null && (part as { type?: unknown }).type === "text",
    );
    text = textPart?.text ?? "";
  }

  text = text.trim().replace(/\s+/g, " ");
  if (!text) return "Untitled";
  return text.length > 42 ? `${text.slice(0, 42).trimEnd()}…` : text;
}

function relativeTime(iso: string): string {
  const diffMs = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diffMs / 60000);
  if (mins < 1) return "now";
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}

export function SessionPanel() {
  const { agent, isReady } = useAgent({ updates: [UseAgentUpdate.OnMessagesChanged] });
  const [sessions, setSessions] = useState<SessionSummary[]>([]);
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const hydratedRef = useRef(false);
  const skipNextAutosaveRef = useRef(false);
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Initial load: fetch the session list, then resume whichever session was
  // active last (if any, and if it still exists), so a page reload doesn't
  // silently drop you into a blank chat.
  useEffect(() => {
    fetch("/api/sessions")
      .then((res) => res.json())
      .then((data: SessionSummary[]) => setSessions(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (!isReady || hydratedRef.current) return;
    hydratedRef.current = true;

    const lastId = localStorage.getItem(ACTIVE_SESSION_KEY);
    if (!lastId) return;

    fetch(`/api/sessions/${lastId}`)
      .then((res) => (res.ok ? res.json() : null))
      .then((data: { id: string; messages: unknown[] } | null) => {
        if (!data) {
          localStorage.removeItem(ACTIVE_SESSION_KEY);
          return;
        }
        skipNextAutosaveRef.current = true;
        agent.setMessages(data.messages as never);
        // The chat's id IS the AG-UI thread id: the backend journals every run under it, so a resumed
        // chat keeps its durable context (decisions, plans, artifacts) instead of starting a new one.
        agent.threadId = data.id;
        setActiveSessionId(data.id);
      })
      .catch(() => {});
    // agent identity is stable across the component's lifetime; only isReady gates this.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isReady]);

  // Debounced autosave whenever the active conversation's messages change.
  useEffect(() => {
    if (!isReady) return;
    if (skipNextAutosaveRef.current) {
      skipNextAutosaveRef.current = false;
      return;
    }
    if (agent.messages.length === 0) return;

    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      // A new chat's first run already used agent.threadId, so that is its id.
      const id = activeSessionId ?? agent.threadId ?? crypto.randomUUID();
      if (!activeSessionId) {
        setActiveSessionId(id);
        localStorage.setItem(ACTIVE_SESSION_KEY, id);
      }

      const title = deriveTitle(agent.messages);
      fetch(`/api/sessions/${id}`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ title, messages: agent.messages }),
      })
        .then((res) => res.json())
        .then((summary: SessionSummary) => {
          setSessions((prev) => {
            const rest = prev.filter((s) => s.id !== summary.id);
            return [summary, ...rest].sort((a, b) => b.updatedAtUtc.localeCompare(a.updatedAtUtc));
          });
        })
        .catch(() => {});
    }, AUTOSAVE_DEBOUNCE_MS);

    return () => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agent.messages, isReady, activeSessionId]);

  function startNewChat() {
    if (!isReady) return;
    skipNextAutosaveRef.current = true;
    agent.setMessages([]);
    agent.threadId = crypto.randomUUID();
    setActiveSessionId(null);
    localStorage.removeItem(ACTIVE_SESSION_KEY);
  }

  function openSession(id: string) {
    if (!isReady || id === activeSessionId) return;
    fetch(`/api/sessions/${id}`)
      .then((res) => (res.ok ? res.json() : null))
      .then((data: { id: string; messages: unknown[] } | null) => {
        if (!data) return;
        skipNextAutosaveRef.current = true;
        agent.setMessages(data.messages as never);
        agent.threadId = data.id;
        setActiveSessionId(data.id);
        localStorage.setItem(ACTIVE_SESSION_KEY, data.id);
      })
      .catch(() => {});
  }

  function deleteSession(id: string, event: React.MouseEvent) {
    event.stopPropagation();
    fetch(`/api/sessions/${id}`, { method: "DELETE" })
      .then(() => {
        setSessions((prev) => prev.filter((s) => s.id !== id));
        if (id === activeSessionId) startNewChat();
      })
      .catch(() => {});
  }

  const filtered = search.trim()
    ? sessions.filter((s) => s.title.toLowerCase().includes(search.trim().toLowerCase()))
    : sessions;

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <h2 className="text-xs uppercase tracking-wide text-neutral-500">Sessions</h2>
        <button
          type="button"
          onClick={startNewChat}
          className="text-xs px-2 py-1 rounded bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
        >
          New chat
        </button>
      </div>

      <input
        type="text"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        placeholder="Search sessions..."
        className="w-full text-sm px-3 py-1.5 rounded-md bg-neutral-900 border border-neutral-800 text-neutral-200 placeholder:text-neutral-600 focus:outline-none focus:border-neutral-600"
      />

      <div className="space-y-1 max-h-48 overflow-y-auto">
        {filtered.length === 0 ? (
          <p className="text-sm text-neutral-600">No sessions yet.</p>
        ) : (
          filtered.map((session) => (
            <div
              key={session.id}
              onClick={() => openSession(session.id)}
              className={`group flex items-center justify-between gap-2 px-3 py-2 rounded-md text-sm cursor-pointer border ${
                session.id === activeSessionId
                  ? "bg-neutral-800 border-neutral-600"
                  : "bg-neutral-900 border-neutral-800 hover:bg-neutral-800/60"
              }`}
            >
              <span className="truncate text-neutral-200">{session.title}</span>
              <span className="flex items-center gap-2 shrink-0">
                <span className="text-xs text-neutral-500">{relativeTime(session.updatedAtUtc)}</span>
                <button
                  type="button"
                  onClick={(e) => deleteSession(session.id, e)}
                  className="opacity-0 group-hover:opacity-100 text-neutral-500 hover:text-red-400 transition-opacity"
                  title="Delete session"
                >
                  ×
                </button>
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
