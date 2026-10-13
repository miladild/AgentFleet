export type WorkScorecards = {
  models?: Array<{
    node: string | null;
    model: string;
    stepsWorked: number;
    stepsDoneOnOwn: number;
    rescuedByNode?: Record<string, number>;
    parksByFailureSignature?: Record<string, number>;
  }>;
  infrastructureRoundsByNode?: Record<string, number>;
  skippedButCountedDoneByNode?: Record<string, number>;
  truncatedPlans?: number;
};

const count = (values: Record<string, number> | undefined) =>
  Object.values(values ?? {}).reduce((total, value) => total + Math.max(0, value), 0);

export function formatWorkLine(
  scorecards: WorkScorecards,
  node: string,
  model: string,
  hubNodeName: string | null,
): string {
  const result = scorecards.models?.find((item) => item.node === node && item.model === model);
  if (!result) return "Work last 7 days: no plan record.";

  const stepsWorked = Math.max(0, result.stepsWorked);
  const stepsDone = Math.max(0, result.stepsDoneOnOwn);
  const parked = count(result.parksByFailureSignature);
  const quality = stepsWorked < 5 ? " (too few to judge)" : "";
  const rescued = hubNodeName ? Math.max(0, result.rescuedByNode?.[hubNodeName] ?? 0) : 0;
  const infrastructure = Math.max(0, scorecards.infrastructureRoundsByNode?.[node] ?? 0);
  const skipped = Math.max(0, scorecards.skippedButCountedDoneByNode?.[node] ?? 0);
  const truncated = scorecards.truncatedPlans ? " Some long plans were cut." : "";

  return `Work last 7 days: ${stepsDone} of ${stepsWorked} steps on its own, ${parked} parked${quality}. ` +
    `Rescued by the hub ${rescued} · infrastructure problems ${infrastructure} · skipped ${skipped}.${truncated}`;
}
