import { hostname } from "node:os";
import { NextResponse, type NextRequest } from "next/server";
import { decideRequest } from "@/requestGuard";

// Runs before every page and API route, so a web page cannot reach the backend through the web UI. The rule is
// the backend's; requestGuard.ts says why the web UI needs its own copy.
const machineName = hostname();

export function proxy(request: NextRequest) {
  const verdict = decideRequest({
    host: request.headers.get("host"),
    origin: request.headers.get("origin"),
    machineName,
    allowedHosts: process.env.FLEET_ALLOWED_HOSTS,
  });
  if (verdict === "allowed") {
    return NextResponse.next();
  }
  return new NextResponse(
    "Refused: the fleet web UI only answers requests addressed to localhost, an IP address, or this machine's own name. " +
      "If you reach it by another name, list that name in FLEET_ALLOWED_HOSTS.",
    { status: 403, headers: { "content-type": "text/plain; charset=utf-8" } },
  );
}

// Next's own static files (_next/static, _next/image and the icons) skip the check. They cannot reach the backend,
// and leaving them out keeps the page's assets loading when the page itself is refused.
export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico|icon.svg).*)"],
};
