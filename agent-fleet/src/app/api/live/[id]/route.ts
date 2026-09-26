import { demoPayload } from "./demo";
import { BACKEND_URL, backend, nodesOf, toLive, type LiveEvent, type RecordEvent } from "../record";

export type { LiveEvent } from "../record";

type Plan = {
  contextId?: string | null;
  workingDirectory?: string | null;
  events?: { detail?: string }[] | null;
  [key: string]: unknown;
};

/**
 * One small answer per tick for the live view: the plan, the new events of its durable record cut down to a line
 * each, and which machines are up. The record's events carry whole files (a write_file call holds the file), so they
 * are read here, next to the backend, and never sent to the browser as they are.
 */
export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const after = Number(new URL(request.url).searchParams.get("after") ?? "0") || 0;
  if (id === "demo") return Response.json(demoPayload(after), { headers: { "cache-control": "no-store" } });
  if (!/^[0-9a-f]{32}$/i.test(id)) return Response.json({ error: "Not a plan id." }, { status: 404 });

  let plan: Plan;
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/plans/${id}`, { cache: "no-store" });
    if (!upstream.ok) {
      return Response.json({ error: upstream.status === 404 ? "This plan no longer exists." : "Could not load the plan." }, { status: upstream.status });
    }
    plan = await upstream.json();
  } catch {
    return Response.json({ error: "Could not reach the backend." }, { status: 502 });
  }

  const root = plan.workingDirectory ?? null;
  const [events, nodes] = await Promise.all([
    plan.contextId
      ? backend<RecordEvent[]>(`/api/contexts/${plan.contextId}/events?limit=${after > 0 ? 150 : 500}`).then((list) =>
          (list ?? [])
            .filter((event) => event.id > after)
            .map((event) => toLive(event, id, root))
            .filter((event): event is LiveEvent => event !== null)
            .sort((a, b) => a.id - b.id),
        )
      : Promise.resolve([] as LiveEvent[]),
    nodesOf(),
  ]);

  // The run log's details (a failing check's output) are long; the view shows a few lines of each.
  const runEvents = (plan.events ?? []).map((event) => {
    const detail = event.detail ?? "";
    return { ...event, detail: detail.length > 1500 ? `${detail.slice(0, 1499)}…` : detail };
  });
  return Response.json(
    { plan: { ...plan, events: runEvents }, events, nodes, serverTime: new Date().toISOString() },
    { headers: { "cache-control": "no-store" } },
  );
}
