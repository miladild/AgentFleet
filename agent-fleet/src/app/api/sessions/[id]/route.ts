const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

type RouteParams = { params: Promise<{ id: string }> };

export async function GET(_request: Request, { params }: RouteParams) {
  const { id } = await params;
  const upstream = await fetch(`${BACKEND_URL}/api/sessions/${id}`, { cache: "no-store" });
  const body = await upstream.text();
  return new Response(body, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}

export async function PUT(request: Request, { params }: RouteParams) {
  const { id } = await params;
  const body = await request.text();
  const upstream = await fetch(`${BACKEND_URL}/api/sessions/${id}`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body,
  });
  const responseBody = await upstream.text();
  return new Response(responseBody, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}

export async function DELETE(_request: Request, { params }: RouteParams) {
  const { id } = await params;
  const upstream = await fetch(`${BACKEND_URL}/api/sessions/${id}`, { method: "DELETE" });
  return new Response(null, { status: upstream.status });
}
