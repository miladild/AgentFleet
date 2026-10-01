const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// approve, reject, stop, skip, and machine selection are POSTs; only these actions are forwarded.
const POST_ACTIONS = new Set(["approve", "reject", "stop", "skip", "machine"]);

export async function POST(request: Request, { params }: { params: Promise<{ id: string; action: string }> }) {
  const { id, action } = await params;
  if (!POST_ACTIONS.has(action)) return new Response("Not found", { status: 404 });
  const query = new URL(request.url).search;
  const body = action === "machine" || action === "approve" ? await request.text() : undefined;
  const upstream = await fetch(`${BACKEND_URL}/api/plans/${encodeURIComponent(id)}/${action}${query}`, {
    method: "POST",
    ...(body === undefined ? {} : { headers: { "content-type": request.headers.get("content-type") ?? "application/json" }, body }),
  });
  return new Response(await upstream.text(), {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}

// markdown is the plan itself; report is what happened while it ran.
const GET_ACTIONS = new Set(["markdown", "report"]);

export async function GET(_request: Request, { params }: { params: Promise<{ id: string; action: string }> }) {
  const { id, action } = await params;
  if (!GET_ACTIONS.has(action)) return new Response("Not found", { status: 404 });
  const upstream = await fetch(`${BACKEND_URL}/api/plans/${encodeURIComponent(id)}/${action}`, { cache: "no-store" });
  return new Response(await upstream.text(), {
    status: upstream.status,
    headers: { "content-type": "text/markdown; charset=utf-8" },
  });
}
