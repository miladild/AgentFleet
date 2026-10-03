'use strict';

function mergeIntervals(intervals) {
  if (intervals.length === 0) {
    return [];
  }

  // Check for invalid intervals
  for (const [start, end] of intervals) {
    if (start > end) {
      throw new RangeError('Invalid interval: start > end');
    }
  }

  // Sort by start, then by end
  const sorted = [...intervals].sort((a, b) => {
    if (a[0] !== b[0]) return a[0] - b[0];
    return a[1] - b[1];
  });

  const merged = [sorted[0]];

  for (let i = 1; i < sorted.length; i++) {
    const current = sorted[i];
    const last = merged[merged.length - 1];

    // If current overlaps or touches the last merged interval, merge them
    if (current[0] <= last[1]) {
      last[1] = Math.max(last[1], current[1]);
    } else {
      merged.push(current);
    }
  }

  return merged;
}

function totalCoverage(intervals) {
  if (intervals.length === 0) {
    return 0;
  }

  const merged = mergeIntervals(intervals);
  return merged.reduce((sum, [start, end]) => sum + (end - start), 0);
}

module.exports = { mergeIntervals, totalCoverage };
