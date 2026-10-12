export type PlanEvidence = "own-check-only" | "second-opinion-agreed";

const LABELS: Record<PlanEvidence, string> = {
  "own-check-only": "Accepted on its own tests, not independently verified",
  "second-opinion-agreed": "Checked by a second opinion as well",
};

export function planEvidenceLabel(evidence: PlanEvidence | null | undefined): string | null {
  return evidence ? LABELS[evidence] ?? null : null;
}
