const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// Only the durable-record API is forwarded: the list, one context, the few actions on it, and the
// storage view and cleanup of the History settings.
const ALLOWED = /^(|storage|cleanup|[0-9a-f-]{36}(\/(events|deliveries|artifacts|decisions|compact|export|events\/\d+\/pin))?)$/;

async function forward(request: Request, path: string[] | undefined, method: "GET" | "POST") {
  const joined = (path ?? []).join("/");
  if (!ALLOWED.test(joined)) return new Response("Not found", { status: 404 });
  const search = new URL(request.url).search;
  const upstream = await fetch(`${BACKEND_URL}/api/contexts${joined ? `/${joined}` : ""}${search}`, {
    method,
    headers: method === "POST" ? { "content-type": "application/json" } : undefined,
    body: method === "POST" ? await request.text() : undefined,
    cache: "no-store",
  });
  return new Response(await upstream.text(), {
    status: upstream.status,
    headers: { "content-type": upstream.headers.get("content-type") ?? "application/json" },
  });
}

export async function GET(request: Request, { params }: { params: Promise<{ path?: string[] }> }) {
  return forward(request, (await params).path, "GET");
}

export async function POST(request: Request, { params }: { params: Promise<{ path?: string[] }> }) {
  return forward(request, (await params).path, "POST");
}
