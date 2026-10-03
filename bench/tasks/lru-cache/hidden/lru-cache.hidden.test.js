'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { LruCache } = require('../src/lru.js');

test('constructor requires maxSize as positive integer', () => {
  assert.throws(() => new LruCache({}), RangeError);
  assert.throws(() => new LruCache({ maxSize: 0 }), RangeError);
  assert.throws(() => new LruCache({ maxSize: -1 }), RangeError);
  assert.throws(() => new LruCache({ maxSize: 1.5 }), RangeError);
});

test('set and get basic operations', () => {
  const cache = new LruCache({ maxSize: 2 });
  cache.set('a', 'value-a');
  assert.equal(cache.get('a'), 'value-a');
  assert.equal(cache.get('x'), undefined);
});

test('set returns the cache itself for chaining', () => {
  const cache = new LruCache({ maxSize: 2 });
  const result = cache.set('a', 1);
  assert.equal(result, cache);
});

test('evicts least recently used when exceeding maxSize', () => {
  const cache = new LruCache({ maxSize: 2 });
  cache.set('a', 1);
  cache.set('b', 2);
  cache.get('a'); // makes a most recently used
  cache.set('c', 3); // b is least recently used, should be evicted
  assert.deepEqual(cache.keys(), ['a', 'c']);
});

test('has does not change recency', () => {
  const cache = new LruCache({ maxSize: 2 });
  cache.set('a', 1);
  cache.set('b', 2);
  cache.has('a'); // does NOT make a most recently used
  cache.set('c', 3); // a is least recently used, should be evicted
  assert.deepEqual(cache.keys(), ['b', 'c']);
  assert.equal(cache.has('a'), false);
});

test('get changes recency and returns value', () => {
  const cache = new LruCache({ maxSize: 2 });
  cache.set('a', 1);
  cache.set('b', 2);
  const val = cache.get('a'); // makes a most recently used
  assert.equal(val, 1);
  cache.set('c', 3); // b is least recently used
  assert.deepEqual(cache.keys(), ['a', 'c']);
});

test('delete removes entry and returns true, false for missing', () => {
  const cache = new LruCache({ maxSize: 2 });
  cache.set('a', 1);
  assert.equal(cache.delete('a'), true);
  assert.equal(cache.delete('a'), false);
  assert.equal(cache.delete('x'), false);
});

test('keys returns live keys in lru to mru order', () => {
  const cache = new LruCache({ maxSize: 5 });
  cache.set('a', 1);
  cache.set('b', 2);
  cache.set('c', 3);
  assert.deepEqual(cache.keys(), ['a', 'b', 'c']);
});

test('size getter counts live entries', () => {
  const cache = new LruCache({ maxSize: 5 });
  assert.equal(cache.size, 0);
  cache.set('a', 1);
  assert.equal(cache.size, 1);
  cache.set('b', 2);
  assert.equal(cache.size, 2);
  cache.delete('a');
  assert.equal(cache.size, 1);
});

test('TTL: entry expires after ttlMs milliseconds', () => {
  let now = 0;
  const cache = new LruCache({ maxSize: 5, ttlMs: 100, now: () => now });
  cache.set('a', 'value');
  now = 99;
  assert.equal(cache.get('a'), 'value');
  now = 100;
  assert.equal(cache.get('a'), undefined);
  assert.equal(cache.size, 0);
});

test('TTL: get does not restart lifetime', () => {
  let now = 0;
  const cache = new LruCache({ maxSize: 5, ttlMs: 100, now: () => now });
  cache.set('a', 'value');
  now = 90;
  cache.get('a'); // does NOT restart lifetime
  now = 100;
  assert.equal(cache.get('a'), undefined);
});

test('TTL: set restarts lifetime for existing key', () => {
  let now = 0;
  const cache = new LruCache({ maxSize: 5, ttlMs: 100, now: () => now });
  cache.set('a', 'v1');
  now = 50;
  cache.set('a', 'v2'); // restart lifetime at time 50
  now = 120;
  assert.equal(cache.get('a'), 'v2'); // alive, expires at 150
  now = 150;
  assert.equal(cache.get('a'), undefined);
});

test('TTL and LRU combined: expired entries do not prevent eviction', () => {
  let now = 0;
  const cache = new LruCache({ maxSize: 2, ttlMs: 100, now: () => now });
  cache.set('a', 1);
  now = 50;
  cache.set('b', 2);
  now = 101; // a is expired, b is not
  cache.set('c', 3);
  assert.deepEqual(cache.keys().sort(), ['b', 'c']);
});

test('no TTL: entries live indefinitely', () => {
  let now = 0;
  const cache = new LruCache({ maxSize: 5, now: () => now });
  cache.set('a', 'value');
  now = 1000000;
  assert.equal(cache.get('a'), 'value');
});

test('empty cache keys is empty array', () => {
  const cache = new LruCache({ maxSize: 5 });
  assert.deepEqual(cache.keys(), []);
});
