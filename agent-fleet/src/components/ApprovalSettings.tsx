"use client";

import { useState } from "react";

export type ApprovalConfig = { enabled: boolean; timeoutSeconds: number };

const WAIT_CHOICES: [number, string][] = [
  [60, "1 minute"],
  [120, "2 minutes"],
  [240, "4 minutes"],
];

/**
 * Whether the chat asks before a tool that can change something runs. Plans are never asked, and neither are read-only
 * tools. A call nobody answers is refused when its wait runs out.
 */
export function ApprovalSettings({
  settings,
  onSaved,
}: {
  settings: ApprovalConfig;
  onSaved: (settings: ApprovalConfig, version: string, message: string) => void;
}) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // What was just chosen, shown while it is being saved; a failed save puts the saved value back.
  const [chosen, setChosen] = useState<ApprovalConfig | null>(null);
  const shown = chosen ?? settings;

  // A wait set by hand in the file is shown as it is rather than snapped to a choice.
  const choices = WAIT_CHOICES.some(([seconds]) => seconds === shown.timeoutSeconds)
    ? WAIT_CHOICES
    : [...WAIT_CHOICES, [shown.timeoutSeconds, `${shown.timeoutSeconds} seconds`] as [number, string]].sort((a, b) => a[0] - b[0]);

  async function save(next: ApprovalConfig) {
    setChosen(next);
    setSaving(true);
    setError(null);
    try {
      const res = await fetch("/api/fleet-config", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ chatApprovals: next }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.reason ?? data.error ?? "Save failed.");
        return;
      }
      const saved: ApprovalConfig = data.chatApprovals ?? next;
      onSaved(
        saved,
        typeof data.version === "string" ? data.version : "",
        saved.enabled
          ? "Saved. The chat now asks before it runs a command or changes a file."
          : "Saved. The chat runs its tools without asking.",
      );
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setSaving(false);
      setChosen(null);
    }
  }

  return (
    <div className="space-y-3 border border-neutral-700 rounded-md p-3">
      <label className="flex items-start gap-3 text-sm text-neutral-200">
        <input
          type="checkbox"
          checked={shown.enabled}
          disabled={saving}
          onChange={(e) => void save({ ...shown, enabled: e.target.checked })}
          className="mt-0.5"
        />
        <span>
          Ask before the chat runs a command or changes a file
          <span className="block text-[11px] text-neutral-500 mt-1">
            A tool call that can change something waits for your Approve or Reject, shown as a card on the chat and live pages.
            Tools that only read, and plans you have approved, never wait. A call you do not answer in time is refused and the
            assistant is told so. Chats in VS Code are asked too: keep this page open to answer them. Off by default.
          </span>
        </span>
      </label>

      <div className="flex items-center gap-3 text-xs text-neutral-300">
        <label htmlFor="approval-wait">Wait for an answer for</label>
        <select
          id="approval-wait"
          value={shown.timeoutSeconds}
          disabled={saving}
          onChange={(e) => void save({ ...shown, timeoutSeconds: Number(e.target.value) })}
          className="text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200"
        >
          {choices.map(([seconds, label]) => (
            <option key={seconds} value={seconds}>
              {label}
            </option>
          ))}
        </select>
        {saving && <span className="text-[11px] text-neutral-500">Saving...</span>}
      </div>

      {error && <p className="text-xs text-red-300">{error}</p>}
    </div>
  );
}
