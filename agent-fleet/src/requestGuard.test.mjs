import assert from "node:assert/strict";
import { test } from "node:test";
import { decideRequest } from "./requestGuard.ts";

// The machine name and the extra names are passed in, so these tests do not depend on the computer running them.
const machineName = "example-pc";
const allowedHosts = "lab.example";

function verdict(host, origin = null, setting = allowedHosts) {
  return decideRequest({ host, origin, machineName, allowedHosts: setting });
}

const allowedHostCases = [
  ["localhost:3000", "localhost on the web UI port"],
  ["LOCALHOST", "localhost in capitals"],
  ["app.localhost:3000", "a name under localhost"],
  ["evil.localhost", "a name under localhost is allowed, as it is in the backend"],
  ["127.0.0.1", "an IPv4 address"],
  ["192.0.2.10:3000", "an IPv4 address with a port"],
  ["[::1]:3000", "an IPv6 address in brackets with a port"],
  ["[::1]", "an IPv6 address in brackets"],
  ["[2001:db8::10]:3000", "another IPv6 address with a port"],
  ["lab.example:3000", "a name from FLEET_ALLOWED_HOSTS"],
  ["LAB.EXAMPLE", "a name from FLEET_ALLOWED_HOSTS in capitals"],
  ["example-pc:3000", "this computer's name"],
  ["EXAMPLE-PC", "this computer's name in capitals"],
  ["example-pc.local:3000", "this computer's name with .local"],
];

const refusedHostCases = [
  [null, "no Host header"],
  ["", "an empty Host header"],
  ["   ", "a Host header of spaces"],
  ["evil.example", "another site"],
  ["evil.example:3000", "another site with a port"],
  ["localhost.evil.example", "a name that starts with localhost"],
  ["example-pc.evil.example", "a name that starts with this computer's name"],
  ["lab.example.evil.example", "a name that starts with a listed name"],
  ["xlab.example", "a name that only ends like a listed name"],
  ["other-pc:3000", "another computer's name"],
  ["[::1", "an unclosed bracket"],
  ["::1", "an IPv6 address without brackets"],
  [":3000", "a port without a name"],
];

const allowedOriginCases = [
  ["http://localhost:3000", "the web UI's own origin"],
  ["https://localhost", "localhost over https"],
  ["HTTP://LOCALHOST:3000", "scheme and host in capitals"],
  ["http://127.0.0.1:3000", "an IPv4 address"],
  ["http://[::1]:3000", "an IPv6 address"],
  ["http://192.0.2.10:3000", "an address on the network"],
  ["http://lab.example:3000", "a name from FLEET_ALLOWED_HOSTS"],
  ["http://example-pc:3000", "this computer's name"],
];

const refusedOriginCases = [
  ["null", "the opaque origin that sandboxed pages send"],
  ["", "an empty Origin header"],
  ["https://evil.example", "another site"],
  ["http://evil.example:3000", "another site on the web UI port"],
  ["http://localhost.evil.example:3000", "a name that starts with localhost"],
  ["http://evil.example/http://localhost:3000", "a local address that appears only in the path"],
  ["http://localhost@evil.example", "a local name that appears only before the @"],
  ["ftp://localhost:3000", "a scheme other than http or https"],
  ["not a web address", "text that is not a URL"],
  ["http://", "a scheme with no host"],
];

for (const [host, why] of allowedHostCases) {
  test(`a Host of ${JSON.stringify(host)} is allowed: ${why}`, () => {
    assert.equal(verdict(host), "allowed");
  });
}

for (const [host, why] of refusedHostCases) {
  test(`a Host of ${JSON.stringify(host)} is refused: ${why}`, () => {
    assert.equal(verdict(host), "refused");
  });
}

for (const [origin, why] of allowedOriginCases) {
  test(`an Origin of ${JSON.stringify(origin)} is allowed with a local Host: ${why}`, () => {
    assert.equal(verdict("localhost:3000", origin), "allowed");
  });
}

for (const [origin, why] of refusedOriginCases) {
  test(`an Origin of ${JSON.stringify(origin)} is refused with a local Host: ${why}`, () => {
    assert.equal(verdict("localhost:3000", origin), "refused");
  });
}

test("a request with an allowed Host and no Origin is allowed", () => {
  assert.equal(verdict("localhost:3000"), "allowed");
});

test("a request is refused when its Origin is allowed but its Host is not", () => {
  assert.equal(verdict("evil.example", "http://localhost:3000"), "refused");
});

test("FLEET_ALLOWED_HOSTS takes names separated by commas, semicolons or spaces", () => {
  const setting = " one.example,two.example; three.example  four.example ";
  for (const name of ["one.example", "two.example", "three.example", "four.example"]) {
    assert.equal(verdict(`${name}:3000`, null, setting), "allowed", name);
  }
  assert.equal(verdict("five.example", null, setting), "refused");
});

test("an unset or blank FLEET_ALLOWED_HOSTS adds no names", () => {
  assert.equal(decideRequest({ host: "lab.example", origin: null, machineName, allowedHosts: undefined }), "refused");
  assert.equal(verdict("lab.example", null, " , ; "), "refused");
});
