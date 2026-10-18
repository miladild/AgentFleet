"use client";

import { useState } from "react";
import { APPROVAL_LEVEL_TEXT, type ApprovalLevel } from "./approvalPresentation";

export type ApprovalConfig = { mode: ApprovalLevel; enabled: boolean; timeoutSeconds: number };

const WAIT_CHOICES: [number, string][] = [
  [60, "1 minute"],
  [120, "2 minutes"],
  [240, "4 minutes"],
];

/**
 * The chat approval level and wait. Plans and read-only tools never wait; an unanswered call is refused.
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
        `Saved. ${APPROVAL_LEVEL_TEXT[saved.mode]}`,
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
      <fieldset disabled={saving} className="space-y-2 text-sm text-neutral-200">
        <legend className="mb-2">Chat approvals</legend>
        {(["off", "risky", "all"] as const).map((mode) => (
          <label key={mode} className="flex items-start gap-3">
            <input
              type="radio"
              name="chat-approval-level"
              value={mode}
              checked={shown.mode === mode}
              onChange={() => void save({ ...shown, mode, enabled: mode !== "off" })}
              className="mt-0.5"
            />
            <span>
              {mode === "off" ? "Off" : mode === "risky" ? "Commands and HTTP (default)" : "All changes"}
              <span className="block text-[11px] text-neutral-500 mt-1">{APPROVAL_LEVEL_TEXT[mode]}</span>
            </span>
          </label>
        ))}
        <p className="text-[11px] text-neutral-500">
          Plans never wait. Unanswered calls are refused. Connected servers and command tools made in Config
          require All changes to ask; the default covers only the three built-in command and HTTP tools.
        </p>
      </fieldset>

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
