// Which chat tool calls the user is asked about, and what the prompt says. Kept free of the vscode module so it can be
// tested with plain Node (see test/approvalPrompts.test.js).

/** A chat tool call that the backend holds until someone answers, as its approval list describes it. */
export interface PendingApproval {
  id: string;
  /** The chat the call belongs to; empty when the backend could not tie the call to a chat. */
  contextId: string;
  tool: string;
  /** The arguments as JSON text, already cut by the backend; empty when the list leaves them out. */
  arguments: string;
  /** Whole seconds until the backend refuses the call. */
  secondsLeft: number;
}

/** What the user chose in the prompt. Anything else (the prompt was dismissed) decides nothing. */
export type ApprovalChoice = "approve" | "reject";

export interface WatchApprovalsOptions {
  /** The chat's thread id. Only this chat's calls are shown. */
  threadId: string;
  /** Also shows the calls the backend could not tie to a chat. Only a chat without a durable context needs it. */
  includeUnattributed?: boolean;
  /** Stops the watch. No list call is made once it aborts. */
  signal: AbortSignal;
  /** Fetches the backend's approval list, as JSON. */
  list: () => Promise<unknown>;
  /** Answers one call. */
  decide: (id: string, approve: boolean) => Promise<void>;
  /** Shows the prompt for one call and resolves to the user's choice. */
  ask: (item: PendingApproval) => Promise<ApprovalChoice | undefined>;
  intervalMs?: number;
  /** Waits between rounds. Must return early when the signal aborts. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
}

// The prompt's arguments are cut to this many characters, the ellipsis included.
const ARGUMENTS_LIMIT = 300;

const HYPHENATED_GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Only a hyphenated GUID gives a chat a durable context on the backend, so only such a chat's calls carry its id. */
export function isHyphenatedGuid(value: string): boolean {
  return HYPHENATED_GUID.test(value);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/**
 * The waiting calls of one chat. Entries that are not complete are skipped rather than failing the whole list, and so
 * is a call whose time is up: the backend refuses it then, so a prompt would only be a click that does nothing.
 * A call with no chat id comes from a chat without a durable context; it is listed only when asked for.
 */
export function approvalsFor(list: unknown, threadId: string, includeUnattributed = false): PendingApproval[] {
  if (!Array.isArray(list)) return [];
  const pending: PendingApproval[] = [];
  for (const entry of list) {
    if (!isObject(entry)) continue;
    const { id, contextId, tool, secondsLeft } = entry;
    if (typeof id !== "string" || typeof contextId !== "string" || typeof tool !== "string") continue;
    if (contextId !== threadId && !(includeUnattributed && contextId === "")) continue;
    if (typeof secondsLeft !== "number" || !(secondsLeft > 0)) continue;
    const args = typeof entry.arguments === "string" ? entry.arguments : "";
    pending.push({ id, contextId, tool, arguments: args, secondsLeft });
  }
  return pending;
}

/**
 * The prompt for one call. VS Code shows a notification's detail only for modal prompts, and this one is not modal,
 * so the arguments and the time left go into the message itself.
 */
export function approvalText(item: PendingApproval): { message: string } {
  const shown = clipText(item.arguments.replace(/\s*(\r\n|\r|\n)\s*/g, " ").trim());
  const withArguments = shown.length > 0 ? ` with ${shown}` : "";
  const seconds = `${item.secondsLeft} ${item.secondsLeft === 1 ? "second" : "seconds"}`;
  const timeLeft = `No answer in ${seconds} means it is refused.`;
  return { message: `Agent Fleet wants to run "${item.tool}"${withArguments}. Allow it? ${timeLeft}` };
}

function clipText(text: string): string {
  const characters = Array.from(text);
  return characters.length <= ARGUMENTS_LIMIT ? text : `${characters.slice(0, ARGUMENTS_LIMIT - 3).join("")}...`;
}

/**
 * Polls the backend's approval list until the signal aborts, and asks about each new call of this chat once. The prompt
 * is not awaited by the watch, so a call that arrives while another prompt is open is still shown. Failures are
 * swallowed: the backend may be unreachable for a moment, and the next round tries again.
 */
export async function watchApprovals(options: WatchApprovalsOptions): Promise<void> {
  const {
    threadId,
    includeUnattributed = false,
    signal,
    list,
    decide,
    ask,
    intervalMs = 2000,
    sleep = sleepUnlessAborted,
  } = options;
  const asked = new Set<string>();

  const answer = async (item: PendingApproval): Promise<void> => {
    try {
      const choice = await ask(item);
      // Stopped while the prompt was open: the chat was cancelled or ended, so a late click must not approve its call.
      if (!signal.aborted && (choice === "approve" || choice === "reject")) await decide(item.id, choice === "approve");
    } catch {
      // Left to the backend, which refuses the call when its time is up.
    }
  };

  while (!signal.aborted) {
    try {
      const pending = approvalsFor(await list(), threadId, includeUnattributed);
      // Stopped while the list was on its way: a finished chat must not raise new prompts.
      if (signal.aborted) return;
      for (const item of pending) {
        if (asked.has(item.id)) continue;
        asked.add(item.id);
        void answer(item);
      }
    } catch {
      // The backend may be unreachable for a moment; the next round tries again.
    }
    await sleep(intervalMs, signal);
  }
}

/** Waits for the time, or returns at once when the signal aborts, so a stopped watch leaves no timer behind. */
export function sleepUnlessAborted(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const finish = (): void => {
      clearTimeout(timer);
      signal.removeEventListener("abort", finish);
      resolve();
    };
    const timer = setTimeout(finish, ms);
    if (signal.aborted) finish();
    else signal.addEventListener("abort", finish);
  });
}
