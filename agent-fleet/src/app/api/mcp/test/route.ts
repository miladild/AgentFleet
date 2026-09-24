const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// Starts an MCP server once on the backend and lists its tools, so an entry can be checked before saving.
export async function POST(request: Request) {
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/mcp/test`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: await request.text(),
      cache: "no-store",
    });
    return new Response(await upstream.text(), {
      status: upstream.status,
      headers: { "content-type": "application/json" },
    });
  } catch {
    return Response.json({ ok: false, tools: [], error: "Could not reach the backend." }, { status: 502 });
  }
}
