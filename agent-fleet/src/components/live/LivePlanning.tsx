"use client";

import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Clock, hhmmss, LogPane, useMachineColors, useReducedMotion } from "./LiveRun";
import type { LiveEvent, LiveNode, LogLine } from "./useLiveFeed";

type Turn = {
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

type Feed = { context: { id: string; title: string }; turn: Turn | null; nodes: LiveNode[] };

const MAX_EVENTS = 600;

/** Follows the strongest machine working out a plan: every two seconds while it works, fifteen once it has answered. */
function usePlanningFeed(contextId: string) {
  const [feed, setFeed] = useState<Feed | null>(null);
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let after = 0;
    let turnStart = 0;
    let inFlight = false;
    let live = true;

    const schedule = () => {
      clearTimeout(timer);
      if (!stopped && !document.hidden) timer = setTimeout(tick, live ? 2000 : 15000);
    };
    const tick = async () => {
      if (stopped || inFlight || document.hidden) return;
      inFlight = true;
      try {
        const res = await fetch(`/api/live/planning/${contextId}?after=${after}`, { cache: "no-store" });
        const data = await res.json();
        if (stopped) return;
        if (!res.ok) {
          setError(data.error ?? "Could not load the conversation.");
          return;
        }
        setError(null);
        const turn: Turn | null = data.turn;
        // A new turn starts a new picture.
        if (turn && turn.startId !== turnStart) {
          turnStart = turn.startId;
          setEvents([]);
        }
        setFeed({ context: data.context, turn, nodes: data.nodes ?? [] });
        const fresh: LiveEvent[] = data.events ?? [];
        if (fresh.length > 0) {
          after = Math.max(after, ...fresh.map((event) => event.id));
          setEvents((current) => {
            const seen = new Set(current.map((event) => event.id));
            const merged = [...current, ...fresh.filter((event) => !seen.has(event.id))];
            return merged.length > MAX_EVENTS ? merged.slice(merged.length - MAX_EVENTS) : merged;
          });
        }
        live = !!turn && !turn.ended && !turn.planId;
      } catch {
        if (!stopped) setError("Could not reach the web server.");
      } finally {
        inFlight = false;
        schedule();
      }
    };
    const onVisibility = () => {
      if (document.hidden) clearTimeout(timer);
      else tick();
    };
    document.addEventListener("visibilitychange", onVisibility);
    tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [contextId]);

  return { feed, events, error };
}

// ---------- what the planner looked at ----------

type TargetKind = "file" | "folder" | "search" | "web" | "blocked" | "decision" | "tool";
type Target = { key: string; kind: TargetKind; label: string; full: string; count: number; lastId: number; slot: number };

const KIND_GLYPH: Record<TargetKind, string> = { file: "▤", folder: "▸", search: "⌕", web: "◍", blocked: "⛔", decision: "◆", tool: "⚙" };

function kindOf(tool: string): TargetKind {
  if (/blocked/i.test(tool)) return "blocked";
  if (/web|fetch|docs|learn|browse|url/i.test(tool)) return "web";
  if (/search|find|grep|glob/i.test(tool)) return "search";
  if (/list|tree|dir|overview/i.test(tool)) return "folder";
  if (/decision/i.test(tool)) return "decision";
  if (/read|open|view|file/i.test(tool)) return "file";
  return "tool";
}

// The last two parts of a path ("server/index.ts"), or the words of a search.
function labelOf(kind: TargetKind, target: string): string {
  if (kind === "file" || kind === "folder") {
    const parts = target.replace(/\\/g, "/").split("/").filter(Boolean);
    const label = parts.slice(-2).join("/");
    return kind === "folder" ? `${parts[parts.length - 1] ?? label}/` : label;
  }
  return target.length > 22 ? `${target.slice(0, 21)}…` : target;
}

