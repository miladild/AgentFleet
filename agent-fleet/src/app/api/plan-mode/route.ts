const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

export async function GET() {
  const upstream = await fetch(`${BACKEND_URL}/api/plan-mode`, { cache: "no-store" });
  const body = await upstream.text();
  return new Response(body, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}

export async function POST(request: Request) {
  const body = await request.text();
  const upstream = await fetch(`${BACKEND_URL}/api/plan-mode`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body,
  });
  const responseBody = await upstream.text();
  return new Response(responseBody, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}
