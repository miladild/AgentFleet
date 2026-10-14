"use client";

import { useCallback, useEffect, useState } from "react";
import { announceFleetChanged } from "./setupApi";
import {
  lastRunLine,
  measuredLine,
  nextWindowLine,
  regressionLine,
  startHourLabel,
  windowLengthLabel,
  type WeeklyProbeConfig,
  type WeeklyStatus,
} from "./weeklyPresentation";

const DAYS: [string, string][] = [
  ["sunday", "Sunday"],
  ["monday", "Monday"],
  ["tuesday", "Tuesday"],
  ["wednesday", "Wednesday"],
  ["thursday", "Thursday"],
  ["friday", "Friday"],
  ["saturday", "Saturday"],
];

const START_HOURS = Array.from({ length: 24 }, (_, hour) => hour);
const WINDOW_HOURS = Array.from({ length: 12 }, (_, index) => index + 1);

const SELECT_CLASS = "text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200";

/**
 * Whether the worker machines are measured once a week, and in which window. The hub is never measured this way, a
 * measurement starts only when nothing else is running, and it compares speed and fit, not coding skill.
 */
export function WeeklyProbeSettings({
  settings,
  onSaved,
}: {
  settings: WeeklyProbeConfig;
  onSaved: (settings: WeeklyProbeConfig, version: string, message: string) => void;
}) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // What was just chosen, shown while it is being saved; a failed save puts the saved value back.
  const [chosen, setChosen] = useState<WeeklyProbeConfig | null>(null);
  const [status, setStatus] = useState<WeeklyStatus | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const shown = chosen ?? settings;

  const loadStatus = useCallback(async () => {
    try {
      const res = await fetch("/api/lab/weekly", { cache: "no-store" });
      if (!res.ok) throw new Error();
      setStatus(await res.json());
      setStatusError(null);
    } catch {
      setStatusError("Could not load the weekly measurement status from the backend.");
    }
  }, []);

  useEffect(() => {
    void loadStatus();
  }, [loadStatus]);

  async function save(next: WeeklyProbeConfig) {
    setChosen(next);
    setSaving(true);
    setError(null);
    try {
      // The whole block goes, because the backend replaces it: a field left out would come back as its default.
      const res = await fetch("/api/fleet-config", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ weeklyProbe: next }),
      });
      const data = await res.json();
      if (!res.ok) {
        setError(data.reason ?? data.error ?? "Save failed.");
        return;
      }
      const saved: WeeklyProbeConfig = data.weeklyProbe ?? next;
      onSaved(
        saved,
        typeof data.version === "string" ? data.version : "",
        saved.enabled
          ? "Saved. The workers are measured once a week, inside the window."
          : "Saved. The workers are no longer measured on a timer.",
      );
      // The weekly check in the Setup list appears only while the measurement is on, so the list is asked to load again.
      announceFleetChanged();
      void loadStatus();
    } catch {
      setError("Could not reach the backend.");
    } finally {
      setSaving(false);
      setChosen(null);
    }
  }

  const clock = { now: new Date() };
  const nodes = status?.nodes ?? [];

  return (
    <div className="space-y-3">
      <h3 className="text-xs uppercase tracking-wide text-neutral-500">Weekly measurement</h3>
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
            Measure the workers once a week
            <span className="block text-[11px] text-neutral-500 mt-1">
              Workers only, never the hub. The measurement runs inside the window below, one machine at a time, and only when no
              plan or other measurement is running and no model is loaded on that machine. It compares speed and fit, not coding
              skill. A machine whose speed or fit got worse since its last measurement is shown in the status below. Off by default.
            </span>
          </span>
        </label>

        {shown.enabled && (
          <div className="flex flex-wrap items-center gap-3 text-xs text-neutral-300">
            <label htmlFor="weekly-probe-day">Day</label>
            <select
              id="weekly-probe-day"
              value={shown.dayOfWeek}
              disabled={saving}
              onChange={(e) => void save({ ...shown, dayOfWeek: e.target.value })}
              className={SELECT_CLASS}
            >
              {DAYS.map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>

            <label htmlFor="weekly-probe-start">Start hour</label>
            <select
              id="weekly-probe-start"
              value={shown.startHour}
              disabled={saving}
              onChange={(e) => void save({ ...shown, startHour: Number(e.target.value) })}
              className={SELECT_CLASS}
            >
              {START_HOURS.map((hour) => (
                <option key={hour} value={hour}>
                  {startHourLabel(hour)}
                </option>
              ))}
            </select>

            <label htmlFor="weekly-probe-hours">Window length</label>
            <select
              id="weekly-probe-hours"
              value={shown.hours}
              disabled={saving}
              onChange={(e) => void save({ ...shown, hours: Number(e.target.value) })}
              className={SELECT_CLASS}
            >
              {WINDOW_HOURS.map((hours) => (
                <option key={hours} value={hours}>
                  {windowLengthLabel(hours)}
                </option>
              ))}
            </select>
          </div>
        )}

        {saving && <p className="text-[11px] text-neutral-500">Saving...</p>}
        {error && <p className="text-xs text-red-300">{error}</p>}
      </div>

      <div className="bg-neutral-800/50 border border-neutral-800 rounded-md p-3 space-y-2">
        <h4 className="text-xs uppercase tracking-wide text-neutral-500">Status</h4>
        {statusError ? (
          <p className="text-xs text-red-300">{statusError}</p>
        ) : status === null ? (
          <p className="text-xs text-neutral-500">Loading the status...</p>
        ) : (
          <>
            <p className="text-xs text-neutral-300">{nextWindowLine(status, clock)}</p>
            <p className="text-xs text-neutral-300">{lastRunLine(status.lastRunUtc, clock)}</p>
            {nodes.length === 0 ? (
              <p className="text-xs text-neutral-500">There are no worker machines to measure.</p>
            ) : (
              <ul className="space-y-2">
                {nodes.map((node) => (
                  <li key={node.name} className="space-y-0.5">
                    <p className="text-xs text-neutral-200">
                      <span className="font-mono">{node.name}</span> <span className="text-neutral-500">{node.model}</span>
                    </p>
                    <p className="text-[11px] text-neutral-400">{measuredLine(node.lastMeasuredUtc, clock)}</p>
                    {node.regression && <p className="text-[11px] text-amber-200">{regressionLine(node.regression, clock)}</p>}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </div>
    </div>
  );
}
