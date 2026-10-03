'use strict';

function nextRun(expression, after) {
  const fields = expression.split(' ');
  if (fields.length !== 5) {
    throw new Error('Invalid cron expression: expected 5 fields');
  }

  const ranges = [
    { min: 0, max: 59, name: 'minute' },
    { min: 0, max: 23, name: 'hour' },
    { min: 1, max: 31, name: 'day of month' },
    { min: 1, max: 12, name: 'month' },
    { min: 0, max: 6, name: 'day of week' }
  ];

  const parsed = [];
  for (let i = 0; i < 5; i++) {
    try {
      parsed.push(parseField(fields[i], ranges[i].min, ranges[i].max));
    } catch (e) {
      throw new Error('Invalid cron expression: ' + e.message);
    }
  }

  const [minutes, hours, daysOfMonth, months, daysOfWeek] = parsed;

  // Start from the next minute
  const start = new Date(after.getTime());
  start.setUTCSeconds(0, 0);
  start.setUTCMinutes(start.getUTCMinutes() + 1);

  const maxDate = new Date(start.getTime());
  maxDate.setUTCFullYear(maxDate.getUTCFullYear() + 8);

  let current = new Date(start.getTime());

  while (current < maxDate) {
    const m = current.getUTCMonth() + 1;
    const d = current.getUTCDate();
    const dow = current.getUTCDay();
    const h = current.getUTCHours();
    const min = current.getUTCMinutes();

    // Check if current minute matches
    if (minutes.has(min) && hours.has(h) && months.has(m)) {
      // For day: if both day-of-month and day-of-week are restricted (not *), use OR
      const domRestricted = daysOfMonth.size < 31;
      const dowRestricted = daysOfWeek.size < 7;

      let dayMatches = false;
      if (!domRestricted && !dowRestricted) {
        // Both are *, so any day matches
        dayMatches = true;
      } else if (!domRestricted) {
        // Only day-of-week is restricted
        dayMatches = daysOfWeek.has(dow);
      } else if (!dowRestricted) {
        // Only day-of-month is restricted
        dayMatches = daysOfMonth.has(d);
      } else {
        // Both restricted: use OR
        dayMatches = daysOfMonth.has(d) || daysOfWeek.has(dow);
      }

      if (dayMatches) {
        return new Date(current.getTime());
      }
    }

    // Advance to next minute
    current.setUTCMinutes(current.getUTCMinutes() + 1);
  }

  throw new Error('No matching run found within 8 years');
}

function parseField(field, min, max) {
  if (field === '*') {
    const result = new Set();
    for (let i = min; i <= max; i++) {
      result.add(i);
    }
    return result;
  }

  const result = new Set();
  const parts = field.split(',');

  for (const part of parts) {
    if (part === '') {
      throw new Error('Empty part in field');
    }

    if (part.startsWith('*/')) {
      // Step syntax: */n
      const step = parseInt(part.substring(2));
      if (isNaN(step) || step === 0) {
        throw new Error('Invalid step value');
      }
      for (let i = min; i <= max; i += step) {
        result.add(i);
      }
    } else if (part.includes('/')) {
      // Range with step: a-b/n
      const [range, stepStr] = part.split('/');
      const step = parseInt(stepStr);
      if (isNaN(step) || step === 0) {
        throw new Error('Invalid step value');
      }

      const [startStr, endStr] = range.split('-');
      const rangeStart = parseInt(startStr);
      const rangeEnd = parseInt(endStr);

      if (isNaN(rangeStart) || isNaN(rangeEnd)) {
        throw new Error('Invalid range');
      }
      if (rangeStart > rangeEnd) {
        throw new Error('Invalid range: start > end');
      }
      if (rangeStart < min || rangeEnd > max) {
        throw new Error('Value out of range');
      }

      for (let i = rangeStart; i <= rangeEnd; i += step) {
        result.add(i);
      }
    } else if (part.includes('-')) {
      // Range: a-b
      const [startStr, endStr] = part.split('-');
      const rangeStart = parseInt(startStr);
      const rangeEnd = parseInt(endStr);

      if (isNaN(rangeStart) || isNaN(rangeEnd)) {
        throw new Error('Invalid range');
      }
      if (rangeStart > rangeEnd) {
        throw new Error('Invalid range: start > end');
      }
      if (rangeStart < min || rangeEnd > max) {
        throw new Error('Value out of range');
      }

      for (let i = rangeStart; i <= rangeEnd; i++) {
        result.add(i);
      }
    } else {
      // Single value
      const value = parseInt(part);
      if (isNaN(value)) {
        throw new Error('Invalid value');
      }
      if (value < min || value > max) {
        throw new Error('Value out of range');
      }
      result.add(value);
    }
  }

  return result;
}

module.exports = { nextRun };
