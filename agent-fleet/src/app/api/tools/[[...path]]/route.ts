const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// Trying a tool, testing a command tool, what helper programs exist, and importing MCP servers from other apps.
const ALLOWED = /^(prerequisites|run|custom\/test|import)$/;

async function forward(request: Request, path: string[] | undefined, method: "GET" | "POST") {
  const joined = (path ?? []).join("/");
  if (!ALLOWED.test(joined)) return new Response("Not found", { status: 404 });
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/tools/${joined}`, {
      method,
      headers: method === "POST" ? { "content-type": "application/json" } : undefined,
      body: method === "POST" ? await request.text() : undefined,
      cache: "no-store",
    });
    return new Response(await upstream.text(), {
      status: upstream.status,
      headers: { "content-type": upstream.headers.get("content-type") ?? "application/json" },
    });
  } catch {
    return Response.json({ error: "Could not reach the backend. Is it running?" }, { status: 502 });
  }
}

type Context = { params: Promise<{ path?: string[] }> };

export async function GET(request: Request, { params }: Context) {
  return forward(request, (await params).path, "GET");
}

export async function POST(request: Request, { params }: Context) {
  return forward(request, (await params).path, "POST");
}