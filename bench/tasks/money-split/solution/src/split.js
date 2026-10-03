'use strict';

function splitAmount(cents, parts) {
  if (!Number.isInteger(cents)) {
    throw new TypeError('cents must be an integer');
  }
  if (!Number.isInteger(parts) || parts <= 0) {
    throw new RangeError('parts must be a positive integer');
  }

  const isNegative = cents < 0;
  const absCents = Math.abs(cents);

  const base = Math.floor(absCents / parts);
  const remainder = absCents % parts;

  const shares = [];
  for (let i = 0; i < parts; i++) {
    shares[i] = base + (i < remainder ? 1 : 0);
  }

  if (isNegative) {
    return shares.map(s => s === 0 ? 0 : -s);
  }
  return shares;
}

function splitByWeights(cents, weights) {
  if (!Array.isArray(weights) || weights.length === 0) {
    throw new RangeError('weights array must not be empty');
  }

  let totalWeight = 0;
  for (let i = 0; i < weights.length; i++) {
    if (!Number.isInteger(weights[i]) || weights[i] < 0) {
      throw new RangeError('all weights must be non-negative integers');
    }
    totalWeight += weights[i];
  }

  if (totalWeight === 0) {
    throw new RangeError('sum of weights must be greater than zero');
  }

  const isNegative = cents < 0;
  const absCents = Math.abs(cents);

  // Calculate base shares (floor of exact proportion)
  const shares = weights.map(w => Math.floor(absCents * w / totalWeight));

  // Calculate fractional remainders and how much is still unassigned
  const remainders = weights.map((w, i) => ({
    index: i,
    remainder: (absCents * w / totalWeight) - shares[i],
  }));

  // Calculate total assigned cents
  let assigned = shares.reduce((a, b) => a + b, 0);
  let unassigned = absCents - assigned;

  // Distribute remaining cents to shares with largest fractional remainders
  // Sort by remainder descending, then by index ascending (for ties)
  remainders.sort((a, b) => {
    if (Math.abs(b.remainder - a.remainder) > 1e-9) {
      return b.remainder - a.remainder;
    }
    return a.index - b.index;
  });

  for (let i = 0; i < unassigned && i < remainders.length; i++) {
    shares[remainders[i].index]++;
  }

  if (isNegative) {
    return shares.map(s => s === 0 ? 0 : -s);
  }
  return shares;
}

module.exports = { splitAmount, splitByWeights };
