'use strict';

function parseVersion(version) {
  const match = version.match(/^(\d+)\.(\d+)\.(\d+)(?:-([a-zA-Z0-9.]+))?(?:\+[a-zA-Z0-9.]+)?$/);

  if (!match) {
    throw new TypeError(`Invalid version: ${version}`);
  }

  const major = match[1];
  const minor = match[2];
  const patch = match[3];
  const prerelease = match[4] || null;

  // Check for leading zeros
  if (major !== '0' && major.startsWith('0')) {
    throw new TypeError(`Invalid version: ${version}`);
  }
  if (minor !== '0' && minor.startsWith('0')) {
    throw new TypeError(`Invalid version: ${version}`);
  }
  if (patch !== '0' && patch.startsWith('0')) {
    throw new TypeError(`Invalid version: ${version}`);
  }

  return {
    major: parseInt(major, 10),
    minor: parseInt(minor, 10),
    patch: parseInt(patch, 10),
    prerelease,
  };
}

function comparePrereleases(pre1, pre2) {
  if (pre1 === null && pre2 === null) return 0;
  if (pre1 === null) return 1;
  if (pre2 === null) return -1;

  const ids1 = pre1.split('.');
  const ids2 = pre2.split('.');

  for (let i = 0; i < Math.min(ids1.length, ids2.length); i++) {
    const id1 = ids1[i];
    const id2 = ids2[i];
    const isNum1 = /^\d+$/.test(id1);
    const isNum2 = /^\d+$/.test(id2);

    if (isNum1 && isNum2) {
      const num1 = parseInt(id1, 10);
      const num2 = parseInt(id2, 10);
      if (num1 !== num2) return num1 > num2 ? 1 : -1;
    } else if (isNum1) {
      return -1;
    } else if (isNum2) {
      return 1;
    } else {
      const cmp = id1.localeCompare(id2);
      if (cmp !== 0) return cmp > 0 ? 1 : -1;
    }
  }

  return ids1.length < ids2.length ? -1 : (ids1.length > ids2.length ? 1 : 0);
}

function compare(a, b) {
  const versionA = parseVersion(a);
  const versionB = parseVersion(b);

  if (versionA.major !== versionB.major) {
    return versionA.major > versionB.major ? 1 : -1;
  }
  if (versionA.minor !== versionB.minor) {
    return versionA.minor > versionB.minor ? 1 : -1;
  }
  if (versionA.patch !== versionB.patch) {
    return versionA.patch > versionB.patch ? 1 : -1;
  }

  return comparePrereleases(versionA.prerelease, versionB.prerelease);
}

function sort(versions) {
  // Validate all versions first to ensure they're properly formatted
  for (const v of versions) {
    parseVersion(v);
  }
  const arr = [...versions];
  arr.sort((a, b) => compare(a, b));
  return arr;
}

module.exports = { compare, sort };
