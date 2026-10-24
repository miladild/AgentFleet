// Words and rules for a vision machine's mode (off, on demand, always ready), the role a machine is given, and the
// tone of a machine's status. Pure functions, so the Config panel and the live view agree and the unit tests can pin them.

export type VisionMode = "off" | "ondemand" | "always";

export const VISION_MODES: { value: VisionMode; label: string; hint: string }[] = [
  { value: "off", label: "Off", hint: "not contacted; images get a note" },
  { value: "ondemand", label: "On demand", hint: "loads when an image arrives, leaves when idle" },
  { value: "always", label: "Always ready", hint: "kept loaded" },
];

/** What a vision machine has when its config says nothing: the backend uses the same default. */
export const DEFAULT_VISION_MODE: VisionMode = "ondemand";

/** The reason the backend gives a machine that is switched off on purpose. */
export const TURNED_OFF_REASON = "turned_off";

export const TURNED_OFF_HINT = "Turned off by its Vision machine setting. This is a choice, not a fault: pick a mode to use it again.";

/** The mode the backend accepts in any of its spellings, or null when the value is not a mode at all. */
export function normalizeVisionMode(raw: unknown): VisionMode | null {
  if (typeof raw !== "string") return null;
  const compact = raw.trim().toLowerCase().replace(/[\s_-]+/g, "");
  if (compact === "off" || compact === "ondemand" || compact === "always") return compact;
  return null;
}

/** The mode a vision machine runs with: its own when valid, otherwise on demand. */
export function effectiveVisionMode(node: { visionMode?: string | null }): VisionMode {
  return normalizeVisionMode(node.visionMode) ?? DEFAULT_VISION_MODE;
}

export type RoleNode = { vision: boolean; tier?: string | null; fallback?: boolean; visionMode?: string | null };

// One dropdown covers both fields the backend stores: vision nodes have no tier.
export function roleOf(node: RoleNode): string {
  return node.vision ? "vision" : (node.tier ?? "standard");
}

export function withRole<T extends RoleNode>(node: T, role: string): T {
  // A vision node cannot be the fallback: it cannot serve text-only work with tools.
  if (role === "vision") return { ...node, vision: true, tier: null, fallback: false, visionMode: effectiveVisionMode(node) } as T;
  return { ...node, vision: false, tier: role } as T;
}

/**
 * The nodes as the backend should receive them: a vision machine carries its mode, a text machine carries none
 * (the backend keeps only the vision setting on vision nodes).
 */
export function nodesForSave<T extends { vision: boolean; visionMode?: string | null }>(nodes: T[]): T[] {
  return nodes.map((node) => {
    if (node.vision) return { ...node, visionMode: effectiveVisionMode(node) } as T;
    const { visionMode: _unused, ...text } = node;
    return text as T;
  });
}

export type StatusTone = "ready" | "warn" | "down" | "off" | "unknown";

/**
 * The colour family for a machine's status line. A machine turned off by its setting is "off", a neutral state,
 * never "down": a warning or red dot would tell the owner something is broken when it is not.
 */
export function statusTone(status: { ready: boolean; reachable?: boolean; reason?: string | null } | undefined): StatusTone {
  if (!status) return "unknown";
  if (status.reason === TURNED_OFF_REASON) return "off";
  if (status.ready) return "ready";
  return status.reachable ? "warn" : "down";
}
