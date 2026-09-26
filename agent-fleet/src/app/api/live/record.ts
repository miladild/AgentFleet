// Reading the fleet's durable record for the live view: the events of a plan's steps, and of a conversation's latest
// turn while the strongest machine works out a plan. Shared by the live routes; nothing here reaches the browser as is.

export const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

/** One line of the live view's log, cut down from an event of the durable record. */
export type LiveEvent = {
  id: number;
  at: string;
  step: number | null;
  kind: string;
  node: string | null;
  text: string;
  ok?: boolean;
  /** For a tool call: the tool, and the file, folder, search or command it was given. */
  tool?: string;
  target?: string;
};

export type RecordEvent = {
  id: number;
  taskId: string | null;
  kind: string;
  node: string | null;
  atUtc: string;
  payload: Record<string, unknown> | null;
};

export type ContextSummary = { id: string; title: string; updatedAtUtc: string };

export type PlanSummary = {
  id: string;
  title: string;
  status: string;
  stepsDone: number;
  stepsTotal: number;
  updatedUtc: string;
  contextId?: string | null;
};

export async function backend<T>(path: string): Promise<T | null> {
  try {
    const res = await fetch(`${BACKEND_URL}${path}`, { cache: "no-store" });
    return res.ok ? ((await res.json()) as T) : null;
  } catch {
    return null;
  }
}

export const nodesOf = () =>
  backend<{ nodes?: { name: string; model: string; ready: boolean }[] }>("/api/fleet-status").then((status) => status?.nodes ?? []);

const MAX_TEXT = 220;

export function clip(value: unknown, length = MAX_TEXT): string {
  const text = typeof value === "string" ? value : value == null ? "" : JSON.stringify(value);
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > length ? `${flat.slice(0, length - 1)}…` : flat;
}

// Paths shown relative to the project, so a log line says "src/app.ts" rather than the whole folder.
export function relative(path: string, root: string | null): string {
  if (!root) return path;
  const normalized = path.replace(/\\/g, "/");
  const base = root.replace(/\\/g, "/").replace(/\/$/, "");
  return normalized.toLowerCase().startsWith(`${base.toLowerCase()}/`) ? normalized.slice(base.length + 1) : normalized;
}

const TARGET_KEYS = ["path", "file", "command", "directory", "pattern", "query", "url", "repositoryPath"];

// A tool call as one short line: its name and the argument that says what it touched.
export function describeCall(name: string, rawArguments: unknown, root: string | null): { text: string; target?: string } {
  let args: Record<string, unknown> = {};
  try {
    args = typeof rawArguments === "string" ? JSON.parse(rawArguments) : ((rawArguments as Record<string, unknown>) ?? {});
  } catch {
    // The record keeps only the start of long arguments (a file being written), so the JSON can be cut off: the
    // path or command comes first and is still there.
    const found = String(rawArguments).match(/"(path|file|command|directory|pattern)"\s*:\s*"((?:[^"\\]|\\.)*)"/);
    if (!found) return { text: name };
    const value = found[2].replace(/\\\\/g, "\\").replace(/\\"/g, '"');
    const target = found[1] === "command" ? value : relative(value, root);
    return { text: `${name} ${clip(target, 160)}`, target };
  }
  for (const key of TARGET_KEYS) {
    const value = args[key];
    if (typeof value === "string" && value.length > 0) {
      const target = key === "command" ? value : relative(value, root);
      return { text: `${name} ${clip(target, 160)}`, target };
    }
  }
  if (Array.isArray(args.paths) && typeof args.paths[0] === "string") {
    const target = relative(args.paths[0], root);
    return { text: `${name} ${clip(target, 120)}${args.paths.length > 1 ? ` +${args.paths.length - 1}` : ""}`, target };
  }
  return { text: name };
}

// "Exit code: 0 --- stdout --- ok" reads as "exit 0 · ok".
function toolResultText(result: string): string {
  const exit = result.match(/^Exit code: (-?\d+)\s*(?:--- stdout ---)?\s*/);
  return exit ? `exit ${exit[1]} · ${result.slice(exit[0].length)}` : result;
}

function stepOf(taskId: string | null, planId: string): number | null {
  const match = taskId?.match(/^plan-([0-9a-f]+)-step-(\d+)$/i);
  return match && match[1] === planId ? Number(match[2]) : null;
}

// The record's own lines that the run log already has (checks and plan transitions) are left out, so the log does
// not say everything twice.
function toLine(event: RecordEvent, step: number | null, root: string | null): LiveEvent | null {
  const payload = event.payload ?? {};
  const base = { id: event.id, at: event.atUtc, step, kind: event.kind, node: event.node };
  switch (event.kind) {
    case "route":
      return { ...base, node: (payload.node as string) ?? event.node, text: `model call on ${payload.node ?? "?"} (${payload.tier ?? "?"})` };
    case "tool-call": {
      const name = String(payload.name ?? "tool");
      return { ...base, tool: name, ...describeCall(name, payload.arguments, root) };
    }
    case "tool-result": {
      const result = clip(payload.result, 180);
      return { ...base, text: relative(toolResultText(result), root), ok: !/^error|^blocked/i.test(result) && !/^Exit code: [1-9]/.test(result) };
    }
    case "assistant-output":
      return payload.text ? { ...base, node: (payload.node as string) ?? event.node, text: clip(payload.text) } : null;
    default:
      return null;
  }
}

