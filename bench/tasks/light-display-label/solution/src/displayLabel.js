'use strict';

// Keep a supplied label, or use the caller's fallback for non-text values.
function displayLabel(value, fallback = 'Unnamed item') {
  return typeof value === 'string' ? value.trim() : fallback;
}

module.exports = { displayLabel };
