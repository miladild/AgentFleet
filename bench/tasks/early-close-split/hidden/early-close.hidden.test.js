'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const hours = require('../src/marketHours.js');

const at = (iso) => new Date(iso);

// New York is UTC-5 in winter and UTC-4 in summer; every instant below is written in UTC with its New York time beside it.

test('regular days keep their hours (summer)', () => {
  assert.equal(hours.marketSession(at('2026-03-10T07:59:00Z')), 'closed'); // 03:59
  assert.equal(hours.marketSession(at('2026-03-10T08:00:00Z')), 'pre'); // 04:00
  assert.equal(hours.marketSession(at('2026-03-10T13:29:00Z')), 'pre'); // 09:29
  assert.equal(hours.marketSession(at('2026-03-10T13:30:00Z')), 'open'); // 09:30
  assert.equal(hours.marketSession(at('2026-03-10T19:59:00Z')), 'open'); // 15:59
  assert.equal(hours.marketSession(at('2026-03-10T20:00:00Z')), 'post'); // 16:00
  assert.equal(hours.marketSession(at('2026-03-10T23:59:00Z')), 'post'); // 19:59
  assert.equal(hours.marketSession(at('2026-03-11T00:00:00Z')), 'closed'); // 20:00
});

test('regular days keep their hours (winter) and the day after the clocks go back', () => {
  assert.equal(hours.marketSession(at('2026-01-12T20:59:00Z')), 'open'); // 15:59
  assert.equal(hours.marketSession(at('2026-01-12T21:00:00Z')), 'post'); // 16:00
  assert.equal(hours.marketSession(at('2026-11-02T14:29:00Z')), 'pre'); // 09:29
  assert.equal(hours.marketSession(at('2026-11-02T14:30:00Z')), 'open'); // 09:30
});

test('the Friday after Thanksgiving closes at 13:00', () => {
  for (const day of ['2026-11-27', '2027-11-26', '2028-11-24']) {
    assert.equal(hours.marketSession(at(`${day}T14:29:00Z`)), 'pre', `${day} 09:29`);
    assert.equal(hours.marketSession(at(`${day}T14:30:00Z`)), 'open', `${day} 09:30`);
    assert.equal(hours.marketSession(at(`${day}T17:59:00Z`)), 'open', `${day} 12:59`);
    assert.equal(hours.marketSession(at(`${day}T18:00:00Z`)), 'post', `${day} 13:00`);
    assert.equal(hours.marketSession(at(`${day}T21:00:00Z`)), 'post', `${day} 16:00`);
    assert.equal(hours.isEarlyClose(at(`${day}T17:00:00Z`)), true, `${day} is an early close`);
  }
  // 20:00 is the end of the after-hours session: the next UTC day.
  assert.equal(hours.marketSession(at('2026-11-28T00:59:00Z')), 'post'); // 19:59
  assert.equal(hours.marketSession(at('2026-11-28T01:00:00Z')), 'closed'); // 20:00
});

test('December 24 closes at 13:00 when it is Monday to Thursday', () => {
  assert.equal(hours.marketSession(at('2026-12-24T17:59:00Z')), 'open'); // Thursday 12:59
  assert.equal(hours.marketSession(at('2026-12-24T18:00:00Z')), 'post'); // 13:00
  assert.equal(hours.isEarlyClose(at('2026-12-24T17:00:00Z')), true);
});

test('July 3 closes at 13:00 when it is Monday to Thursday', () => {
  assert.equal(hours.marketSession(at('2028-07-03T16:59:00Z')), 'open'); // Monday 12:59 (summer time)
  assert.equal(hours.marketSession(at('2028-07-03T17:00:00Z')), 'post'); // 13:00
  assert.equal(hours.marketSession(at('2028-07-03T23:59:00Z')), 'post'); // 19:59
  assert.equal(hours.marketSession(at('2028-07-04T00:00:00Z')), 'closed'); // 20:00
  assert.equal(hours.isEarlyClose(at('2028-07-03T16:00:00Z')), true);
});

test('days that look like early closes but are not', () => {
  // July 2 2026 is a normal Thursday: July 3 2026 is the observed holiday.
  assert.equal(hours.marketSession(at('2026-07-02T19:00:00Z')), 'open'); // 15:00
  assert.equal(hours.isEarlyClose(at('2026-07-02T16:00:00Z')), false);
  // July 2 2027 is a normal Friday.
  assert.equal(hours.marketSession(at('2027-07-02T19:00:00Z')), 'open'); // 15:00
  assert.equal(hours.isEarlyClose(at('2027-07-02T16:00:00Z')), false);
  // The days before the two early closes are ordinary.
  assert.equal(hours.marketSession(at('2026-11-25T19:00:00Z')), 'open'); // Wednesday 14:00
  assert.equal(hours.isEarlyClose(at('2026-11-25T17:00:00Z')), false);
  assert.equal(hours.marketSession(at('2026-12-23T19:00:00Z')), 'open'); // Wednesday 14:00
  assert.equal(hours.isEarlyClose(at('2026-12-23T17:00:00Z')), false);
  assert.equal(hours.marketSession(at('2028-12-22T19:00:00Z')), 'open'); // Friday 14:00
});

test('holidays and weekends are closed and are not early closes', () => {
  assert.equal(hours.marketSession(at('2026-07-03T14:00:00Z')), 'closed'); // Friday, observed holiday
  assert.equal(hours.isEarlyClose(at('2026-07-03T16:00:00Z')), false);
  assert.equal(hours.marketSession(at('2027-12-24T15:00:00Z')), 'closed'); // Friday, observed holiday
  assert.equal(hours.isEarlyClose(at('2027-12-24T17:00:00Z')), false);
  assert.equal(hours.marketSession(at('2026-11-26T15:00:00Z')), 'closed'); // Thanksgiving
  assert.equal(hours.isEarlyClose(at('2026-11-26T17:00:00Z')), false);
  assert.equal(hours.marketSession(at('2028-12-24T16:00:00Z')), 'closed'); // Sunday
  assert.equal(hours.isEarlyClose(at('2028-12-24T17:00:00Z')), false);
  assert.equal(hours.marketSession(at('2027-07-03T16:00:00Z')), 'closed'); // Saturday
  assert.equal(hours.isEarlyClose(at('2027-07-03T16:00:00Z')), false);
});
