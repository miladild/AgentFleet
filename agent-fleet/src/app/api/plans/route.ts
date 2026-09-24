const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

export async function GET() {
  const upstream = await fetch(`${BACKEND_URL}/api/plans`, { cache: "no-store" });
  return new Response(await upstream.text(), {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}
