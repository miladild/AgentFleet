export type PlanEvidence = "own-check-only" | "second-opinion-agreed";

const LABELS: Record<PlanEvidence, string> = {
  "own-check-only": "Accepted on its own tests, not independently verified",
  "second-opinion-agreed": "Checked by a second opinion as well",
};

const TONES: Record<PlanEvidence, "amber" | "green"> = {
  "own-check-only": "amber",
  "second-opinion-agreed": "green",
};

const TONE_CLASSES = {
  amber: "border-amber-700/60 bg-amber-950/40 text-amber-200",
  green: "border-emerald-700/60 bg-emerald-950/40 text-emerald-200",
} as const;

export function planEvidenceLabel(evidence: PlanEvidence | null | undefined): string | null {
  return evidence ? LABELS[evidence] ?? null : null;
}

export function planEvidenceTone(evidence: PlanEvidence | null | undefined): "amber" | "green" | null {
  return evidence ? TONES[evidence] ?? null : null;
}

export function planEvidenceClass(evidence: PlanEvidence | null | undefined): string {
  const tone = planEvidenceTone(evidence);
  return tone ? TONE_CLASSES[tone] : "";
}
