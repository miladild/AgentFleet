'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { greet } = require('../src/greet.js');

test('returns the exact greeting', () => {
  assert.equal(typeof greet, 'function');
  assert.equal(greet('Ana'), 'Hello, Ana!');
});

test('throws TypeError for an empty string', () => {
  assert.equal(typeof greet, 'function');
  assert.throws(() => greet(''), TypeError);
});

test('throws TypeError for a non-string value', () => {
  assert.equal(typeof greet, 'function');
  assert.throws(() => greet(5), TypeError);
});
