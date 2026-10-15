import { isIP } from "node:net";

// The web UI's copy of the rule in agent/RequestGuard.cs. The backend refuses a request whose Host is not an
// address this machine answers to, and a browser request whose Origin names another site. The web UI needs the
// same rule: its API routes call the backend from the server without the browser's Origin header, so the backend
// cannot tell a page's request from the web UI's own. Keep the two in step.

export type GuardRequest = {
  /** The Host header, or null when the request has none. */
  host: string | null;
  /** The Origin header, or null when there is none (only browsers send one). */
  origin: string | null;
  /** This computer's name, from os.hostname(). */
  machineName: string;
  /** The FLEET_ALLOWED_HOSTS setting: names separated by commas, semicolons or spaces. */
  allowedHosts: string | undefined;
};

export type GuardVerdict = "allowed" | "refused";

export function decideRequest(request: GuardRequest): GuardVerdict {
  const names = parseAllowedNames(request.allowedHosts);
  if (!isAllowedHost(request.host, request.machineName, names)) {
    return "refused";
  }
  if (request.origin !== null && !isAllowedOrigin(request.origin, request.machineName, names)) {
    return "refused";
  }
  return "allowed";
}

// Same separators as the backend. Names are compared without regard to case, so they are kept in lower case.
function parseAllowedNames(setting: string | undefined): string[] {
  return (setting ?? "")
    .split(/[,; ]/)
    .map((name) => name.trim().toLowerCase())
    .filter((name) => name !== "");
}

function isAllowedHost(hostHeader: string | null, machineName: string, names: string[]): boolean {
  const host = hostHeader === null ? "" : hostOnly(hostHeader.trim()).toLowerCase();
  if (host === "") {
    return false;
  }
  if (host === "localhost" || host.endsWith(".localhost")) {
    return true;
  }
  // An IP address cannot be pointed at another address by DNS, so any IP literal is accepted.
  if (isIP(host) !== 0) {
    return true;
  }
  const machine = machineName.toLowerCase();
  if (host === machine || host === `${machine}.local`) {
    return true;
  }
  return names.includes(host);
}

// Only a request that carries an Origin is checked against it. "null" (sent by sandboxed pages) and anything
// that is not a web address are refused.
function isAllowedOrigin(origin: string, machineName: string, names: string[]): boolean {
  let url: URL;
  try {
    url = new URL(origin);
  } catch {
    return false;
  }
  return (url.protocol === "http:" || url.protocol === "https:") && isAllowedHost(url.hostname, machineName, names);
}

// "host", "host:3000", "[::1]" and "[::1]:3000" all reduce to the host alone.
function hostOnly(hostHeader: string): string {
  if (hostHeader.startsWith("[")) {
    const end = hostHeader.indexOf("]");
    return end > 0 ? hostHeader.slice(1, end) : "";
  }
  const colon = hostHeader.lastIndexOf(":");
  return colon >= 0 ? hostHeader.slice(0, colon) : hostHeader;
}
