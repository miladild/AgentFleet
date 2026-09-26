import { backend, nodesOf, planningTurn, projectRoot, toTurnLine, TURN_KINDS, type ContextSummary, type LiveEvent, type RecordEvent } from "../../record";

/**
 * The strongest machine working out a plan in one conversation, for the live view: what was asked, the files and
 * searches it went through, what it said, and whether a plan came of it. Only the latest turn, cut down to a line per
 * event; the tool results (whole files) stay here.
 */
export async function GET(request: Request, { params }: { params: Promise<{ contextId: string }> }) {
  const { contextId } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(contextId)) return Response.json({ error: "Not a conversation id." }, { status: 404 });
  const after = Number(new URL(request.url).searchParams.get("after") ?? "0") || 0;

  const [marks, contexts, nodes] = await Promise.all([
    backend<RecordEvent[]>(`/api/contexts/${contextId}/events?kind=${TURN_KINDS}&limit=300`),
    backend<ContextSummary[]>("/api/contexts"),
    nodesOf(),
  ]);
  if (!marks) return Response.json({ error: "This conversation no longer exists." }, { status: 404 });
  const turn = planningTurn(marks);

  // The turn's own events, tool calls and results included, since the last tick.
  let events: LiveEvent[] = [];
  if (turn) {
    const recent = (await backend<RecordEvent[]>(`/api/contexts/${contextId}/events?limit=${after > 0 ? 150 : 500}`)) ?? [];
    const calls = await backend<RecordEvent[]>(`/api/contexts/${contextId}/events?kind=tool-call&limit=60`);
    const root = projectRoot((calls ?? []).filter((event) => event.id >= turn.startId && event.taskId === null));
    events = recent
      .filter((event) => event.id >= turn.startId && event.id > after)
      .map((event) => toTurnLine(event, root))
      .filter((event): event is LiveEvent => event !== null)
      .sort((a, b) => a.id - b.id);
  }

  return Response.json(
    {
      context: { id: contextId, title: contexts?.find((summary) => summary.id === contextId)?.title ?? "conversation" },
      turn,
      events,
      nodes,
      serverTime: new Date().toISOString(),
    },
    { headers: { "cache-control": "no-store" } },
  );
}
