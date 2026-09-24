"use client";

import { useCallback, useEffect, useState } from "react";

type Storage = { bytes: number; contexts: number; chats: number; events: number; oldestActivityUtc: string | null };
type CleanupItem = { id: string; title: string; lastActivityUtc: string; messageCount: number };
type CleanupResult = {
  dryRun: boolean;
  olderThanDays: number | null;
  chats: CleanupItem[];
  keptForPlans: number;
  deliveriesTrimmed: number;
  bytesBefore: number;
  bytesAfter: number;
};
type StorageResponse = { storage: Storage; deleteAfterDays: number; keepDeliveriesPerChat: number; preview: CleanupResult };

const AUTO_CHOICES: [number, string][] = [
  [0, "Keep everything"],
  [30, "After 30 days"],
  [90, "After 90 days"],
  [180, "After 180 days"],
  [365, "After a year"],
];

const NOW_CHOICES: [number, string][] = [
  [7, "a week"],
  [30, "30 days"],
  [90, "90 days"],
  [180, "180 days"],
  [365, "a year"],
];

function formatBytes(bytes: number): string {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function formatDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }) : "";
}

/** How much the durable record holds, how long it keeps chats, and deleting old ones now. */
export function HistorySettings({
  deleteAfterDays,
  onSaved,
}: {
  deleteAfterDays: number;
  onSaved: (days: number, message: string) => void;
}) {
  const [info, setInfo] = useState<StorageResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [savingAuto, setSavingAuto] = useState(false);
  const [days, setDays] = useState(deleteAfterDays > 0 ? deleteAfterDays : 90);
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [done, setDone] = useState<string | null>(null);

  const load = useCallback(async (olderThanDays: number) => {
    try {
      const res = await fetch(`/api/contexts/storage?olderThanDays=${olderThanDays}`, { cache: "no-store" });
      if (!res.ok) {
        setError(res.status === 404 ? "This backend is older and has no history settings yet. Deploy the new build." : "Could not read the record.");
        return;
      }
      setInfo(await res.json());
      setError(null);
    } catch {
      setError("Could not reach the backend.");
    }
  }, []);

  useEffect(() => {
    setConfirming(false);
    load(days);
  }, [days, load]);

  async function saveAuto(value: number) {
    setSavingAuto(true);
    try {
      const res = await fetch("/api/fleet-config", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ history: { deleteAfterDays: value } }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error ?? "Save failed.");
        return;
      }
      onSaved(
        data.history?.deleteAfterDays ?? value,
        value > 0 ? `Saved. Chats not used for ${value} days are deleted from now on.` : "Saved. Every chat is kept.",
      );
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setSavingAuto(false);
    }
  }

  async function deleteNow() {
    setDeleting(true);
    setDone(null);
    try {
      const res = await fetch("/api/contexts/cleanup", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ olderThanDays: days }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.error ?? "The cleanup failed.");
        return;
      }
      const result = data as CleanupResult;
      setDone(
        `Deleted ${result.chats.length} chat${result.chats.length === 1 ? "" : "s"}. The record went from ${formatBytes(result.bytesBefore)} to ${formatBytes(result.bytesAfter)}.`,
      );
      await load(days);
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setDeleting(false);
      setConfirming(false);
    }
  }

  const preview = info?.preview;
  const shown = preview?.chats.slice(0, 8) ?? [];

  return (
    <div className="space-y-5">
      <p className="text-xs text-neutral-500">
        The fleet keeps a record of every chat on this machine: the messages, each tool call and its result (including file
        contents the assistant read), pinned decisions, and what each model was handed. It is what lets work continue on another
        machine or after a restart. It never leaves this machine; deleting a chat removes it from the file.
      </p>

      {error && <p className="text-xs text-red-300">{error}</p>}

      {info && (
        <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-neutral-400 bg-neutral-800/60 border border-neutral-700 rounded-md px-3 py-2">
          <span>
            <span className="text-neutral-200">{formatBytes(info.storage.bytes)}</span> on disk
          </span>
          <span>
            <span className="text-neutral-200">{info.storage.chats}</span> chat{info.storage.chats === 1 ? "" : "s"}
          </span>
          <span>
            <span className="text-neutral-200">{info.storage.events.toLocaleString()}</span> recorded entries
          </span>
          {info.storage.oldestActivityUtc && <span>least recently used: {formatDate(info.storage.oldestActivityUtc)}</span>}
        </div>
      )}

      <div className="space-y-1.5">
        <label htmlFor="history-auto" className="block text-xs uppercase tracking-wide text-neutral-500">
          Delete chats automatically
        </label>
        <div className="flex items-center gap-3">
          <select
            id="history-auto"
            value={deleteAfterDays}
            disabled={savingAuto}
            onChange={(e) => saveAuto(Number(e.target.value))}
            className="text-xs px-2 py-1.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200"
          >
            {AUTO_CHOICES.some(([value]) => value === deleteAfterDays) ? null : (
              <option value={deleteAfterDays}>After {deleteAfterDays} days</option>
            )}
            {AUTO_CHOICES.map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
          {savingAuto && <span className="text-[11px] text-neutral-500">Saving...</span>}
        </div>
        <p className="text-[11px] text-neutral-500">
          A chat is deleted with its whole record once nothing has happened in it for that long. A chat that a plan still runs
          in, or waits in, is kept. Checked every six hours. Whatever you choose, each chat keeps only its newest{" "}
          {info?.keepDeliveriesPerChat ?? 30} copies of what a model was handed; older copies are removed automatically.
        </p>
      </div>

      <div className="space-y-2 border border-neutral-700 rounded-md p-3">
        <div className="flex flex-wrap items-center gap-2 text-xs text-neutral-300">
          <span>Delete chats not used for</span>
          <select
            aria-label="Not used for"
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            className="text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200"
          >
            {NOW_CHOICES.map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </div>

        {preview && (
          <>
            {preview.chats.length === 0 ? (
              <p className="text-[11px] text-neutral-500">No chat is that old.</p>
            ) : (
              <div className="space-y-1">
                <p className="text-[11px] text-neutral-400">
                  {preview.chats.length} chat{preview.chats.length === 1 ? "" : "s"} would be deleted:
                </p>
                <ul className="text-[11px] text-neutral-500 space-y-0.5">
                  {shown.map((chat) => (
                    <li key={chat.id} className="flex gap-2">
                      <span className="truncate text-neutral-300">{chat.title || "(untitled)"}</span>
                      <span className="shrink-0">last used {formatDate(chat.lastActivityUtc)}</span>
                    </li>
                  ))}
                  {preview.chats.length > shown.length && <li>and {preview.chats.length - shown.length} more</li>}
                </ul>
              </div>
            )}
            {preview.keptForPlans > 0 && (
              <p className="text-[11px] text-neutral-500">
                {preview.keptForPlans} old chat{preview.keptForPlans === 1 ? " is" : "s are"} kept because a plan still runs or
                waits in {preview.keptForPlans === 1 ? "it" : "them"}.
              </p>
            )}
            {preview.chats.length > 0 && (
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  disabled={deleting}
                  onClick={() => (confirming ? deleteNow() : setConfirming(true))}
                  className={`text-xs px-3 py-1.5 rounded-md border disabled:opacity-50 ${
                    confirming
                      ? "bg-red-600/30 text-red-200 border-red-500/60 hover:bg-red-600/40"
                      : "bg-red-600/10 text-red-300 border-red-600/40 hover:bg-red-600/20"
                  }`}
                >
                  {deleting
                    ? "Deleting..."
                    : confirming
                      ? `Yes, delete ${preview.chats.length} chat${preview.chats.length === 1 ? "" : "s"} for good`
                      : `Delete ${preview.chats.length} chat${preview.chats.length === 1 ? "" : "s"}`}
                </button>
                {confirming && !deleting && (
                  <button type="button" onClick={() => setConfirming(false)} className="text-[11px] text-neutral-400 hover:text-neutral-200">
                    Cancel
                  </button>
                )}
              </div>
            )}
          </>
        )}
        {done && <p className="text-[11px] text-emerald-300">{done}</p>}
        <p className="text-[11px] text-neutral-600">
          To keep a copy of a chat first, open it, then use Export JSON in its Context panel.
        </p>
      </div>
    </div>
  );
}
