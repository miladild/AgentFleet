const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

export async function GET(request: Request) {
  const url = new URL(request.url).searchParams.get("url") ?? "";
  const upstream = await fetch(`${BACKEND_URL}/api/fleet-config/models?url=${encodeURIComponent(url)}`, {
    cache: "no-store",
  });
  const body = await upstream.text();
  return new Response(body, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}
