// When to tell the user that a plan parked a step. Kept free of the vscode module so it can be tested with plain Node
// (see test/planNotices.test.js).

/** What the backend's plan list says about a plan. */
export interface PlanProgress {
  title: string;
  stepsDone: number;
  stepsTotal: number;
  /** Steps that are stuck and set aside while the rest of the plan goes on. */
  stepsParked?: number;
}

export interface ParkedNotice {
  /** How many parked steps the user has now been told about. Keep it, and give it back with the next poll. */
  announced: number;
  /** The one message to show, or undefined when there is nothing new. */
  text?: string;
}

/**
 * One message per transition: a message only when more steps are parked than the user was last told about. A step the
 * user retries (the count goes down) is forgotten, so parking again is news again; polling the same count never repeats.
 */
export function parkedNotice(announced: number, plan: PlanProgress): ParkedNotice {
  const parked = Math.max(0, plan.stepsParked ?? 0);
  if (parked <= announced) {
    return { announced: parked };
  }

  const text =
    `Agent Fleet parked ${parked === 1 ? "a step" : `${parked} steps`} of "${plan.title}" ` +
    `(${plan.stepsDone}/${plan.stepsTotal} steps done). The steps that do not depend on ${parked === 1 ? "it" : "them"} go on; ` +
    "open the report to retry or skip.";
  return { announced: parked, text };
}
