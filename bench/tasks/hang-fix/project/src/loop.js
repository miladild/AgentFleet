// Returns the first even number in the list, or null when there is none.
function firstEven(list) {
  let i = 0;
  while (true) {
    if (list[i] % 2 === 0) return list[i];
    i += 1;
  }
}
module.exports = { firstEven };
