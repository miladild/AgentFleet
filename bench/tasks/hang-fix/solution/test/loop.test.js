'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { firstEven } = require('../src/loop.js');

test('returns the first even number', () => {
  assert.equal(firstEven([1, 4, 6]), 4);
});

test('returns null for empty and all-odd lists', () => {
  assert.equal(firstEven([]), null);
  assert.equal(firstEven([1, 3, 5]), null);
});

test('handles zero and negative even numbers', () => {
  assert.equal(firstEven([1, 0]), 0);
  assert.equal(firstEven([3, -2, 4]), -2);
});
