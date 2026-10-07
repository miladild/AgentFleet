function firstEven(list) {
  for (const number of list) {
    if (number % 2 === 0) return number;
  }
  return null;
}

module.exports = { firstEven };
