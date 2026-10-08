'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { splitAmount, splitByWeights } = require('../src/split.js');

// The text says every share of a negative amount is negated, which does not say whether a zero share is 0 or -0. Strict
// deepEqual tells them apart, so the shares are normalised (x + 0 turns -0 into 0) and only the amounts are compared.
const shares = (list) => list.map((x) => x + 0);

test('splitAmount: positive amounts distribute evenly with first shares getting remainder', () => {
  assert.deepEqual(splitAmount(100, 3), [34, 33, 33]);
  assert.deepEqual(splitAmount(1, 3), [1, 0, 0]);
  assert.deepEqual(splitAmount(0, 4), [0, 0, 0, 0]);
  assert.deepEqual(splitAmount(10, 5), [2, 2, 2, 2, 2]);
});

test('splitAmount: negative amounts are split on absolute value and negated', () => {
  assert.deepEqual(splitAmount(-100, 3), [-34, -33, -33]);
  assert.deepEqual(shares(splitAmount(-1, 3)), [-1, 0, 0]);
  assert.deepEqual(splitAmount(-7, 2), [-4, -3]);
});

test('splitAmount: parts must be a positive integer', () => {
  assert.throws(() => splitAmount(100, 0), RangeError);
  assert.throws(() => splitAmount(100, -1), RangeError);
  assert.throws(() => splitAmount(100, 1.5), RangeError);
  assert.throws(() => splitAmount(100, NaN), RangeError);
});

test('splitAmount: cents must be an integer', () => {
  assert.equal(typeof splitAmount, 'function');
  assert.deepEqual(splitAmount(100, 3), [34, 33, 33]);
  assert.throws(() => splitAmount(1.5, 2), TypeError);
  assert.throws(() => splitAmount(10.1, 3), TypeError);
  assert.throws(() => splitAmount(NaN, 2), TypeError);
});

test('splitAmount: shares sum to original amount', () => {
  const sum = (arr) => arr.reduce((a, b) => a + b, 0);
  assert.equal(sum(splitAmount(100, 3)), 100);
  assert.equal(sum(splitAmount(1, 3)), 1);
  assert.equal(sum(splitAmount(-100, 3)), -100);
  assert.equal(sum(splitAmount(17, 7)), 17);
});

test('splitByWeights: distributes proportionally with largest-remainder method', () => {
  assert.deepEqual(splitByWeights(100, [1, 1, 1]), [34, 33, 33]);
  assert.deepEqual(splitByWeights(10, [1, 2, 3]), [2, 3, 5]);
  assert.deepEqual(splitByWeights(5, [1, 1]), [3, 2]);
});

test('splitByWeights: handles zero weights correctly', () => {
  assert.deepEqual(splitByWeights(100, [0, 1, 1]), [0, 50, 50]);
  assert.deepEqual(splitByWeights(7, [1, 0, 0]), [7, 0, 0]);
  assert.deepEqual(splitByWeights(10, [0, 0, 1]), [0, 0, 10]);
});

test('splitByWeights: empty or invalid weight arrays throw RangeError', () => {
  assert.throws(() => splitByWeights(100, []), RangeError);
  assert.throws(() => splitByWeights(100, [-1, 2]), RangeError);
  assert.throws(() => splitByWeights(100, [1.5, 2]), RangeError);
  assert.throws(() => splitByWeights(100, [0, 0]), RangeError);
});

test('splitByWeights: negative amounts are split on absolute value and negated', () => {
  assert.deepEqual(splitByWeights(-10, [1, 2, 3]), [-2, -3, -5]);
  assert.deepEqual(splitByWeights(-100, [1, 1, 1]), [-34, -33, -33]);
});

test('splitByWeights: shares sum to original amount', () => {
  const sum = (arr) => arr.reduce((a, b) => a + b, 0);
  assert.equal(sum(splitByWeights(100, [1, 1, 1])), 100);
  assert.equal(sum(splitByWeights(10, [1, 2, 3])), 10);
  assert.equal(sum(splitByWeights(-10, [1, 2, 3])), -10);
  assert.equal(sum(splitByWeights(17, [2, 3, 5])), 17);
});

test('splitByWeights: ties in remainders go to earlier indices', () => {
  assert.deepEqual(splitByWeights(5, [1, 1]), [3, 2]);
  assert.deepEqual(splitByWeights(7, [1, 1, 1]), [3, 2, 2]);
  assert.deepEqual(splitByWeights(10, [1, 1, 1, 1]), [3, 3, 2, 2]);
});

test('splitByWeights: complex weight distributions', () => {
  const res = splitByWeights(100, [1, 2, 3, 4]);
  const sum = (arr) => arr.reduce((a, b) => a + b, 0);
  assert.equal(sum(res), 100);
  assert.equal(res[0], 10);
  assert.equal(res[1], 20);
  assert.equal(res[2], 30);
  assert.equal(res[3], 40);
});
