'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parseCsv, toCsv } = require('../src/csv.js');

test('empty text gives empty array', () => {
  assert.deepEqual(parseCsv(''), []);
});

test('single row with fields', () => {
  assert.deepEqual(parseCsv('a,b,c'), [['a', 'b', 'c']]);
  assert.deepEqual(parseCsv('a,b\n'), [['a', 'b']]);
  assert.deepEqual(parseCsv('a,b\r\n'), [['a', 'b']]);
});

test('multiple rows with newline separators', () => {
  assert.deepEqual(parseCsv('a,b\n1,2\n'), [['a', 'b'], ['1', '2']]);
  assert.deepEqual(parseCsv('a,b\n1,2'), [['a', 'b'], ['1', '2']]);
});

test('blank line gives row with single empty string', () => {
  assert.deepEqual(parseCsv('a\n\nb'), [['a'], [''], ['b']]);
});

test('empty fields and trailing comma', () => {
  assert.deepEqual(parseCsv('a,,c'), [['a', '', 'c']]);
  assert.deepEqual(parseCsv(''), []);
});

test('quoted fields with commas inside', () => {
  assert.deepEqual(parseCsv('a,"b,c"'), [['a', 'b,c']]);
  assert.deepEqual(parseCsv('a,"b,c"\r\n'), [['a', 'b,c']]);
});

test('quoted fields with newlines inside', () => {
  assert.deepEqual(parseCsv('x,"line1\nline2",y'), [['x', 'line1\nline2', 'y']]);
});

test('quoted fields with carriage returns inside', () => {
  assert.deepEqual(parseCsv('x,"line1\r\nline2",y'), [['x', 'line1\r\nline2', 'y']]);
});

test('doubled quotes become single quote', () => {
  assert.deepEqual(parseCsv('"say ""hi"""'), [['say "hi"']]);
  assert.deepEqual(parseCsv('a,"b""c",d'), [['a', 'b"c', 'd']]);
});

test('quoted empty field', () => {
  assert.deepEqual(parseCsv('a,"",c'), [['a', '', 'c']]);
});

test('spaces preserved exactly', () => {
  assert.deepEqual(parseCsv(' a , b '), [[' a ', ' b ']]);
  assert.deepEqual(parseCsv('  ,  '), [['  ', '  ']]);
});

test('unquoted field with double quote is ordinary character', () => {
  assert.deepEqual(parseCsv('5" nail,x'), [['5" nail', 'x']]);
  assert.deepEqual(parseCsv('a,"b""c",5"x'), [['a', 'b"c', '5"x']]);
});

test('unterminated quote throws SyntaxError', () => {
  assert.throws(() => parseCsv('"abc'), (err) => {
    return err instanceof SyntaxError && err.message.startsWith('Unterminated quote');
  });
  assert.throws(() => parseCsv('a,"b,c'), (err) => {
    return err instanceof SyntaxError && err.message.startsWith('Unterminated quote');
  });
  assert.throws(() => parseCsv('"abc\n'), (err) => {
    return err instanceof SyntaxError && err.message.startsWith('Unterminated quote');
  });
});

test('unexpected character after closing quote throws SyntaxError', () => {
  assert.throws(() => parseCsv('"a"b,c'), (err) => {
    return err instanceof SyntaxError && err.message.startsWith('Unexpected character');
  });
  assert.throws(() => parseCsv('"a"x'), (err) => {
    return err instanceof SyntaxError && err.message.startsWith('Unexpected character');
  });
});

test('toCsv converts rows to string', () => {
  assert.equal(toCsv([]), '');
  assert.equal(toCsv([['a', 'b']]), 'a,b');
  assert.equal(toCsv([['a', 'b'], ['1', '2']]), 'a,b\n1,2');
});

test('toCsv quotes fields with comma', () => {
  assert.equal(toCsv([['a', 'b,c']]), 'a,"b,c"');
});

test('toCsv quotes fields with double quote', () => {
  assert.equal(toCsv([['say "hi"', 'x']]), '"say ""hi""",x');
});

test('toCsv quotes fields with newline', () => {
  assert.equal(toCsv([['a', 'b\nc']]), 'a,"b\nc"');
});

test('toCsv quotes fields with carriage return', () => {
  assert.equal(toCsv([['a', 'b\rc']]), 'a,"b\rc"');
});

test('toCsv does not quote simple fields', () => {
  assert.equal(toCsv([['abc', 'def']]), 'abc,def');
});

test('toCsv with mixed quoted and unquoted', () => {
  assert.equal(toCsv([['a', 'b,c'], ['say "hi"', 'x']]), 'a,"b,c"\n"say ""hi""",x');
});

test('roundtrip: parseCsv(toCsv(rows)) equals rows', () => {
  const rows1 = [['a', 'b\nc'], ['', ',']];
  assert.deepEqual(parseCsv(toCsv(rows1)), rows1);

  const rows2 = [['say "hi"', 'x'], ['a', 'b,c']];
  assert.deepEqual(parseCsv(toCsv(rows2)), rows2);
});

test('complex example with crlf', () => {
  assert.deepEqual(parseCsv('a,"b,c"\r\n'), [['a', 'b,c']]);
});

test('field with consecutive doubled quotes', () => {
  assert.deepEqual(parseCsv('"a""b""c"'), [['a"b"c']]);
});

test('empty quoted field at end', () => {
  assert.deepEqual(parseCsv('a,b,""'), [['a', 'b', '']]);
});

test('toCsv preserves empty fields without special chars', () => {
  assert.equal(toCsv([['a', '', 'c']]), 'a,,c');
});
