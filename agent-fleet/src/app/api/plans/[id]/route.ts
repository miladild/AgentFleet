const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

export async function GET(_request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const upstream = await fetch(`${BACKEND_URL}/api/plans/${encodeURIComponent(id)}`, { cache: "no-store" });
  return new Response(await upstream.text(), {
    status: upstream.status,
    headers: { "content-type": "application/json" },
  });
}
