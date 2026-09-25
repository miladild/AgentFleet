// How a VS Code chat joins a fleet conversation. Kept free of the vscode module so it can be tested with
// plain Node (see test/fleetLink.test.js).
import { createHash } from "node:crypto";

/** A message as the backend stores and replays it (AG-UI shape); the web UI's may carry more fields. */
export interface WireMessage {
  id: string;
  role: string;
  content?: unknown;
  toolCallId?: string;
}

export const INSTRUCTIONS_PREFIX = "Workspace custom instructions";

/** Which durable context a model-picker request belongs to, and how the backend should record it. */
export interface PickerRouting {
  /** A hyphenated GUID when the request is journaled; a GUID without hyphens opts out. */
  threadId: string;
  contextId: string | null;
  /** Joined an existing chat: record the messages, leave that chat's visible transcript alone. */
  journalOnly: boolean;
  /** The id to hand back in the invisible data part (only with the experimental carrier). */
  carrierId: string | null;
}

/**
 * The model picker has no per-chat id of its own. A link set with "Continue a fleet chat" wins: every
 * Fleet Router request then joins that chat until the link is removed. Otherwise the experimental carrier
 * gives each chat its own context. Otherwise the request is not journaled at all, so title generation and
 * other side requests never create contexts.
 */
export function routePickerRequest(options: {
  carrierEnabled: boolean;
  carriedId: string | null;
  linkedId: string | null;
  newId: () => string;
}): PickerRouting {
  if (options.linkedId) {
    return { threadId: options.linkedId, contextId: options.linkedId, journalOnly: true, carrierId: null };
  }

  if (options.carrierEnabled) {
    const id = options.carriedId ?? options.newId();
    return { threadId: id, contextId: id, journalOnly: false, carrierId: id };
  }

  return { threadId: options.newId().replace(/-/g, ""), contextId: null, journalOnly: false, carrierId: null };
}

/**
 * The model picker's messages have no ids, and several picker chats may join the same conversation, so an
 * index would collide. The content decides instead: a message replayed on the next turn is recognised and
 * not recorded twice.
 */
export function pickerMessageId(role: string, content: string): string {
  return `vp-${createHash("sha256").update(`${role}\n${content}`).digest("hex").slice(0, 24)}`;
}

function isInstructions(message: WireMessage): boolean {
  return message.role === "system" && typeof message.content === "string" && message.content.startsWith(INSTRUCTIONS_PREFIX);
}

/**
 * The messages for one turn of an @fleet chat that continues a fleet conversation: the conversation's saved
 * transcript (so turns made in the web UI are included and kept), this workspace's instructions sent fresh
 * instead of an old copy, then the new user message. Null when the transcript is not a list.
 */
export function continuedMessages(transcript: unknown, instructions: WireMessage | null, turn: WireMessage): WireMessage[] | null {
  if (!Array.isArray(transcript)) {
    return null;
  }

  const saved = transcript.filter(
    (message): message is WireMessage =>
      typeof message === "object" && message !== null && typeof (message as WireMessage).role === "string" && !isInstructions(message as WireMessage),
  );
  return [...(instructions ? [instructions] : []), ...saved, turn];
}

/** A one-line description of a chat for a picker: how many messages and how long ago it was used. */
export function describeSession(messageCount: number, updatedAtUtc: string, now: Date = new Date()): string {
  const minutes = Math.max(0, Math.round((now.getTime() - new Date(updatedAtUtc).getTime()) / 60000));
  const ago =
    minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 48 * 60 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} days ago`;
  return `${messageCount} message${messageCount === 1 ? "" : "s"}, ${ago}`;
}

/** One AG-UI context entry: what it is, and its value. */
export interface ContextEntry {
  description: string;
  value: string;
}

const MAX_CONTEXT_VALUE_CHARS = 2000;
const UNSAFE_PATH_CHARACTER = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/;

/**
 * The workspace folders as AG-UI context, so the fleet knows which project "the tests" or "this file" means. Only when
 * the backend runs on this same computer: the fleet's file tools work on the hub's disk, and a path on another
 * machine (or a remote workspace) would send it looking in the wrong place.
 */
export function workspaceContext(backendUrl: string, folders: ReadonlyArray<{ scheme: string; fsPath: string }>): ContextEntry[] {
  let host: string;
  try {
    host = new URL(backendUrl).hostname.toLowerCase();
  } catch {
    return [];
  }

  if (!["localhost", "127.0.0.1", "[::1]", "::1"].includes(host)) {
    return [];
  }

  const local: string[] = [];
  let used = 0;
  for (const folder of folders) {
    const path = folder.fsPath;
    if (folder.scheme !== "file" || !path || UNSAFE_PATH_CHARACTER.test(path) || local.includes(path)) {
      continue;
    }

    // Keep paths whole. A partial path looks valid but sends the agent to the wrong place.
    const added = path.length + (local.length === 0 ? 0 : 2);
    if (used + added > MAX_CONTEXT_VALUE_CHARS) {
      continue;
    }

    local.push(path);
    used += added;
  }

  if (local.length === 0) {
    return [];
  }

  return [
    {
      description: local.length === 1 ? "The project folder the user is working in (open in VS Code)" : "The project folders open in VS Code",
      value: local.join("; "),
    },
  ];
}
