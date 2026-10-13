const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

type Context = { params: Promise<{ name: string }> };

export async function PATCH(request: Request, { params }: Context) {
  const { name } = await params;
  if (!/^[a-z0-9][a-z0-9-]{0,31}$/.test(name)) return new Response("Not found", { status: 404 });
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/fleet-config/nodes/${encodeURIComponent(name)}/context`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: await request.text(),
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
