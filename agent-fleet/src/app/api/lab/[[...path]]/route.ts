import { labPathAllowed } from "../labPaths";
import { MAX_LAB_BODY_BYTES, readLabBody } from "../labBody";

const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

async function forward(request: Request, path: string[] | undefined) {
  const segments = path ?? [];
  if (!labPathAllowed(segments, request.method)) return new Response("Not found", { status: 404 });
  const joined = segments.join("/");
  const search = new URL(request.url).search;
  try {
    const body = request.method === "GET" ? undefined : await readLabBody(request, MAX_LAB_BODY_BYTES);
    if (body === null) return Response.json({ error: "The request is larger than 8 MB." }, { status: 413 });
    const upstream = await fetch(`${BACKEND_URL}/api/lab${joined ? `/${joined}` : ""}${search}`, {
      method: request.method,
      headers: request.method === "GET" ? undefined : { "content-type": "application/json" },
      body,
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
  return forward(request, (await params).path);
}

export async function DELETE(request: Request, { params }: Context) {
  return forward(request, (await params).path);
}

export async function POST(request: Request, { params }: Context) {
  return forward(request, (await params).path);
}
