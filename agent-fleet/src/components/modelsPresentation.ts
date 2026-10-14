// Words and counts for the Models tab. Nothing here ranks models, orders them by how well they did, or shows speed:
// the table keeps the order the backend sends, and every number is a count of runs.

export type BenchModelGroup = {
  node: string;
  model: string;
  runs: number;
  doneAndCorrect: number;
  falseAccepts: number;
  parked: number;
  timedOut: number;
  errors: number;
  tasks: string[];
  tooFew: boolean;
  lastRunUtc: string;
};

export type BenchImport = {
  importId: string;
  label: string | null;
  importedAtUtc: string;
  rows: number;
};

export type BenchModels = {
  days: number | null;
  groups: BenchModelGroup[];
  imports: BenchImport[];
};

export type BenchImportResult = {
  imported: number;
  duplicates: number;
  skipped: number;
  models: string[];
};

export type ImportBody = {
  resultsJsonl: string;
  run?: string;
  models?: Record<string, string>;
  label?: string;
};

export type GroupRowText = {
  machine: string;
  model: string;
  runs: string;
  doneAndCorrect: string;
  falseAccepts: string;
  parked: string;
  timedOut: string;
  errors: string;
  tasks: string;
  lastRun: string;
  tag: string | null;
};

/** The most a results or run file may weigh. The backend refuses a larger results file. */
export const MAX_IMPORT_FILE_BYTES = 2 * 1024 * 1024;

/** Fewer runs than this and a machine and model cannot be judged. The backend counts the same way. */
export const MINIMUM_RUNS_TO_JUDGE = 5;

export const MAX_LABEL_LENGTH = 80;

export const TOO_FEW_TAG = "too few to judge";

const utcDateTime = new Intl.DateTimeFormat("en-NZ", {
  day: "numeric",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
  timeZone: "UTC",
});

export function pluralize(count: number, word: string): string {
  return `${count} ${word}${count === 1 ? "" : "s"}`;
}

export function formatUtcDateTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "date unavailable" : `${utcDateTime.format(date)} UTC`;
}

// Fewer than five runs is too few to judge even when the flag from the backend says otherwise.
export function isTooFew(group: Pick<BenchModelGroup, "runs" | "tooFew">): boolean {
  return group.tooFew || group.runs < MINIMUM_RUNS_TO_JUDGE;
}

export function groupRowText(group: BenchModelGroup): GroupRowText {
  return {
    machine: group.node,
    model: group.model,
    runs: String(group.runs),
    doneAndCorrect: String(group.doneAndCorrect),
    falseAccepts: String(group.falseAccepts),
    parked: String(group.parked),
    timedOut: String(group.timedOut),
    errors: String(group.errors),
    tasks: group.tasks.join(", "),
    lastRun: formatUtcDateTime(group.lastRunUtc),
    tag: isTooFew(group) ? TOO_FEW_TAG : null,
  };
}

// The sentence printed after an import. Duplicates are runs the fleet already kept; skipped lines could not be read.
export function importSummary(result: BenchImportResult): string {
  const sentences = [`Imported ${pluralize(result.imported, "run")}.`];
  if (result.duplicates > 0) {
    sentences.push(`${pluralize(result.duplicates, "run")} ${result.duplicates === 1 ? "was" : "were"} already stored and not added again.`);
  }
  if (result.skipped > 0) {
    sentences.push(`${pluralize(result.skipped, "line")} could not be read and ${result.skipped === 1 ? "was" : "were"} skipped.`);
  }
  return sentences.join(" ");
}

const LIMIT_TEXT = `${MAX_IMPORT_FILE_BYTES / (1024 * 1024)} MB`;

/** A file over the limit is named and refused here, before anything is sent. */
export function importFileProblem(file: { name: string; size: number }): string | null {
  if (file.size <= MAX_IMPORT_FILE_BYTES) return null;
  return `${file.name} is larger than ${LIMIT_TEXT}, the most one import takes, so it was not sent. Import the results in smaller parts.`;
}

function isText(value: unknown): value is string {
  return typeof value === "string" && value.trim() !== "";
}

// The models column of one results line, or null for a line the backend would skip: not JSON, or without a plan,
// task and status. A skipped line names no machine, so it does not ask for a model either.
function namedMachines(line: string): string | null {
  const trimmed = line.trim();
  if (!trimmed) return null;
  let row: unknown;
  try {
    row = JSON.parse(trimmed);
  } catch {
    return null;
  }
  if (typeof row !== "object" || row === null) return null;
  const { planId, task, status, models } = row as Record<string, unknown>;
  if (!isText(planId) || !isText(task) || !isText(status)) return null;
  return typeof models === "string" ? models : "";
}

/** The machines a results file names, each once and sorted. */
export function nodeNamesFromResults(resultsJsonl: string): string[] {
  const names = new Set<string>();
  for (const line of resultsJsonl.split("\n")) {
    const models = namedMachines(line);
    if (models === null) continue;
    for (const part of models.split(",")) {
      const node = part.trim();
      if (node) names.add(node);
    }
  }
  return [...names].sort();
}

/**
 * The POST body. The run record goes as text, as the bench README sends it. Without one, each machine's model goes
 * along; a machine with no model typed is left out, and its runs then count as model not recorded.
 */
export function importBody(input: {
  resultsJsonl: string;
  runJson: string | null;
  nodes: string[];
  models: Record<string, string>;
  label: string;
}): ImportBody {
  const body: ImportBody = { resultsJsonl: input.resultsJsonl };
  const label = input.label.trim();
  if (label) body.label = label;
  if (input.runJson !== null) {
    body.run = input.runJson;
    return body;
  }
  const models: Record<string, string> = {};
  for (const node of input.nodes) {
    const model = (input.models[node] ?? "").trim();
    if (model) models[node] = model;
  }
  body.models = models;
  return body;
}
