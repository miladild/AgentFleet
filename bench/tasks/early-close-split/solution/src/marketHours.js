'use strict';

// Days the exchange is closed all day (2026 to 2028).
const HOLIDAYS = new Set([
  '2026-01-01', '2026-01-19', '2026-02-16', '2026-04-03', '2026-05-25', '2026-06-19', '2026-07-03', '2026-09-07', '2026-11-26', '2026-12-25',
  '2027-01-01', '2027-01-18', '2027-02-15', '2027-03-26', '2027-05-31', '2027-06-18', '2027-07-05', '2027-09-06', '2027-11-25', '2027-12-24',
  '2028-01-17', '2028-02-21', '2028-04-14', '2028-05-29', '2028-06-19', '2028-07-04', '2028-09-04', '2028-11-23', '2028-12-25',
]);

const formatter = new Intl.DateTimeFormat('en-US', {
  timeZone: 'America/New_York',
  weekday: 'short',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

// The New York calendar day ("2026-03-10"), weekday ("Tue") and minutes since midnight of an instant.
function newYorkParts(date) {
  const parts = {};
  for (const part of formatter.formatToParts(date)) parts[part.type] = part.value;
  return {
    year: Number(parts.year),
    month: Number(parts.month),
    dayOfMonth: Number(parts.day),
    day: `${parts.year}-${parts.month}-${parts.day}`,
    weekday: parts.weekday,
    minutes: Number(parts.hour) * 60 + Number(parts.minute),
  };
}

function isTradingDay(ny) {
  return ny.weekday !== 'Sat' && ny.weekday !== 'Sun' && !HOLIDAYS.has(ny.day);
}

// The day of the month of the fourth Thursday of November.
function thanksgiving(year) {
  const firstWeekday = new Date(Date.UTC(year, 10, 1)).getUTCDay();
  return 1 + ((4 - firstWeekday + 7) % 7) + 21;
}

function earlyClose(ny) {
  if (!isTradingDay(ny)) return false;
  const mondayToThursday = ['Mon', 'Tue', 'Wed', 'Thu'].includes(ny.weekday);
  if (ny.month === 11 && ny.dayOfMonth === thanksgiving(ny.year) + 1) return true;
  if (ny.month === 7 && ny.dayOfMonth === 3 && mondayToThursday) return true;
  if (ny.month === 12 && ny.dayOfMonth === 24 && mondayToThursday) return true;
  return false;
}

// True when the New York calendar day of the instant closes at 13:00.
function isEarlyClose(date = new Date()) {
  return earlyClose(newYorkParts(date));
}

// 'pre', 'open', 'post' or 'closed' at an instant.
function marketSession(date = new Date()) {
  const ny = newYorkParts(date);
  if (!isTradingDay(ny)) return 'closed';
  const closing = earlyClose(ny) ? 13 * 60 : 16 * 60;
  const m = ny.minutes;
  if (m < 4 * 60) return 'closed';
  if (m < 9 * 60 + 30) return 'pre';
  if (m < closing) return 'open';
  if (m < 20 * 60) return 'post';
  return 'closed';
}

module.exports = { marketSession, isEarlyClose };
