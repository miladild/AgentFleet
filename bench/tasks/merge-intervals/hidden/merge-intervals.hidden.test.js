'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { mergeIntervals, totalCoverage } = require('../src/intervals.js');

test('mergeIntervals returns new array', () => {
  const input = [[1, 3]];
  const result = mergeIntervals(input);
  assert.notEqual(result, input);
});

test('mergeIntervals does not modify input', () => {
  const input = [[3, 4], [1, 2]];
  const copy = [[[3, 4], [1, 2]]];
  mergeIntervals(input);
  assert.deepEqual(input, [[3, 4], [1, 2]]);
});

test('empty array returns empty array', () => {
  assert.deepEqual(mergeIntervals([]), []);
});

test('single interval returns same interval', () => {
  assert.deepEqual(mergeIntervals([[1, 3]]), [[1, 3]]);
});

test('non-overlapping intervals sorted', () => {
  assert.deepEqual(mergeIntervals([[5, 6], [1, 2]]), [[1, 2], [5, 6]]);
});

test('overlapping intervals merge', () => {
  assert.deepEqual(mergeIntervals([[1, 3], [2, 6]]), [[1, 6]]);
});

test('touching intervals merge', () => {
  assert.deepEqual(mergeIntervals([[1, 4], [4, 5]]), [[1, 5]]);
});

test('nested intervals are absorbed', () => {
  assert.deepEqual(mergeIntervals([[1, 10], [2, 3]]), [[1, 10]]);
});

test('complex example with multiple merges', () => {
  assert.deepEqual(mergeIntervals([[1, 3], [2, 6], [8, 10], [15, 18]]), [[1, 6], [8, 10], [15, 18]]);
});

test('unsorted input is handled', () => {
  const input = [[8, 10], [1, 3], [15, 18], [2, 6]];
  assert.deepEqual(mergeIntervals(input), [[1, 6], [8, 10], [15, 18]]);
});

test('start greater than end throws RangeError', () => {
  assert.throws(() => mergeIntervals([[2, 1]]), RangeError);
});

test('totalCoverage with single interval', () => {
  assert.equal(totalCoverage([[1, 3]]), 2);
});

test('totalCoverage with empty array', () => {
  assert.equal(totalCoverage([]), 0);
});

test('totalCoverage sums merged intervals', () => {
  assert.equal(totalCoverage([[1, 3], [2, 6], [8, 10]]), 7);
});

test('totalCoverage with multiple non-overlapping intervals', () => {
  assert.equal(totalCoverage([[1, 2], [5, 6], [15, 18]]), 5);
});

test('totalCoverage handles touching intervals', () => {
  assert.equal(totalCoverage([[1, 3], [3, 5]]), 4);
});

test('totalCoverage with nested intervals', () => {
  assert.equal(totalCoverage([[1, 10], [2, 3]]), 9);
});

test('intervals with same start and end', () => {
  assert.deepEqual(mergeIntervals([[1, 1], [1, 1]]), [[1, 1]]);
  assert.equal(totalCoverage([[1, 1]]), 0);
});

test('negative numbers in intervals', () => {
  assert.deepEqual(mergeIntervals([[-5, -2], [-3, 1]]), [[-5, 1]]);
  assert.equal(totalCoverage([[-5, -2], [-3, 1]]), 6);
});

test('floating point intervals', () => {
  assert.deepEqual(mergeIntervals([[1.5, 3.5], [2.0, 4.0]]), [[1.5, 4.0]]);
  assert.equal(totalCoverage([[1.5, 3.5], [2.0, 4.0]]), 2.5);
});

test('many intervals with overlaps', () => {
  const intervals = [[1, 2], [3, 4], [2, 3], [4, 5], [0, 6]];
  const merged = mergeIntervals(intervals);
  assert.deepEqual(merged, [[0, 6]]);
});