/** An event of a plan's step, or null when it belongs to something else. */
export function toLive(event: RecordEvent, planId: string, root: string | null): LiveEvent | null {
  const step = stepOf(event.taskId, planId);
  return step === null ? null : toLine(event, step, root);
}

/** An event of the conversation itself (not of a plan's step): the planner's model calls and tool calls. */
export function toTurnLine(event: RecordEvent, root: string | null): LiveEvent | null {
  if (event.kind === "plan-transition" && (event.payload ?? {}).to === "awaiting-approval") {
    return { id: event.id, at: event.atUtc, step: null, kind: event.kind, node: null, text: clip((event.payload ?? {}).note ?? "plan proposed"), ok: true };
  }
  return event.taskId === null ? toLine(event, null, root) : null;
}

/**
 * The folder the planner is working in, so its lines say "src/app.ts": the folder it asked an overview of, or else the
 * deepest folder every full path it read shares.
 */
export function projectRoot(events: RecordEvent[]): string | null {
  const paths: string[] = [];
  for (const event of events) {
    if (event.kind !== "tool-call") continue;
    const payload = event.payload ?? {};
    const { target } = describeCall(String(payload.name ?? ""), payload.arguments, null);
    if (!target || !/^([a-z]:[\\/]|\/)/i.test(target)) continue;
    if (/overview|list_directory|directory_tree/.test(String(payload.name))) return target.replace(/[\\/]+$/, "");
    paths.push(target.replace(/\\/g, "/"));
  }
  if (paths.length < 2) return null;
  let common = paths[0].split("/").slice(0, -1);
  for (const path of paths.slice(1)) {
    const parts = path.split("/");
    let index = 0;
    while (index < common.length && index < parts.length - 1 && common[index].toLowerCase() === parts[index].toLowerCase()) index++;
    common = common.slice(0, index);
  }
  return common.length > 1 ? common.join("/") : null;
}

/** The latest turn of a conversation: what was asked, whether the strongest machine is planning, and how it ended. */
export type PlanningTurn = {
  startId: number;
  startedAt: string;
  lastAt: string;
  surface: string | null;
  request: string | null;
  planning: boolean;
  planId: string | null;
  ended: boolean;
  answer: string | null;
};

// The kinds that say how a turn is going, without the tool results (a read_file result holds the whole file).
export const TURN_KINDS = "run-input,message,message-updated,route,assistant-output,plan-transition";

function messageText(message: unknown): string | null {
  const content = (message as { content?: unknown } | null)?.content;
  const text =
    typeof content === "string"
      ? content
      : Array.isArray(content)
        ? content.map((part) => (typeof part === "object" && part && "text" in part ? String((part as { text: unknown }).text) : "")).join(" ")
        : "";
  // VS Code adds the editor's context below a "---" line; the request is what comes before it.
  const request = text.split(/\n\s*---\s*\n/)[0].trim();
  return request ? clip(request, 400) : null;
}

export function planningTurn(events: RecordEvent[]): PlanningTurn | null {
  let start = -1;
  for (let index = events.length - 1; index >= 0; index--) {
    if (events[index].kind === "run-input") {
      start = index;
      break;
    }
  }
  if (start < 0) return null;

  const turn = events.slice(start);
  const own = turn.filter((event) => event.taskId === null);
  const planning = own.some((event) => event.kind === "route" && (event.payload ?? {}).phase === "Planning");
  const proposed = turn.find((event) => event.kind === "plan-transition" && (event.payload ?? {}).to === "awaiting-approval");
  const lastRoute = own.findLastIndex((event) => event.kind === "route");
  const lastOutput = own.findLastIndex((event) => event.kind === "assistant-output");
  // The final answer is a reply with no tool calls that no further model call follows.
  const final = lastOutput > lastRoute && Number((own[lastOutput].payload ?? {}).toolCalls ?? 0) === 0 ? own[lastOutput] : null;

  let request: string | null = null;
  for (let index = start; index >= 0 && !request; index--) {
    const payload = events[index].payload ?? {};
    if ((events[index].kind === "message" || events[index].kind === "message-updated") && payload.role === "user") {
      request = messageText(payload.message);
    }
  }

  const input = events[start].payload ?? {};
  return {
    startId: events[start].id,
    startedAt: events[start].atUtc,
    lastAt: turn[turn.length - 1].atUtc,
    surface: typeof input.surface === "string" ? input.surface : null,
    request,
    planning,
    planId: proposed ? String((proposed.payload ?? {}).planId ?? "") || null : null,
    ended: final !== null,
    answer: final && !proposed ? clip((final.payload ?? {}).text, 600) : null,
  };
}

/** A turn still being worked on: no final answer yet, and something happened in the last fifteen minutes. */
export function isActive(turn: PlanningTurn): boolean {
  return !turn.ended && Date.now() - Date.parse(turn.lastAt) < 15 * 60_000;
}
