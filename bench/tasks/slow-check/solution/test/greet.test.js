'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { greet } = require('../src/greet.js');

test('greets a normal name', () => {
  assert.equal(greet('Ana'), 'Hello, Ana!');
});

test('rejects an empty string', () => {
  assert.throws(() => greet(''), TypeError);
});

test('rejects a non-string value', () => {
  assert.throws(() => greet(5), TypeError);
});
