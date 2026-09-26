import { backend, isActive, planningTurn, TURN_KINDS, type ContextSummary, type PlanSummary, type RecordEvent } from "../record";

export type LiveCurrent =
  | { kind: "plan"; planId: string; status: string; title: string; contextId: string | null }
  | { kind: "planning"; contextId: string; title: string; ended: boolean }
  | { kind: "none" };

const LIVE = new Set(["running", "approved"]);

async function turnOf(contextId: string) {
  const events = await backend<RecordEvent[]>(`/api/contexts/${contextId}/events?kind=${TURN_KINDS}&limit=300`);
  return events ? planningTurn(events) : null;
}

const asPlan = (plan: PlanSummary): LiveCurrent => ({
  kind: "plan",
  planId: plan.id,
  status: plan.status,
  title: plan.title,
  contextId: plan.contextId ?? null,
});

/**
 * What the live view should show now. For one conversation (?context=): the strongest machine working out a plan in
 * it, or the newest plan it has. For the whole fleet: a plan running, then a plan being worked out anywhere, then a
 * plan waiting for approval, then the newest plan.
 */
export async function GET(request: Request) {
  const context = new URL(request.url).searchParams.get("context");
  const noStore = { headers: { "cache-control": "no-store" } };
  const plans = (await backend<PlanSummary[]>("/api/plans")) ?? [];

  if (context) {
    if (!/^[0-9a-f-]{36}$/i.test(context)) return Response.json({ kind: "none" } satisfies LiveCurrent, noStore);
    const own = plans.filter((plan) => plan.contextId === context);
    const turn = await turnOf(context);
    const newest = own[0];
    // The latest turn planned without proposing anything (yet): it is the news, unless a plan was touched since.
    if (turn?.planning && !turn.planId && (isActive(turn) || !newest || Date.parse(turn.startedAt) > Date.parse(newest.updatedUtc))) {
      const title = (await backend<ContextSummary[]>("/api/contexts"))?.find((summary) => summary.id === context)?.title ?? "planning";
      return Response.json({ kind: "planning", contextId: context, title, ended: !isActive(turn) } satisfies LiveCurrent, noStore);
    }
    const proposed = turn?.planId ? plans.find((plan) => plan.id === turn.planId) : undefined;
    const pick = own.find((plan) => LIVE.has(plan.status)) ?? proposed ?? newest;
    return Response.json(pick ? asPlan(pick) : ({ kind: "none" } satisfies LiveCurrent), noStore);
  }

  const running = plans.find((plan) => LIVE.has(plan.status));
  if (running) return Response.json(asPlan(running), noStore);

  // Conversations that changed in the last fifteen minutes, newest first: is the strongest machine planning in one?
  const contexts = ((await backend<ContextSummary[]>("/api/contexts")) ?? [])
    .filter((summary) => Date.now() - Date.parse(summary.updatedAtUtc) < 15 * 60_000)
    .sort((a, b) => Date.parse(b.updatedAtUtc) - Date.parse(a.updatedAtUtc))
    .slice(0, 3);
  for (const summary of contexts) {
    const turn = await turnOf(summary.id);
    if (turn?.planning && !turn.planId && isActive(turn)) {
      return Response.json({ kind: "planning", contextId: summary.id, title: summary.title, ended: false } satisfies LiveCurrent, noStore);
    }
  }

  const waiting = plans.find((plan) => plan.status === "awaiting-approval");
  const pick = waiting ?? plans[0];
  return Response.json(pick ? asPlan(pick) : ({ kind: "none" } satisfies LiveCurrent), noStore);
}
