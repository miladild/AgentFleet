import { demoPayload } from "./demo";

const BACKEND_URL = (process.env.AGENT_URL || "http://localhost:8000/").replace(/\/$/, "");

/** One line of the live view's log, cut down from an event of the plan's durable record. */
export type LiveEvent = {
  id: number;
  at: string;
  step: number | null;
  kind: string;
  node: string | null;
  text: string;
  ok?: boolean;
};

type RecordEvent = {
  id: number;
  taskId: string | null;
  kind: string;
  node: string | null;
  atUtc: string;
  payload: Record<string, unknown> | null;
};

const MAX_TEXT = 220;

function clip(value: unknown, length = MAX_TEXT): string {
  const text = typeof value === "string" ? value : value == null ? "" : JSON.stringify(value);
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > length ? `${flat.slice(0, length - 1)}…` : flat;
}

// Paths shown relative to the project, so a log line says "src/app.ts" rather than the whole folder.
function relative(path: string, root: string | null): string {
  if (!root) return path;
  const normalized = path.replace(/\\/g, "/");
  const base = root.replace(/\\/g, "/").replace(/\/$/, "");
  return normalized.toLowerCase().startsWith(`${base.toLowerCase()}/`) ? normalized.slice(base.length + 1) : normalized;
}

// A tool call as one short line: its name and the argument that says what it touched.
function describeCall(name: string, rawArguments: unknown, root: string | null): string {
  let args: Record<string, unknown> = {};
  try {
    args = typeof rawArguments === "string" ? JSON.parse(rawArguments) : ((rawArguments as Record<string, unknown>) ?? {});
  } catch {
    // The record keeps only the start of long arguments (a file being written), so the JSON can be cut off: the
    // path or command comes first and is still there.
    const found = String(rawArguments).match(/"(path|file|command|directory|pattern)"\s*:\s*"((?:[^"\\]|\\.)*)"/);
    if (!found) return name;
    const value = found[2].replace(/\\\\/g, "\\").replace(/\\"/g, '"');
    return `${name} ${clip(found[1] === "command" ? value : relative(value, root), 160)}`;
  }
  for (const key of ["path", "file", "command", "directory", "pattern", "query", "url", "repositoryPath"]) {
    const value = args[key];
    if (typeof value === "string" && value.length > 0) {
      return `${name} ${clip(key === "command" ? value : relative(value, root), 160)}`;
    }
  }
  return name;
}

function stepOf(taskId: string | null, planId: string): number | null {
  const match = taskId?.match(/^plan-([0-9a-f]+)-step-(\d+)$/i);
  return match && match[1] === planId ? Number(match[2]) : null;
}

function toLive(event: RecordEvent, planId: string, root: string | null): LiveEvent | null {
  const step = stepOf(event.taskId, planId);
  if (step === null) return null;
  const payload = event.payload ?? {};
  const base = { id: event.id, at: event.atUtc, step, kind: event.kind, node: event.node };
  switch (event.kind) {
    case "route":
      return { ...base, node: (payload.node as string) ?? event.node, text: `routed to ${payload.node ?? "?"} (${payload.tier ?? "?"})` };
    case "tool-call":
      return { ...base, text: describeCall(String(payload.name ?? "tool"), payload.arguments, root) };
    case "tool-result": {
      const result = clip(payload.result, 180);
      return { ...base, text: relative(result, root), ok: !/^error/i.test(result) };
    }
    case "assistant-output":
      return payload.text ? { ...base, node: (payload.node as string) ?? event.node, text: clip(payload.text) } : null;
    case "verification":
      return { ...base, text: `check ${payload.passed ? "passed" : "failed"}: ${clip(payload.command, 160)}`, ok: Boolean(payload.passed) };
    case "plan-transition":
      return { ...base, text: clip(`${payload.to ?? ""} ${payload.note ?? ""}`) };
    default:
      return null;
  }
}

type Plan = {
  contextId?: string | null;
  workingDirectory?: string | null;
  events?: { detail?: string }[] | null;
  [key: string]: unknown;
};

/**
 * One small answer per tick for the live view: the plan, the new events of its durable record cut down to a line
 * each, and which machines are up. The record's events carry whole files (a write_file call holds the file), so they
 * are read here, next to the backend, and never sent to the browser as they are.
 */
export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const after = Number(new URL(request.url).searchParams.get("after") ?? "0") || 0;
  if (id === "demo") return Response.json(demoPayload(after), { headers: { "cache-control": "no-store" } });
  if (!/^[0-9a-f]{32}$/i.test(id)) return Response.json({ error: "Not a plan id." }, { status: 404 });

  let plan: Plan;
  try {
    const upstream = await fetch(`${BACKEND_URL}/api/plans/${id}`, { cache: "no-store" });
    if (!upstream.ok) {
      return Response.json({ error: upstream.status === 404 ? "This plan no longer exists." : "Could not load the plan." }, { status: upstream.status });
    }
    plan = await upstream.json();
  } catch {
    return Response.json({ error: "Could not reach the backend." }, { status: 502 });
  }

  const root = plan.workingDirectory ?? null;
  const [events, nodes] = await Promise.all([
    plan.contextId
      ? fetch(`${BACKEND_URL}/api/contexts/${plan.contextId}/events?limit=${after > 0 ? 150 : 500}`, { cache: "no-store" })
          .then((res) => (res.ok ? (res.json() as Promise<RecordEvent[]>) : []))
          .then((list) =>
            list
              .filter((event) => event.id > after)
              .map((event) => toLive(event, id, root))
              .filter((event): event is LiveEvent => event !== null)
              .sort((a, b) => a.id - b.id),
          )
          .catch(() => [] as LiveEvent[])
      : Promise.resolve([] as LiveEvent[]),
    fetch(`${BACKEND_URL}/api/fleet-status`, { cache: "no-store" })
      .then((res) => (res.ok ? res.json() : null))
      .then((status) => (status?.nodes ?? []) as { name: string; model: string; ready: boolean }[])
      .catch(() => []),
  ]);

  // The run log's details (a failing check's output) are long; the view shows a few lines of each.
  const runEvents = (plan.events ?? []).map((event) => {
    const detail = event.detail ?? "";
    return { ...event, detail: detail.length > 1500 ? `${detail.slice(0, 1499)}…` : detail };
  });
  return Response.json(
    { plan: { ...plan, events: runEvents }, events, nodes, serverTime: new Date().toISOString() },
    { headers: { "cache-control": "no-store" } },
  );
}
