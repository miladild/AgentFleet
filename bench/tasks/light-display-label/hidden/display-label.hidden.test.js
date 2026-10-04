'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { displayLabel } = require('../src/displayLabel.js');

test('trims leading and trailing spaces, tabs and line feeds', () => {
  assert.equal(displayLabel('  North  '), 'North');
  assert.equal(displayLabel('\tNorth\n'), 'North');
});

test('preserves case and internal spaces, tabs and line feeds exactly', () => {
  assert.equal(displayLabel('  Case  Label\tName\nNext  '), 'Case  Label\tName\nNext');
});

test('empty and whitespace-only strings remain empty rather than using fallback', () => {
  assert.equal(displayLabel(''), '');
  assert.equal(displayLabel(' \t\n '), '');
  assert.equal(displayLabel('', 'Missing'), '');
});

test('non-string inputs use the default fallback', () => {
  for (const value of [undefined, null, 0, false, {}, []]) {
    assert.equal(displayLabel(value), 'Unnamed item');
  }
});

test('a supplied fallback is returned without trimming', () => {
  assert.equal(displayLabel(null, '  Missing  '), '  Missing  ');
});

test('an undefined fallback uses the default', () => {
  assert.equal(displayLabel(null, undefined), 'Unnamed item');
});
