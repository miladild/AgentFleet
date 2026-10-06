'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { nextRun } = require('../src/cron.js');

const at = (iso) => new Date(iso);

test('step expression: every 15 minutes', () => {
  assert.deepEqual(nextRun('*/15 * * * *', at('2026-01-01T10:07:30Z')), at('2026-01-01T10:15:00Z'));
  assert.deepEqual(nextRun('*/15 * * * *', at('2026-01-01T10:15:00Z')), at('2026-01-01T10:30:00Z'));
});

test('first of month', () => {
  assert.deepEqual(nextRun('0 0 1 * *', at('2026-01-31T00:00:00Z')), at('2026-02-01T00:00:00Z'));
});

test('weekday restriction', () => {
  assert.deepEqual(nextRun('30 9 * * 1-5', at('2026-01-02T10:00:00Z')), at('2026-01-05T09:30:00Z'));
});

test('leap year handling', () => {
  assert.deepEqual(nextRun('0 0 29 2 *', at('2026-01-01T00:00:00Z')), at('2028-02-29T00:00:00Z'));
});

test('day of month OR day of week', () => {
  assert.deepEqual(nextRun('0 12 13 * 5', at('2026-02-01T00:00:00Z')), at('2026-02-06T12:00:00Z'));
});

test('list of values', () => {
  assert.deepEqual(nextRun('0,30 8-9 * * *', at('2026-03-03T09:30:00Z')), at('2026-03-04T08:00:00Z'));
});

test('range with step', () => {
  assert.deepEqual(nextRun('10-30/10 * * * *', at('2026-03-03T09:00:00Z')), at('2026-03-03T09:10:00Z'));
});

test('year boundary crossing', () => {
  assert.deepEqual(nextRun('59 23 31 12 *', at('2026-12-31T23:59:00Z')), at('2027-12-31T23:59:00Z'));
});

test('sunday matching', () => {
  assert.deepEqual(nextRun('0 0 * * 0', at('2026-01-01T00:00:00Z')), at('2026-01-04T00:00:00Z'));
});

test('strictly after: not same time', () => {
  assert.deepEqual(nextRun('0 0 * * *', at('2026-01-01T00:00:00Z')), at('2026-01-02T00:00:00Z'));
});

test('invalid cron: wrong field count', () => {
  assert.throws(() => nextRun('* * * *', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('invalid cron: value out of range', () => {
  assert.throws(() => nextRun('60 * * * *', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('invalid cron: step of zero', () => {
  assert.throws(() => nextRun('*/0 * * * *', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('invalid cron: bad range', () => {
  assert.throws(() => nextRun('5-1 * * * *', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('invalid cron: day of week 7', () => {
  assert.throws(() => nextRun('* * * * 7', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('invalid cron: empty list item', () => {
  assert.throws(() => nextRun('1,,2 * * * *', at('2026-01-01T00:00:00Z')), (err) => {
    return err instanceof Error && err.message.startsWith('Invalid cron');
  });
});

test('single hour value', () => {
  assert.deepEqual(nextRun('0 5 * * *', at('2026-01-01T04:59:00Z')), at('2026-01-01T05:00:00Z'));
});

test('single minute and hour', () => {
  assert.deepEqual(nextRun('30 14 * * *', at('2026-01-01T14:29:59Z')), at('2026-01-01T14:30:00Z'));
});

test('any day of month and any day of week', () => {
  assert.deepEqual(nextRun('0 0 * * *', at('2026-01-01T00:00:00Z')), at('2026-01-02T00:00:00Z'));
});

test('only day of month restricted, skip weekends', () => {
  assert.deepEqual(nextRun('0 0 15 * *', at('2026-02-01T00:00:00Z')), at('2026-02-15T00:00:00Z'));
});

test('only day of week restricted, skip holidays', () => {
  assert.deepEqual(nextRun('0 0 * * 1', at('2026-01-01T00:00:00Z')), at('2026-01-05T00:00:00Z'));
});

test('multiple hours', () => {
  assert.deepEqual(nextRun('0 5,15 * * *', at('2026-01-01T04:59:00Z')), at('2026-01-01T05:00:00Z'));
  assert.deepEqual(nextRun('0 5,15 * * *', at('2026-01-01T05:00:00Z')), at('2026-01-01T15:00:00Z'));
});

test('range of minutes', () => {
  assert.deepEqual(nextRun('0-30 * * * *', at('2026-01-01T10:45:00Z')), at('2026-01-01T11:00:00Z'));
});

test('UTC arithmetic ensures timezone independence', () => {
  const date = at('2026-06-15T12:00:00Z');
  const result = nextRun('0 0 * * *', date);
  assert.equal(result.getUTCFullYear(), 2026);
  assert.equal(result.getUTCMonth(), 5); // June is month 5
  assert.equal(result.getUTCDate(), 16);
  assert.equal(result.getUTCHours(), 0);
  assert.equal(result.getUTCMinutes(), 0);
  assert.equal(result.getUTCSeconds(), 0);
  assert.equal(result.getUTCMilliseconds(), 0);
});

test('result has seconds and milliseconds zero', () => {
  const result = nextRun('30 9 * * *', at('2026-01-01T08:00:00.500Z'));
  assert.equal(result.getUTCSeconds(), 0);
  assert.equal(result.getUTCMilliseconds(), 0);
});

test('hour range with step', () => {
  assert.deepEqual(nextRun('0 9-17/4 * * *', at('2026-01-01T08:00:00Z')), at('2026-01-01T09:00:00Z'));
  assert.deepEqual(nextRun('0 9-17/4 * * *', at('2026-01-01T09:00:00Z')), at('2026-01-01T13:00:00Z'));
});

test('both day of month and day of week: either matches (OR logic)', () => {
  assert.deepEqual(nextRun('0 12 1 * 5', at('2026-01-01T00:00:00Z')), at('2026-01-01T12:00:00Z'));
});

test('numeric values', () => {
  assert.deepEqual(nextRun('5 10 15 6 3', at('2026-05-01T00:00:00Z')), at('2026-06-03T10:05:00Z'));
});
