const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

// Thumbs up and down per machine, for the Machines tab.
export async function GET() {
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/feedback`, { cache: "no-store" });
    return new Response(await upstream.text(), { status: upstream.status, headers: { "content-type": "application/json" } });
  } catch {
    return Response.json([], { status: 502 });
  }
}