// Orbits around the planner: six places on the inner one, ten, then fourteen. A target keeps its place.
const W = 1000;
const H = 560;
const CX = W / 2;
const CY = H / 2;
const RINGS = [
  { rx: 210, ry: 108, count: 6, offset: 0 },
  { rx: 330, ry: 176, count: 10, offset: Math.PI / 10 },
  { rx: 420, ry: 236, count: 14, offset: 0 },
];
const SLOTS = RINGS.flatMap((ring) =>
  Array.from({ length: ring.count }, (_, index) => {
    const angle = ring.offset + (index / ring.count) * Math.PI * 2 - Math.PI / 2;
    return { x: CX + ring.rx * Math.cos(angle), y: CY + ring.ry * Math.sin(angle) };
  }),
);

function targetsOf(events: LiveEvent[]): { targets: Target[]; total: number } {
  const byKey = new Map<string, Target>();
  let order = 0;
  for (const event of events) {
    if (event.kind !== "tool-call" || !event.tool) continue;
    const kind = kindOf(event.tool);
    const full = event.target ?? event.tool;
    const key = `${kind}:${full.toLowerCase()}`;
    const known = byKey.get(key);
    if (known) {
      known.count += 1;
      known.lastId = event.id;
    } else {
      byKey.set(key, { key, kind, label: labelOf(kind, full), full, count: 1, lastId: event.id, slot: order++ % SLOTS.length });
    }
  }
  // Past thirty, a newer target takes the place of the one that had it.
  const bySlot = new Map<number, Target>();
  for (const target of byKey.values()) {
    const holder = bySlot.get(target.slot);
    if (!holder || holder.lastId < target.lastId) bySlot.set(target.slot, target);
  }
  return { targets: [...bySlot.values()], total: byKey.size };
}

const ScanMap = memo(function ScanMap({
  targets,
  latest,
  node,
  model,
  color,
  state,
  since,
  working,
}: {
  targets: Target[];
  latest: number;
  node: string;
  model: string;
  color: string;
  state: string;
  since: number | null;
  working: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: W, height: H });
  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => setSize({ width: entry.contentRect.width, height: entry.contentRect.height }));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const scale = Math.max(0.35, Math.min(1.1, (size.width - 8) / W, (size.height - 8) / H));

  return (
    <div className="live-stage" ref={box}>
      <div style={{ width: W * scale, height: H * scale, margin: "0 auto" }}>
        <div className={`live-scan${working ? " working" : ""}`} style={{ width: W, height: H, transform: `scale(${scale})`, ["--m" as string]: color }}>
          <div className="radar" aria-hidden="true">
            <i />
          </div>
          <svg width={W} height={H} aria-hidden="true">
            {RINGS.map((ring) => (
              <ellipse key={ring.rx} className="orbit" cx={CX} cy={CY} rx={ring.rx} ry={ring.ry} />
            ))}
            {targets.map((target) => (
              <line
                key={target.key}
                className={`ray${target.lastId === latest ? " hot" : ""}`}
                x1={CX}
                y1={CY}
                x2={SLOTS[target.slot].x}
                y2={SLOTS[target.slot].y}
              />
            ))}
          </svg>
          {targets.map((target) => (
            <div
              key={target.key}
              className={`live-target ${target.kind}${target.lastId === latest ? " latest" : ""}`}
              style={{ left: SLOTS[target.slot].x, top: SLOTS[target.slot].y }}
              title={`${target.full}${target.count > 1 ? ` (${target.count} times)` : ""}`}
            >
              <span className="g">{KIND_GLYPH[target.kind]}</span>
              {target.label}
              {target.count > 1 && <small>×{target.count}</small>}
            </div>
          ))}
          <div className="live-planner" style={{ left: CX, top: CY }}>
            <div className="live-core big">
              <i />
              <i />
              <b />
            </div>
            <b className="who">{node}</b>
            <small>{model}</small>
            <span className="state">
              {state}
              {since !== null && <Elapsed since={since} />}
            </span>
          </div>
        </div>
      </div>
    </div>
  );
});

