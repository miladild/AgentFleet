/** A chat tool call that waits for the user's answer, as GET /api/approvals lists it. */
export type PendingApproval = {
  id: string;
  contextId: string;
  tool: string;
  arguments: string;
  requestedAtUtc: string;
  expiresAtUtc: string;
  secondsLeft: number;
};

// Enough to name the tool and the start of a short request; the card's own block shows the whole text.
const ARGUMENTS_IN_LINE = 120;

/** The text with its line breaks turned into spaces, or null when it is not a string with any text in it. */
function oneLine(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const flat = value.replace(/\s*[\r\n]+\s*/g, " ").trim();
  return flat === "" ? null : flat;
}

/** The arguments as a JSON object, or null when they are not one (invalid text, a list, a bare value). */
function parseArguments(raw: unknown): Record<string, unknown> | null {
  if (typeof raw !== "string") return null;
  try {
    const value: unknown = JSON.parse(raw);
    return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** " (5 characters)" when the value is text; nothing when the field is missing, so no size is ever made up. */
function sizeNote(value: unknown, label = ""): string {
  if (typeof value !== "string") return "";
  const count = value.length;
  return ` (${label}${count} character${count === 1 ? "" : "s"})`;
}

function describeKnownTool(tool: string, args: Record<string, unknown>): string | null {
  if (tool === "run_command") return oneLine(args.command);
  const path = oneLine(args.path);
  if (path === null) return null;
  if (tool === "write_file") return `Write ${path}${sizeNote(args.content)}`;
  if (tool === "edit_file") return `Edit ${path}${sizeNote(args.newText, "new text: ")}`;
  return null;
}

/**
 * What the tool call would do, in one line. A shell command shows the command; a file write or edit names the file and
 * the size of its text. Any other tool shows its name and the start of its arguments. Arguments that are not JSON, or
 * lack the field, take that last route, so a malformed request still shows what it asked for.
 */
export function describeApproval(item: Partial<Pick<PendingApproval, "tool" | "arguments">>): string {
  const tool = oneLine(item.tool) ?? "tool call";
  const args = parseArguments(item.arguments);
  const known = args ? describeKnownTool(tool, args) : null;
  if (known) return known;

  const raw = oneLine(item.arguments);
  if (raw === null) return tool;
  const start = Array.from(raw);
  return start.length > ARGUMENTS_IN_LINE
    ? `${tool}: ${start.slice(0, ARGUMENTS_IN_LINE).join("")}...`
    : `${tool}: ${raw}`;
}

/** A countdown as m:ss, or h:mm:ss from an hour on. A time that has run out, or cannot be read, shows as 0:00. */
export function formatCountdown(secondsLeft: number): string {
  const total = Number.isFinite(secondsLeft) ? Math.max(0, Math.floor(secondsLeft)) : 0;
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${seconds}` : `${minutes}:${seconds}`;
}

function expiryTime(item: { expiresAtUtc: string }): number {
  const time = Date.parse(item.expiresAtUtc);
  return Number.isNaN(time) ? Number.POSITIVE_INFINITY : time;
}

/**
 * The requests in the order they run out, soonest first, so the one about to expire is the first card. A request whose
 * expiry time cannot be read goes last. Equal times keep a fixed order by id, so the cards do not swap on each poll.
 */
export function sortApprovals<T extends { id: string; expiresAtUtc: string }>(items: readonly T[]): T[] {
  return [...items].sort((a, b) => {
    const left = expiryTime(a);
    const right = expiryTime(b);
    if (left !== right) return left < right ? -1 : 1;
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });
}
