'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { firstEven } = require('../src/loop.js');

test('returns the first even number', () => {
  assert.equal(firstEven([1, 3, 4, 6]), 4);
});

test('returns null for an empty list', () => {
  assert.equal(firstEven([]), null);
});

test('returns null when there is no even number', () => {
  assert.equal(firstEven([1, 3, 5]), null);
});

test('zero is even', () => {
  assert.equal(firstEven([1, 0]), 0);
});

test('negative numbers are even', () => {
  assert.equal(firstEven([3, -2, 4]), -2);
});
