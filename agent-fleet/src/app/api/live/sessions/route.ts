const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

type Summary = { id: string; title: string; status: string; stepsDone: number; stepsTotal: number; updatedUtc: string; contextId?: string | null };

/**
 * Which conversations have a plan, newest plan first: the Sessions panel marks those with a link to the live view.
 * The plan list carries each plan's conversation (contextId) in newer backends; older ones only have it on the
 * plan itself, so the newest plans are read one by one.
 */
export async function GET() {
  try {
    const res = await fetch(`${BACKEND_URL}/api/plans`, { cache: "no-store" });
    if (!res.ok) return Response.json([], { status: 200 });
    const plans = ((await res.json()) as Summary[]).slice(0, 25);
    const withContext = await Promise.all(
      plans.map(async (plan) => {
        if (plan.contextId !== undefined) return plan;
        const detail = await fetch(`${BACKEND_URL}/api/plans/${plan.id}`, { cache: "no-store" })
          .then((r) => (r.ok ? r.json() : null))
          .catch(() => null);
        return { ...plan, contextId: (detail?.contextId as string | null) ?? null };
      }),
    );
    return Response.json(
      withContext
        .filter((plan) => plan.contextId)
        .map((plan) => ({
          contextId: plan.contextId,
          planId: plan.id,
          title: plan.title,
          status: plan.status,
          stepsDone: plan.stepsDone,
          stepsTotal: plan.stepsTotal,
        })),
      { headers: { "cache-control": "no-store" } },
    );
  } catch {
    return Response.json([]);
  }
}
