'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { compare, sort } = require('../src/semver.js');

test('compare returns correct values for major version differences', () => {
  assert.equal(compare('2.0.0', '1.0.0'), 1);
  assert.equal(compare('1.0.0', '2.0.0'), -1);
  assert.equal(compare('1.0.0', '1.0.0'), 0);
});

test('compare returns correct values for minor version differences', () => {
  assert.equal(compare('1.2.0', '1.1.0'), 1);
  assert.equal(compare('1.1.0', '1.2.0'), -1);
});

test('compare returns correct values for patch version differences', () => {
  assert.equal(compare('1.2.10', '1.2.9'), 1);
  assert.equal(compare('1.2.9', '1.2.10'), -1);
});

test('version with prerelease is lower than without', () => {
  assert.equal(compare('1.0.0-alpha', '1.0.0'), -1);
  assert.equal(compare('1.0.0', '1.0.0-alpha'), 1);
});

test('prerelease identifiers compare in order with numeric logic', () => {
  assert.equal(compare('1.0.0-alpha', '1.0.0-alpha.1'), -1);
  assert.equal(compare('1.0.0-alpha.1', '1.0.0-alpha.beta'), -1);
  assert.equal(compare('1.0.0-alpha.beta', '1.0.0-beta'), -1);
  assert.equal(compare('1.0.0-beta', '1.0.0-beta.2'), -1);
  assert.equal(compare('1.0.0-beta.2', '1.0.0-beta.11'), -1);
  assert.equal(compare('1.0.0-beta.11', '1.0.0-rc.1'), -1);
  assert.equal(compare('1.0.0-rc.1', '1.0.0'), -1);
});

test('build metadata is ignored in comparison', () => {
  assert.equal(compare('2.0.0+build.1', '2.0.0+build.2'), 0);
  assert.equal(compare('1.0.0+build', '1.0.0'), 0);
});

test('sort returns new array in ascending order', () => {
  const input = ['1.10.0', '1.2.0', '1.2.0-rc.1'];
  const result = sort(input);
  assert.deepEqual(result, ['1.2.0-rc.1', '1.2.0', '1.10.0']);
  assert.notEqual(result, input);
});

test('sort does not modify original array', () => {
  const input = ['1.10.0', '1.2.0'];
  const copy = [...input];
  sort(input);
  assert.deepEqual(input, copy);
});

test('sort complex prerelease ordering', () => {
  const versions = ['1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-alpha.beta', '1.0.0-beta', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0'];
  const shuffled = ['1.0.0-beta.2', '1.0.0', '1.0.0-alpha.1', '1.0.0-alpha', '1.0.0-rc.1', '1.0.0-beta.11', '1.0.0-beta', '1.0.0-alpha.beta'];
  const result = sort(shuffled);
  assert.deepEqual(result, versions);
});

test('invalid version formats throw TypeError', () => {
  assert.throws(() => compare('1.2', '1.0.0'), TypeError);
  assert.throws(() => compare('v1.2.3', '1.0.0'), TypeError);
  assert.throws(() => compare('1.2.3.4', '1.0.0'), TypeError);
  assert.throws(() => compare('01.2.3', '1.0.0'), TypeError);
  assert.throws(() => compare('', '1.0.0'), TypeError);
});

test('invalid versions throw TypeError in sort', () => {
  assert.throws(() => sort(['1.2.3', '1.2']), TypeError);
  assert.throws(() => sort(['v1.2.3']), TypeError);
});

test('error message starts with "Invalid version"', () => {
  try {
    compare('invalid', '1.0.0');
    assert.fail('should have thrown');
  } catch (e) {
    assert(e.message.startsWith('Invalid version'));
  }
});

test('leading zeros in version numbers are invalid', () => {
  assert.throws(() => compare('01.0.0', '1.0.0'), TypeError);
  assert.throws(() => compare('1.01.0', '1.0.0'), TypeError);
  assert.throws(() => compare('1.0.01', '1.0.0'), TypeError);
});

test('numeric vs non-numeric prerelease comparison', () => {
  assert.equal(compare('1.0.0-1', '1.0.0-a'), -1);
  assert.equal(compare('1.0.0-a', '1.0.0-1'), 1);
});

test('versions with different build metadata compare equal', () => {
  assert.equal(compare('1.0.0+build1', '1.0.0+build2'), 0);
  assert.equal(compare('2.3.4+meta', '2.3.4'), 0);
});
