'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { greet } = require('../src/greet.js');

test('returns the exact greeting', () => {
  assert.equal(greet('Ana'), 'Hello, Ana!');
});

test('throws TypeError for an empty string', () => {
  assert.throws(() => greet(''), TypeError);
});

test('throws TypeError for a non-string value', () => {
  assert.throws(() => greet(5), TypeError);
});
