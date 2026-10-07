'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('notes.txt contains done', () => {
  const notes = fs.readFileSync(path.join(__dirname, '..', 'notes.txt'), 'utf8');
  assert.equal(notes.trim(), 'done');
});
