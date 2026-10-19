export type PlanRequirement = { id: string; text: string; coveredBy: string[] };

export type RequirementRow = { id: string; text: string; coverage: string };

/** What covers a requirement: the names of the tests that carry its id, or that no test does yet. */
export function requirementCoverage(requirement: PlanRequirement): string {
  return requirement.coveredBy.length === 0
    ? "No test covers it yet"
    : `Covered by ${requirement.coveredBy.join(", ")}`;
}

/** The requirement list as the plan card shows it, in the order the plan gives; no rows when the plan has no list. */
export function requirementRows(requirements: PlanRequirement[] | null | undefined): RequirementRow[] {
  return (requirements ?? []).map((requirement) => ({
    id: requirement.id,
    text: requirement.text,
    coverage: requirementCoverage(requirement),
  }));
}
