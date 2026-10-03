'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { slugify } = require('../src/slugify.js');

test('basic lowercase and punctuation removal', () => {
  assert.equal(slugify('Hello, World!'), 'hello-world');
});

test('accented letters are replaced with ASCII base', () => {
  assert.equal(slugify('Crème'), 'creme');
  assert.equal(slugify('Brûlée'), 'brulee');
  assert.equal(slugify('Café'), 'cafe');
  assert.equal(slugify('Ünïcödé'), 'unicode');
  assert.equal(slugify('Ångström'), 'angstrom');
  assert.equal(slugify('Niño'), 'nino');
  assert.equal(slugify('Français'), 'francais');
});

test('ampersand becomes the word and', () => {
  assert.equal(slugify('Rock & Roll'), 'rock-and-roll');
  assert.equal(slugify('  Crème Brûlée & Café  '), 'creme-brulee-and-cafe');
});

test('multiple non-alphanumeric characters become single hyphen', () => {
  assert.equal(slugify('a---b'), 'a-b');
  assert.equal(slugify('hello...world'), 'hello-world');
  assert.equal(slugify('test___case'), 'test-case');
});

test('leading and trailing hyphens are removed', () => {
  assert.equal(slugify('---hello'), 'hello');
  assert.equal(slugify('world---'), 'world');
  assert.equal(slugify('---hello-world---'), 'hello-world');
});

test('only punctuation returns empty string', () => {
  assert.equal(slugify('!!!'), '');
  assert.equal(slugify('...'), '');
  assert.equal(slugify('---'), '');
});

test('non-string inputs return empty string', () => {
  assert.equal(slugify(null), '');
  assert.equal(slugify(123), '');
  assert.equal(slugify(undefined), '');
  assert.equal(slugify({}), '');
  assert.equal(slugify([]), '');
});

test('maxLength option truncates result', () => {
  assert.equal(slugify('Hello World', { maxLength: 7 }), 'hello-w');
  assert.equal(slugify('Hello World', { maxLength: 6 }), 'hello');
  assert.equal(slugify('Hello World', { maxLength: 5 }), 'hello');
});

test('maxLength removes trailing hyphen if cut', () => {
  assert.equal(slugify('Hello-World-Test', { maxLength: 6 }), 'hello');
  assert.equal(slugify('a-b-c-d', { maxLength: 3 }), 'a-b');
});

test('numbers are preserved', () => {
  assert.equal(slugify('Test123'), 'test123');
  assert.equal(slugify('Test-123-Case'), 'test-123-case');
  assert.equal(slugify('2024'), '2024');
});

test('empty and whitespace strings', () => {
  assert.equal(slugify(''), '');
  assert.equal(slugify('   '), '');
  assert.equal(slugify('  \t\n  '), '');
});

test('complex accented and special characters', () => {
  assert.equal(slugify('Côté & Ça'), 'cote-and-ca');
  assert.equal(slugify('naïve résumé'), 'naive-resume');
});

test('maxLength with no limit default', () => {
  const long = 'supercalifragilisticexpialidocious';
  assert.equal(slugify(long), long);
  assert.equal(slugify(long, {}), long);
});

test('apostrophes and quotes are treated as separators', () => {
  assert.equal(slugify("it's"), 'it-s');
  assert.equal(slugify('say "hello"'), 'say-hello');
});
