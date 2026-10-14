/** The weekly block of the fleet config, as GET and PUT /api/fleet-config carry it. */
export type WeeklyProbeConfig = {
  enabled: boolean;
  dayOfWeek: string;
  startHour: number;
  hours: number;
};

/** A regression still open for a machine: when it began, and the reasons in the words the backend uses. */
export type WeeklyRegression = { since: string; summary: string };

export type WeeklyNodeStatus = {
  name: string;
  model: string;
  lastMeasuredUtc: string | null;
  regression: WeeklyRegression | null;
};

/** What GET /api/lab/weekly returns: the settings, the next window, the last complete run, and each worker. */
export type WeeklyStatus = WeeklyProbeConfig & {
  nextWindowStartUtc: string | null;
  lastRunUtc: string | null;
  nodes: WeeklyNodeStatus[];
};

/** The moment the dates are read against, and the zone they are shown in. Without a zone, the viewer's own. */
export type WeeklyClock = { now: Date; timeZone?: string };

const DAY_MS = 86_400_000;

function instant(value: string | null): Date | null {
  if (!value) return null;
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? null : date;
}

// Days are counted on the calendar of the zone, so a measurement made just after midnight is "today" to the viewer
// even when it is still yesterday in UTC.
function calendarDay(date: Date, timeZone?: string): [number, number, number] {
  const [year, month, day] = date.toLocaleDateString("en-CA", { timeZone }).split("-").map(Number);
  return [year, month, day];
}

function daysBetween(later: Date, earlier: Date, timeZone?: string): number {
  const [yearLater, monthLater, dayLater] = calendarDay(later, timeZone);
  const [yearEarlier, monthEarlier, dayEarlier] = calendarDay(earlier, timeZone);
  return (Date.UTC(yearLater, monthLater - 1, dayLater) - Date.UTC(yearEarlier, monthEarlier - 1, dayEarlier)) / DAY_MS;
}

/** "18 Oct", with the year added when the date falls in another year than the clock. */
function shortDate(date: Date, clock: WeeklyClock): string {
  const sameYear = calendarDay(date, clock.timeZone)[0] === calendarDay(clock.now, clock.timeZone)[0];
  return new Intl.DateTimeFormat("en-NZ", {
    day: "numeric",
    month: "short",
    year: sameYear ? undefined : "numeric",
    timeZone: clock.timeZone,
  }).format(date);
}

/**
 * "today (18 Oct)", "yesterday (17 Oct)" or "3 days ago (15 Oct)". A week or more back the count says little, so the date
 * stands alone.
 */
function whenPhrase(date: Date, clock: WeeklyClock): string {
  const exact = shortDate(date, clock);
  const days = daysBetween(clock.now, date, clock.timeZone);
  if (days >= 7) return exact;
  if (days <= 0) return `today (${exact})`;
  if (days === 1) return `yesterday (${exact})`;
  return `${days} days ago (${exact})`;
}

function timeOfDay(date: Date, timeZone?: string): string {
  return new Intl.DateTimeFormat("en-NZ", { hour: "2-digit", minute: "2-digit", hourCycle: "h23", timeZone }).format(date);
}

/** "02:00", the way the start hour is offered. */
export function startHourLabel(hour: number): string {
  return `${String(hour).padStart(2, "0")}:00`;
}

/** "1 hour" or "4 hours", the way the window length is offered. */
export function windowLengthLabel(hours: number): string {
  return hours === 1 ? "1 hour" : `${hours} hours`;
}

/** The next window in the viewer's time, for example "Next window: Sunday 25 Oct, 02:00 local time, for 4 hours." */
export function nextWindowLine(status: WeeklyStatus, clock: WeeklyClock): string {
  if (!status.enabled) return "The weekly measurement is off, so no window is planned.";
  const start = instant(status.nextWindowStartUtc);
  if (!start) return "No window is planned yet.";
  const weekday = new Intl.DateTimeFormat("en-NZ", { weekday: "long", timeZone: clock.timeZone }).format(start);
  const day = new Intl.DateTimeFormat("en-NZ", { day: "numeric", month: "short", timeZone: clock.timeZone }).format(start);
  return `Next window: ${weekday} ${day}, ${timeOfDay(start, clock.timeZone)} local time, for ${windowLengthLabel(status.hours)}.`;
}

/** When the last complete weekly run finished, or that none has finished yet. */
export function lastRunLine(lastRunUtc: string | null, clock: WeeklyClock): string {
  if (!lastRunUtc) return "No weekly run has finished yet.";
  const date = instant(lastRunUtc);
  return date ? `Last complete run: ${whenPhrase(date, clock)}.` : "Last complete run: date unavailable.";
}

/** When a machine was last measured. A machine that never was says so in those words. */
export function measuredLine(lastMeasuredUtc: string | null, clock: WeeklyClock): string {
  if (!lastMeasuredUtc) return "Never measured.";
  const date = instant(lastMeasuredUtc);
  return date ? `Last measured ${whenPhrase(date, clock)}.` : "Last measured: date unavailable.";
}

/**
 * An open regression with the day it began. The summary is the backend's own wording; a full stop is added when it has
 * none, so the line reads as a sentence whatever the backend sends.
 */
export function regressionLine(regression: WeeklyRegression, clock: WeeklyClock): string {
  const summary = regression.summary.trim();
  const sentence = /[.!?]$/.test(summary) ? summary : `${summary}.`;
  const since = instant(regression.since);
  return since ? `Regression since ${shortDate(since, clock)}: ${sentence}` : `Regression: ${sentence}`;
}
