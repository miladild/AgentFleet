/**
 * The backend paths under /api/lab that the web UI may reach through its own proxy route. Anything else is refused there,
 * so the browser cannot use the proxy to reach the rest of the backend. src/components/labProxyPaths.test.mjs reads every
 * /api/lab path the UI calls and checks it is allowed here, so a call added to the UI cannot be forgotten.
 */
const HEX = "[0-9a-f]{32}";
const GET = new RegExp(`^(fit|fit/latest|scorecards|weekly|models)$`);
const POST = new RegExp(`^(fit|fit/${HEX}/cancel|results/import)$`);
const DELETE = new RegExp(`^results/${HEX}$`);

/** Whether the proxy passes this request on: the path under /api/lab as its segments, and the HTTP method. */
export function labPathAllowed(segments: string[], method: string): boolean {
  const joined = segments.join("/");
  switch (method) {
    case "GET":
      return GET.test(joined);
    case "POST":
      return POST.test(joined);
    case "DELETE":
      return DELETE.test(joined);
    default:
      return false;
  }
}
