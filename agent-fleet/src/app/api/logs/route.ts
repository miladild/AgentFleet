const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

export async function GET(request: Request) {
  const lines = new URL(request.url).searchParams.get("lines") ?? "150";
  const upstream = await fetch(`${BACKEND_URL}/api/logs/tail?lines=${encodeURIComponent(lines)}`, {
    cache: "no-store",
  });
  const body = await upstream.text();
  return new Response(body, {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}
