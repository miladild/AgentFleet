'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { requestOptions } = require('../src/requestOptions.js');

test('no argument and missing fields use the existing defaults', () => {
  const defaults = { page: 1, pageSize: 25, sort: 'name' };
  assert.deepEqual(requestOptions(), defaults);
  assert.deepEqual(requestOptions({}), defaults);
  assert.deepEqual(requestOptions({ page: undefined, pageSize: undefined, sort: undefined }), defaults);
});

test('an explicit zero page is preserved', () => {
  assert.deepEqual(requestOptions({ page: 0 }), { page: 0, pageSize: 25, sort: 'name' });
});

test('an explicit zero page size is preserved', () => {
  assert.deepEqual(requestOptions({ pageSize: 0 }), { page: 1, pageSize: 0, sort: 'name' });
});

test('both pagination values may be zero', () => {
  assert.deepEqual(requestOptions({ page: 0, pageSize: 0 }), { page: 0, pageSize: 0, sort: 'name' });
});

test('positive pagination values and a non-empty sort string are preserved', () => {
  assert.deepEqual(requestOptions({ page: 2, pageSize: 50, sort: 'price' }), { page: 2, pageSize: 50, sort: 'price' });
});

test('an empty sort string still uses the existing sort default', () => {
  assert.deepEqual(requestOptions({ sort: '' }), { page: 1, pageSize: 25, sort: 'name' });
});

test('returns a new object with the existing shape and does not modify the input', () => {
  const input = { page: 0, pageSize: 50, sort: 'price' };
  const before = { ...input };
  const result = requestOptions(input);
  assert.notEqual(result, input);
  assert.deepEqual(result, { page: 0, pageSize: 50, sort: 'price' });
  assert.deepEqual(input, before);
});
