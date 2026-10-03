'use strict';

class LruCache {
  constructor(options) {
    if (!options || !Number.isInteger(options.maxSize) || options.maxSize <= 0) {
      throw new RangeError('maxSize must be a positive integer');
    }

    this.maxSize = options.maxSize;
    this.ttlMs = options.ttlMs;
    this.now = options.now || (() => Date.now());

    // Map to store key -> { value, setTime }
    this.data = new Map();
    // Array to track access order (lru to mru)
    this.order = [];
  }

  // Check if entry is live without modifying state
  _checkLive(key) {
    if (!this.data.has(key)) {
      return false;
    }

    const entry = this.data.get(key);
    if (this.ttlMs !== undefined) {
      const age = this.now() - entry.setTime;
      if (age >= this.ttlMs) {
        return false;
      }
    }

    return true;
  }

  // Count and clean up live entries
  _countLive() {
    let count = 0;
    const keysToRemove = [];
    for (const key of this.order) {
      if (this._checkLive(key)) {
        count++;
      } else {
        keysToRemove.push(key);
      }
    }
    // Clean up expired entries
    for (const key of keysToRemove) {
      this.data.delete(key);
      const idx = this.order.indexOf(key);
      if (idx !== -1) {
        this.order.splice(idx, 1);
      }
    }
    return count;
  }

  // Remove and return the least recently used entry (expired or not)
  _removeLru() {
    if (this.order.length === 0) return;
    const lruKey = this.order.shift();
    this.data.delete(lruKey);
  }

  set(key, value) {
    // Remove from order if already exists
    const idx = this.order.indexOf(key);
    if (idx !== -1) {
      this.order.splice(idx, 1);
    }

    // Add the entry
    this.data.set(key, {
      value,
      setTime: this.now(),
    });
    this.order.push(key);

    // Clean up expired and evict if necessary to maintain maxSize live entries
    while (true) {
      const liveCount = this._countLive();
      if (liveCount <= this.maxSize) break;
      this._removeLru();
    }

    return this;
  }

  get(key) {
    if (!this._checkLive(key)) {
      // Remove if expired
      if (this.data.has(key)) {
        this.data.delete(key);
        const idx = this.order.indexOf(key);
        if (idx !== -1) {
          this.order.splice(idx, 1);
        }
      }
      return undefined;
    }

    // Move to end (most recently used)
    const idx = this.order.indexOf(key);
    if (idx !== -1) {
      this.order.splice(idx, 1);
      this.order.push(key);
    }

    return this.data.get(key).value;
  }

  has(key) {
    if (!this._checkLive(key)) {
      // Remove if expired
      if (this.data.has(key)) {
        this.data.delete(key);
        const idx = this.order.indexOf(key);
        if (idx !== -1) {
          this.order.splice(idx, 1);
        }
      }
      return false;
    }
    return true;
  }

  delete(key) {
    if (!this.data.has(key)) {
      return false;
    }

    this.data.delete(key);
    const idx = this.order.indexOf(key);
    if (idx !== -1) {
      this.order.splice(idx, 1);
    }

    return true;
  }

  keys() {
    // Return keys in lru to mru order, filtering expired entries
    const result = [];
    const keysToRemove = [];
    for (const key of this.order) {
      if (this._checkLive(key)) {
        result.push(key);
      } else {
        keysToRemove.push(key);
      }
    }
    // Clean up expired entries
    for (const key of keysToRemove) {
      this.data.delete(key);
      const idx = this.order.indexOf(key);
      if (idx !== -1) {
        this.order.splice(idx, 1);
      }
    }
    return result;
  }

  get size() {
    // Count live entries and clean up expired ones
    let count = 0;
    const keysToRemove = [];
    for (const key of this.order) {
      if (this._checkLive(key)) {
        count++;
      } else {
        keysToRemove.push(key);
      }
    }
    // Clean up expired entries
    for (const key of keysToRemove) {
      this.data.delete(key);
      const idx = this.order.indexOf(key);
      if (idx !== -1) {
        this.order.splice(idx, 1);
      }
    }
    return count;
  }
}

module.exports = { LruCache };
