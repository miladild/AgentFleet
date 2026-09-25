const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// The setup checklist and the fixes it can make: model downloads, starting Ollama, and the sandbox settings.
const ALLOWED = /^(|hub|pull|pulls|pulls\/[0-9a-f]{16}\/cancel|start-ollama|sandbox|sandbox\/(key|test|install-key|prepare))$/;

async function forward(request: Request, path: string[] | undefined, method: "GET" | "POST" | "PUT") {
  const joined = (path ?? []).join("/");
  if (!ALLOWED.test(joined)) return new Response("Not found", { status: 404 });
  const search = new URL(request.url).search;
  try {
    // Nothing here is logged: the key install request carries a password for one SSH sign-in.
    const upstream = await fetch(`${BACKEND_URL}/api/setup${joined ? `/${joined}` : ""}${search}`, {
      method,
      headers: method === "GET" ? undefined : { "content-type": "application/json" },
      body: method === "GET" ? undefined : await request.text(),
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

export async function PUT(request: Request, { params }: Context) {
  return forward(request, (await params).path, "PUT");
}