// Seconds since the model call started, ticking on its own so nothing else redraws every second.
function Elapsed({ since }: { since: number }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return <> · {Math.max(0, Math.round((now - since) / 1000))} s</>;
}

const PHASES = ["read the code", "propose a plan", "your approval", "the fleet runs it"];

function Phases({ turn }: { turn: Turn | null }) {
  const noPlan = !!turn?.ended && !turn.planId;
  const at = !turn ? 0 : turn.planId ? 2 : turn.ended ? 1 : 0;
  return (
    <ol className="live-phases">
      {PHASES.map((phase, index) => {
        const state = noPlan && index === 1 ? "failed" : index < at ? "done" : index === at && !noPlan ? "running" : "pending";
        return (
          <li key={phase} className={state}>
            <span className="dot">{state === "done" ? "✔" : state === "failed" ? "✖" : index + 1}</span>
            {noPlan && index === 1 ? "answered without a plan" : phase}
          </li>
        );
      })}
    </ol>
  );
}

// ---------- the page ----------

/** The strongest machine working out a plan in one conversation, before there is a plan to show. */
export function LivePlanning({ contextId, following = false }: { contextId: string; following?: boolean }) {
  const { feed, events, error } = usePlanningFeed(contextId);
  const nodes = useMemo(() => feed?.nodes ?? [], [feed?.nodes]);
  const colorOf = useMachineColors(nodes);
  const reducedMotion = useReducedMotion();
  const [filter, setFilter] = useState<number | "errors" | null>(null);
  const [hidden, setHidden] = useState(false);

  useEffect(() => {
    const onVisibility = () => setHidden(document.hidden);
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  const { targets, total } = useMemo(() => targetsOf(events), [events]);
  // Tool calls and results carry no machine in the record: they are the planner's, the machine of the latest model call.
  const log: LogLine[] = useMemo(() => {
    let planner: string | null = null;
    return events.map((event) => {
      if (event.kind === "route" && event.node) planner = event.node;
      return {
        key: `r${event.id}`,
        at: Date.parse(event.at),
        step: null,
        node: event.node ?? (event.kind === "plan-transition" ? null : planner),
        kind: event.kind,
        text: event.text,
        tone:
          event.kind === "tool-result"
            ? event.ok === false
              ? "bad"
              : "dim"
            : event.kind === "assistant-output"
              ? "accent"
              : event.kind === "route"
                ? "dim"
                : event.kind === "plan-transition"
                  ? "good"
                  : "info",
      } satisfies LogLine;
    });
  }, [events]);

  const turn = feed?.turn ?? null;
  useEffect(() => {
    document.title = `${turn && !turn.ended ? "◉ " : ""}planning · Fleet live`;
  }, [turn]);

  if (!feed) {
    return (
      <div className="live-root">
        <div className="live-empty" style={{ gridRow: "1 / -1" }}>
          <span className="live-glow-text">{error ?? "connecting to the fleet…"}</span>
        </div>
      </div>
    );
  }

  const working = !!turn && !turn.ended && !turn.planId;
  const lastRoute = [...events].reverse().find((event) => event.kind === "route");
  const planner = lastRoute?.node ?? nodes[0]?.name ?? "hub";
  const plannerNode = nodes.find((node) => node.name === planner);
  const lastEvent = events[events.length - 1];
  const thought = [...events].reverse().find((event) => event.kind === "assistant-output")?.text ?? null;
  const latest = [...events].reverse().find((event) => event.kind === "tool-call")?.id ?? -1;
  const reads = events.filter((event) => event.kind === "tool-call").length;
  const calls = events.filter((event) => event.kind === "route").length;
  const state = !turn
    ? "waiting for a request"
    : turn.planId
      ? "plan proposed"
      : turn.ended
        ? "answered in the chat"
        : lastEvent?.kind === "route"
          ? "thinking"
          : lastEvent?.kind === "tool-call"
            ? "reading"
            : "working";
  const started = turn ? Date.parse(turn.startedAt) : null;

  return (
    <div className={`live-root${hidden ? " live-paused" : ""}`}>
      <div className="live-bar">
        <a className="mod logo" href="/" title="Back to the chat">◢ FLEET</a>
        <span className="mod title" title={turn?.request ?? feed.context.title}>
          <span>{turn?.request ?? feed.context.title}</span>
        </span>
        <span className={`mod live-status ${working ? "planning" : turn?.planId ? "done" : "rejected"}`}>
          <span className="dot" />
          {working ? "planning" : turn?.planId ? "plan proposed" : "no plan"}
        </span>
        <Clock from={started} until={working || !turn ? null : Date.parse(turn.lastAt)} />
        <span className="mod hide-narrow" aria-label="Machines">
          {nodes.map((node) => (
            <span key={node.name} title={`${node.name}: ${node.model} (${node.ready ? "ready" : "not ready"})`} style={{ color: node.ready ? colorOf(node.name) : "#565f89" }}>
              {working && node.name === planner ? "◉" : "●"} {node.name}
            </span>
          ))}
        </span>
        {following && (
          <span className="mod hide-narrow follow" title="This page follows the fleet: the plan shows here as soon as it is proposed">
            ◎ following
          </span>
        )}
        <a className="mod hide-narrow" href="/live?pick" title="Plans">≡</a>
      </div>
      <div className="live-progress" aria-hidden="true">
        <div className="fill" style={{ transform: `scaleX(${turn?.planId ? 0.5 : turn?.ended ? 0.25 : 0.1})` }} />
        {working && !reducedMotion && <div className="shine" />}
      </div>
      <div className="live-main">
        <section className="live-tile focus">
          <header>
            <b>planner</b> {total} looked at · {reads} tool calls · {calls} model calls
            <span className="spacer" />
            {turn?.surface && <span>from {turn.surface === "vscode" ? "VS Code" : turn.surface}</span>}
          </header>
          {turn?.ended && !turn.planId && (
            <div className="live-banner warn">
              <div className="head">
                <span className="icon">◇</span>
                <b>NO PLAN THIS TIME</b>
                <span className="muted">the planner answered in the chat</span>
              </div>
              {turn.answer && <div className="hint answer">{turn.answer}</div>}
              <div className="hint">
                To get a plan, ask for one: in VS Code <code>@fleet /plan</code> and what to do, or in the web chat with plan mode on.
              </div>
            </div>
          )}
          <ScanMap
            targets={targets}
            latest={latest}
            node={planner}
            model={plannerNode?.model ?? ""}
            color={colorOf(planner)}
            state={state}
            // The model writes for a while before anything reaches the record: how long it has been at it.
            since={working && lastEvent?.kind === "route" ? Date.parse(lastEvent.at) : null}
            working={working && !reducedMotion}
          />
          {thought && (
            <div className="live-thought" title={thought}>
              <span className="prompt">❯</span> {thought}
              {working && <span className="cursor" />}
            </div>
          )}
        </section>
        <div className="live-side">
          <section className="live-tile">
            <header>
              <b>where it is</b>
            </header>
            <Phases turn={turn} />
          </section>
          <section className="live-tile">
            <header>
              <b>request</b>
              <span className="spacer" />
              {turn && hhmmss(Date.parse(turn.startedAt))}
            </header>
            <div className="live-inspector">
              <div>{turn?.request ?? "(not saved yet)"}</div>
              <div className="label">conversation</div>
              <div>{feed.context.title}</div>
              <div className="label">tip</div>
              <div>Every file and search the planner goes through lands on the map; hover one for its full path. The plan shows here once it is proposed.</div>
            </div>
          </section>
        </div>
      </div>
      <LogPane log={log} steps={[]} filter={filter} setFilter={setFilter} colorOf={colorOf} live={working} />
    </div>
  );
}
