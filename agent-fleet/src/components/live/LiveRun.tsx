"use client";

import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { PlanStep } from "../PlanCard";
import { useLiveFeed, type LiveNode, type LivePlan, type LogLine, type StepActivity } from "./useLiveFeed";
import { blockOf, stepFailures, stepTiers, withRunAttempts, type Why } from "./why";

// ---------- colours and helpers ----------

const PALETTE = ["#bb9af7", "#7dcfff", "#9ece6a", "#ff9e64", "#e0af68", "#2ac3de", "#ff4fa3", "#73daca"];

/** Each machine keeps one colour everywhere: its card, its badge on a step and its name in the log. */
export function useMachineColors(nodes: LiveNode[]) {
  return useMemo(() => {
    const map = new Map<string, string>();
    nodes.forEach((node, index) => map.set(node.name, PALETTE[index % PALETTE.length]));
    return (name: string | null | undefined) => {
      if (!name) return "#565f89";
      const known = map.get(name);
      if (known) return known;
      let hash = 0;
      for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
      return PALETTE[hash % PALETTE.length];
    };
  }, [nodes]);
}

const GLYPH: Record<string, string> = { pending: "○", running: "◐", done: "✔", failed: "✖" };

// A tier as a strength meter and its name, with what it means on hover.
const TIERS: Record<string, { bars: number; hint: string }> = {
  heavy: { bars: 3, hint: "Heavy: the strongest machine, for hard or risky work" },
  standard: { bars: 2, hint: "Standard: a mid-size model, for ordinary work" },
  light: { bars: 1, hint: "Light: a small, quick model, for small edits" },
};

const NO_SIBLINGS: number[] = [];

export function TierMeter({ tier, planned }: { tier: string; planned?: string }) {
  const known = TIERS[tier] ?? { bars: 0, hint: tier };
  // A retry runs on the heavy tier whatever the step was planned for: the arrow says it was raised.
  const raised = planned && planned !== tier;
  return (
    <span className={`tier ${tier}${raised ? " raised" : ""}`} title={raised ? `Planned as ${planned}; retried on ${tier}. ${known.hint}` : known.hint}>
      {raised && <b>↑</b>}
      {[1, 2, 3].map((bar) => (
        <i key={bar} className={bar <= known.bars ? "on" : ""} />
      ))}
      {tier}
    </span>
  );
}

export function hhmmss(ms: number) {
  return new Date(ms).toLocaleTimeString([], { hour12: false });
}

function duration(ms: number) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

export function useReducedMotion() {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    setReduced(query.matches);
    const onChange = () => setReduced(query.matches);
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, []);
  return reduced;
}

// ---------- layout of the pipeline ----------

type Stage = PlanStep[];
type Placed = { step: PlanStep; x: number; y: number; stage: number };
type Edge = { from: Placed; to: Placed; d: string };

const NODE_W = 236;
const NODE_H = 108;
const GAP_MAIN = 66;
const GAP_CROSS = 18;
const PAD = 18;

/** Steps in stages: a parallel group is one stage, every other step is a stage of its own. */
function stagesOf(steps: PlanStep[]): Stage[] {
  const stages: Stage[] = [];
  for (const step of steps) {
    const last = stages[stages.length - 1];
    if (last && step.parallelGroup && last[0].parallelGroup === step.parallelGroup) last.push(step);
    else stages.push([step]);
  }
  return stages;
}

function layout(stages: Stage[], vertical: boolean) {
  const along = vertical ? NODE_H : NODE_W;
  const across = vertical ? NODE_W : NODE_H;
  const widest = Math.max(1, ...stages.map((stage) => stage.length));
  const span = widest * across + (widest - 1) * GAP_CROSS;
  const placed: Placed[] = [];
  stages.forEach((stage, index) => {
    const main = PAD + index * (along + GAP_MAIN);
    const used = stage.length * across + (stage.length - 1) * GAP_CROSS;
    stage.forEach((step, j) => {
      const cross = PAD + (span - used) / 2 + j * (across + GAP_CROSS);
      placed.push(vertical ? { step, x: cross, y: main, stage: index } : { step, x: main, y: cross, stage: index });
    });
  });
  const mainSize = PAD * 2 + stages.length * along + Math.max(0, stages.length - 1) * GAP_MAIN;
  const crossSize = PAD * 2 + span;
  const edges: Edge[] = [];
  for (let i = 0; i + 1 < stages.length; i++) {
    for (const from of placed.filter((p) => p.stage === i)) {
      for (const to of placed.filter((p) => p.stage === i + 1)) {
        const d = vertical
          ? (() => {
              const x1 = from.x + NODE_W / 2, y1 = from.y + NODE_H, x2 = to.x + NODE_W / 2, y2 = to.y;
              const mid = (y1 + y2) / 2;
              return `M ${x1} ${y1} C ${x1} ${mid}, ${x2} ${mid}, ${x2} ${y2}`;
            })()
          : (() => {
              const x1 = from.x + NODE_W, y1 = from.y + NODE_H / 2, x2 = to.x, y2 = to.y + NODE_H / 2;
              const mid = (x1 + x2) / 2;
              return `M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`;
            })();
        edges.push({ from, to, d });
      }
    }
  }
  return {
    placed,
    edges,
    width: vertical ? crossSize : mainSize,
    height: vertical ? mainSize : crossSize,
  };
}

/**
 * A packet travelling along a live edge. Measured: moving it with a CSS motion path cost a fifth of the page's main
 * thread, because the browser works the position out on every frame. Here the path is sampled once and the packet
 * moves by transform keyframes, which the graphics card plays without the page's help.
 */
function Packet({ d, delay }: { d: string; delay: number }) {
  const dot = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    const element = dot.current;
    if (!element || typeof element.animate !== "function") return;
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", d);
    const length = path.getTotalLength();
    const samples = 24;
    const keyframes = Array.from({ length: samples + 1 }, (_, index) => {
      const point = path.getPointAtLength((length * index) / samples);
      return { transform: `translate(${point.x}px, ${point.y}px)` };
    });
    const animation = element.animate(keyframes, { duration: 1600, delay: delay * 1000, iterations: Infinity, easing: "linear" });
    return () => animation.cancel();
  }, [d, delay]);
  return <span ref={dot} className="live-packet-dot" />;
}

/** Steps whose status just changed: a finished step flashes once, a failed one glitches once. */
function useTransitions(steps: PlanStep[]) {
  const previous = useRef(new Map<number, string>());
  const [burst, setBurst] = useState<Set<number>>(new Set());
  const [glitch, setGlitch] = useState<Set<number>>(new Set());
  useEffect(() => {
    const done: number[] = [];
    const failed: number[] = [];
    for (const step of steps) {
      const before = previous.current.get(step.id);
      if (before && before !== step.status) {
        if (step.status === "done") done.push(step.id);
        if (step.status === "failed") failed.push(step.id);
      }
      previous.current.set(step.id, step.status);
    }
    if (done.length === 0 && failed.length === 0) return;
    setBurst(new Set(done));
    setGlitch(new Set(failed));
    const timer = setTimeout(() => {
      setBurst(new Set());
      setGlitch(new Set());
    }, 1200);
    return () => clearTimeout(timer);
  }, [steps]);
  return { burst, glitch };
}

// ---------- pieces ----------

export function Clock({ from, until }: { from: number | null; until: number | null }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (until !== null) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [until]);
  return (
    <>
      <span className="mod hide-narrow" title="Time since the plan was approved">
        ⏱ {from === null ? "--:--" : duration((until ?? now) - from)}
      </span>
      <span className="mod hide-narrow">{new Date(now).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false })}</span>
    </>
  );
}

/** approve, reject, stop or skip a step, with the confirmation each needs; true while one is on its way. */
function usePlanAction(planId: string) {
  const [acting, setActing] = useState(false);
  async function act(action: "stop" | "approve" | "reject" | "skip", step?: PlanStep) {
    const question =
      action === "stop"
        ? "Stop this plan? Finished steps stay done; you can resume it later."
        : action === "reject"
          ? "Reject this plan? It will not run."
          : action === "skip" && step
            ? `Skip step ${step.id} (${step.title})? It counts as done without its check, and the plan goes on with the next step.`
            : null;
    if (question && !window.confirm(question)) return;
    setActing(true);
    try {
      const query = action === "skip" && step ? `?step=${step.id}&resume=true` : "";
      const res = await fetch(`/api/plans/${planId}/${action}${query}`, { method: "POST" });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        window.alert(body.error ?? `Could not ${action} the plan.`);
      }
    } finally {
      setActing(false);
    }
  }
  return [acting, act] as const;
}

function stageStatus(stage: Stage) {
  if (stage.some((step) => step.status === "failed")) return "failed";
  if (stage.some((step) => step.status === "running")) return "running";
  if (stage.every((step) => step.status === "done")) return "done";
  return "pending";
}

function TopBar({
  plan,
  stages,
  nodes,
  colorOf,
  busyNodes,
  following,
}: {
  plan: LivePlan;
  stages: Stage[];
  nodes: LiveNode[];
  colorOf: (name: string | null | undefined) => string;
  busyNodes: Set<string>;
  following: boolean;
}) {
  const [acting, act] = usePlanAction(plan.id);
  const done = plan.steps.filter((step) => step.status === "done").length;
  const started = plan.approvedUtc ? Date.parse(plan.approvedUtc) : null;
  const finished = plan.status === "done" || plan.status === "blocked" || plan.status === "rejected" ? Date.parse(plan.updatedUtc) : null;

  return (
    <div className="live-bar">
      <a className="mod logo" href="/" title="Back to the chat">◢ FLEET</a>
      <span className="live-ws" aria-label="Stages">
        {stages.map((stage, index) => (
          <i key={index} className={stageStatus(stage)} title={stage.map((step) => `#${step.id} ${step.title}`).join("\n")}>
            {index + 1}
          </i>
        ))}
      </span>
      <span className="mod title" title={plan.goal}>
        <span>{plan.title}</span>
      </span>
      <span className={`mod live-status ${plan.status}`}>
        <span className="dot" />
        {plan.status.replace("-", " ")}
      </span>
      <span className="mod">◆ {done}/{plan.steps.length}</span>
      <Clock from={started} until={finished} />
      <span className="mod hide-narrow" aria-label="Machines">
        {nodes.map((node) => (
          <span key={node.name} title={`${node.name}: ${node.model} (${node.ready ? "ready" : "not ready"})`} style={{ color: node.ready ? colorOf(node.name) : "#565f89" }}>
            {busyNodes.has(node.name) ? "◉" : "●"} {node.name}
          </span>
        ))}
      </span>
      {plan.id !== "demo" && (plan.status === "running" || plan.status === "approved") && (
        <button type="button" className="mod danger" disabled={acting} onClick={() => act("stop")}>
          ■ stop
        </button>
      )}
      {following && (
        <span className="mod hide-narrow follow" title="This page follows the fleet: a new plan, or one being worked out, shows here by itself">
          ◎ following
        </span>
      )}
      <a className="mod hide-narrow" href="/live?pick" title="Other plans">≡</a>
    </div>
  );
}

const StepNode = memo(function StepNode({
  placed,
  siblings,
  activity,
  color,
  failure,
  tierNow,
  selected,
  burst,
  glitch,
  onSelect,
  onHover,
}: {
  placed: Placed;
  siblings: number[];
  activity: StepActivity | undefined;
  color: string;
  failure: Why | undefined;
  tierNow: string | undefined;
  selected: boolean;
  burst: boolean;
  glitch: boolean;
  onSelect: (id: number) => void;
  onHover: (id: number | null, element: HTMLElement | null) => void;
}) {
  const { step } = placed;
  const node = activity?.node ?? null;
  const skipped = step.status === "done" && step.note?.startsWith("Skipped by the user");
  const footer =
    step.status === "running"
      ? activity?.lastAction ?? "thinking…"
      : skipped
        ? "skipped by you, not checked"
        : step.status === "done"
          ? step.attempts > 1
            ? `passed on attempt ${step.attempts}`
            : "passed its check"
          : step.status === "failed"
            ? failure?.short ?? step.note ?? "check failed"
            : step.verify
              ? `check: ${step.verify}`
              : "waiting";
  const glyph = skipped ? "⤼" : GLYPH[step.status] ?? "○";
  return (
    <div
      role="button"
      tabIndex={0}
      className={`live-node ${step.status}${skipped ? " skipped" : ""}${selected ? " selected" : ""}${glitch ? " fresh" : ""}`}
      style={{ left: placed.x, top: placed.y, width: NODE_W, height: NODE_H }}
      onClick={() => onSelect(step.id)}
      onKeyDown={(event) => event.key === "Enter" && onSelect(step.id)}
      onMouseEnter={(event) => onHover(step.id, event.currentTarget)}
      onMouseLeave={() => onHover(null, null)}
    >
      <div className="head">
        <span className="num">#{step.id}</span>
        <TierMeter tier={tierNow ?? step.tier} planned={step.tier} />
        {siblings.length > 0 && (
          <span
            className="with"
            title={`Runs at the same time as ${siblings.map((id) => `step ${id}`).join(" and ")}, on another machine (parallel group "${step.parallelGroup}")`}
          >
            ⇉ with {siblings.map((id) => `#${id}`).join(" ")}
          </span>
        )}
        {step.status === "running" ? <span className="glyph"><span className="ring" /></span> : <span className="glyph">{glyph}</span>}
      </div>
      <div className="name">{step.title}</div>
      <div className="foot">
        {node && (step.status === "running" || step.status === "done") && !skipped && (
          <span className="live-chip" style={{ ["--m" as string]: color }}>{node}</span>
        )}
        <span className="act" title={failure && step.status === "failed" ? `${failure.short}\n\n${failure.hint}` : footer}>
          {footer}
        </span>
        {(step.attempts > 1 || (step.status === "failed" && step.attempts > 0)) && (
          <span className="tries" title={`${step.attempts} of 3 attempts used`}>
            ↻ {step.attempts}/3
          </span>
        )}
      </div>
      {burst && <span className="burst" />}
    </div>
  );
});

function Pipeline({
  steps,
  stages,
  activity,
  colorOf,
  selected,
  onSelect,
  onHover,
  reducedMotion,
  failures,
  tiers,
}: {
  steps: PlanStep[];
  stages: Stage[];
  activity: Map<number, StepActivity>;
  colorOf: (name: string | null | undefined) => string;
  failures: Map<number, Why>;
  tiers: Map<number, string>;
  selected: number | null;
  onSelect: (id: number) => void;
  onHover: (id: number | null, element: HTMLElement | null) => void;
  reducedMotion: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 900, height: 400 });
  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => setSize({ width: entry.contentRect.width, height: entry.contentRect.height }));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // A narrow pane (VS Code's browser beside the code) stacks the steps and scrolls rather than shrinking them to fit.
  const vertical = size.width < 760 || size.height > size.width * 1.15;
  const graph = useMemo(() => layout(stages, vertical), [stages, vertical]);
  // The steps each step runs alongside, worked out once per plan so the step cards are not redrawn for nothing.
  const siblingsOf = useMemo(
    () => new Map(stages.flatMap((stage) => stage.map((step) => [step.id, stage.filter((other) => other.id !== step.id).map((other) => other.id)] as const))),
    [stages],
  );
  const fitWidth = (size.width - 8) / graph.width;
  const scale = vertical
    ? Math.max(0.5, Math.min(1, fitWidth))
    : Math.max(0.5, Math.min(1.15, fitWidth, (size.height - 8) / graph.height));
  const { burst, glitch } = useTransitions(steps);
  const edgeClass = (edge: Edge) =>
    edge.to.step.status === "running" && edge.from.step.status === "done"
      ? "hot"
      : edge.to.step.status === "done" || (edge.from.step.status === "done" && edge.to.step.status !== "pending")
        ? "done"
        : "";

  return (
    <div className="live-stage" ref={box}>
      <div style={{ width: graph.width * scale, height: graph.height * scale, margin: "0 auto" }}>
        <div className="live-graph" style={{ width: graph.width, height: graph.height, transform: `scale(${scale})` }}>
          <svg width={graph.width} height={graph.height} aria-hidden="true">
            {graph.edges.map((edge, index) => {
              const kind = edgeClass(edge);
              return (
                <g key={index}>
                  {kind === "hot" && <path className="live-edge-glow" d={edge.d} />}
                  <path className={`live-edge ${kind}`} d={edge.d} />
                </g>
              );
            })}
          </svg>
          {!reducedMotion &&
            graph.edges
              .filter((edge) => edgeClass(edge) === "hot")
              .map((edge, index) => <Packet key={`${edge.from.step.id}-${edge.to.step.id}`} d={edge.d} delay={index * 0.35} />)}
          {graph.placed.map((placed) => (
            <StepNode
              key={placed.step.id}
              placed={placed}
              siblings={siblingsOf.get(placed.step.id) ?? NO_SIBLINGS}
              activity={activity.get(placed.step.id)}
              color={colorOf(activity.get(placed.step.id)?.node)}
              failure={failures.get(placed.step.id)}
              tierNow={tiers.get(placed.step.id)}
              selected={selected === placed.step.id}
              burst={burst.has(placed.step.id)}
              glitch={glitch.has(placed.step.id)}
              onSelect={onSelect}
              onHover={onHover}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

function Machines({
  nodes,
  steps,
  activity,
  log,
  colorOf,
}: {
  nodes: LiveNode[];
  steps: PlanStep[];
  activity: Map<number, StepActivity>;
  log: LogLine[];
  colorOf: (name: string | null | undefined) => string;
}) {
  // Tool calls per machine in twelve ten-second slots: the last two minutes of work, as bars.
  const bars = useMemo(() => {
    const now = Date.now();
    const counts = new Map<string, number[]>();
    for (const line of log) {
      if (line.kind !== "tool-call" || line.step === null || now - line.at > 120_000) continue;
      const node = activity.get(line.step)?.node;
      if (!node) continue;
      const slots = counts.get(node) ?? new Array(12).fill(0);
      slots[Math.min(11, Math.floor((now - line.at) / 10_000))] += 1;
      counts.set(node, slots);
    }
    return counts;
  }, [log, activity]);

  return (
    <div className="live-machines">
      {nodes.map((node) => {
        const working = steps.filter((step) => step.status === "running" && activity.get(step.id)?.node === node.name);
        const slots = (bars.get(node.name) ?? new Array(12).fill(0)).slice().reverse();
        const peak = Math.max(1, ...slots);
        return (
          <div
            key={node.name}
            className={`live-machine${working.length ? " busy" : ""}${node.ready ? "" : " down"}`}
            style={{ ["--m" as string]: colorOf(node.name) }}
          >
            <div className="live-core">
              <i />
              <i />
              <b />
            </div>
            <div className="who">
              <b>{node.name}</b>
              <small>{node.model}</small>
            </div>
            <div className="doing">
              {!node.ready ? "not reachable" : working.length ? working.map((step) => `#${step.id} ${step.title}`).join(" · ") : "idle"}
            </div>
            <div className="live-bars" aria-hidden="true">
              {slots.map((count, index) => (
                <i key={index} style={{ transform: `scaleY(${count === 0 ? 0.06 : 0.2 + (0.8 * count) / peak})` }} />
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function Inspector({
  plan,
  step,
  activity,
  log,
  failure,
  tierNow,
}: {
  plan: LivePlan;
  step: PlanStep | null;
  activity: StepActivity | undefined;
  log: LogLine[];
  failure: Why | undefined;
  tierNow: string | undefined;
}) {
  if (!step) {
    return (
      <div className="live-inspector">
        <div className="label">goal</div>
        <div>{plan.goal}</div>
        {plan.workingDirectory && (
          <>
            <div className="label">project</div>
            <code>{plan.workingDirectory}</code>
          </>
        )}
        <div className="label">tip</div>
        <div>Click a step to see its instructions, its check and its last result. Hover one for its latest lines.</div>
      </div>
    );
  }
  const lastCheck = [...(plan.events ?? [])].reverse().find((event) => event.stepId === step.id && (event.kind === "check-failed" || event.kind === "check-passed"));
  const lines = log.filter((line) => line.step === step.id).slice(-12);
  return (
    <div className="live-inspector">
      <h3>
        #{step.id} {step.title}
      </h3>
      <div>
        <span className="live-glow-text">{step.status}</span> · <TierMeter tier={tierNow ?? step.tier} planned={step.tier} /> tier · {step.attempts} of 3 attempts
        {activity?.node ? ` · on ${activity.node}` : ""} · {activity?.toolCalls ?? 0} tool calls
      </div>
      {step.status === "failed" && failure && (
        <>
          <div className="label">why it stopped</div>
          <div className="live-why">
            <b>{failure.short}</b>
            <span>{failure.hint}</span>
          </div>
        </>
      )}
      {step.files.length > 0 && (
        <>
          <div className="label">files</div>
          <div>{step.files.map((file) => <code key={file} style={{ display: "block" }}>{file}</code>)}</div>
        </>
      )}
      {step.verify && (
        <>
          <div className="label">check</div>
          <code>{step.verify}</code>
        </>
      )}
      {lastCheck && (
        <>
          <div className="label">last check ({lastCheck.kind === "check-passed" ? "passed" : "failed"})</div>
          <pre>{lastCheck.detail || "(no output)"}</pre>
        </>
      )}
      <div className="label">instructions</div>
      <pre>{step.detail}</pre>
      {lines.length > 0 && (
        <>
          <div className="label">latest</div>
          <pre>{lines.map((line) => `${hhmmss(line.at)}  ${line.text}`).join("\n")}</pre>
        </>
      )}
    </div>
  );
}

function HoverCard({ step, anchor, log, activity }: { step: PlanStep; anchor: DOMRect; log: LogLine[]; activity: StepActivity | undefined }) {
  const lines = log.filter((line) => line.step === step.id).slice(-8);
  const width = 380;
  const left = anchor.right + 12 + width < window.innerWidth ? anchor.right + 12 : Math.max(10, anchor.left - width - 12);
  const top = Math.min(anchor.top, window.innerHeight - 260);
  return (
    <div className="live-hover" style={{ left, top }}>
      <div style={{ color: "var(--fg)", marginBottom: 6 }}>
        #{step.id} {step.title}
      </div>
      <div style={{ color: "var(--muted)", fontSize: 11, marginBottom: 6 }}>
        {step.status} · {activity?.node ?? "no machine yet"} · {activity?.toolCalls ?? 0} tool calls
      </div>
      {lines.length === 0 ? (
        <div style={{ color: "var(--muted)" }}>Nothing yet.</div>
      ) : (
        lines.map((line) => (
          <div key={line.key} className="line">
            <span className="t">{hhmmss(line.at)}</span>
            <span className="x" style={{ color: line.tone === "bad" ? "var(--red)" : line.tone === "good" ? "var(--green)" : undefined }}>
              {line.text}
            </span>
          </div>
        ))
      )}
    </div>
  );
}

export function LogPane({
  log,
  steps,
  filter,
  setFilter,
  colorOf,
  live,
}: {
  log: LogLine[];
  steps: PlanStep[];
  filter: number | "errors" | null;
  setFilter: (value: number | "errors" | null) => void;
  colorOf: (name: string | null | undefined) => string;
  live: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const seen = useRef<Set<string> | null>(null);
  const shown = useMemo(() => {
    const filtered =
      filter === null ? log : filter === "errors" ? log.filter((line) => line.tone === "bad" || line.tone === "warn") : log.filter((line) => line.step === filter);
    return filtered.slice(-400);
  }, [log, filter]);

  // Lines that arrived since the last render slide in; the first screenful does not.
  const fresh = useMemo(() => {
    const result = new Set<string>();
    if (seen.current === null) {
      seen.current = new Set(log.map((line) => line.key));
      return result;
    }
    for (const line of log) {
      if (!seen.current.has(line.key)) {
        result.add(line.key);
        seen.current.add(line.key);
      }
    }
    return result;
  }, [log]);

  useLayoutEffect(() => {
    const element = box.current;
    if (element && pinned.current) element.scrollTop = element.scrollHeight;
  }, [shown]);

  return (
    <section className="live-tile live-logtile">
      <header>
        <b>journalctl</b> -f -u fleet
        <span className="spacer" />
        <span className="live-filter">
          <button type="button" className={filter === null ? "on" : ""} onClick={() => setFilter(null)}>
            all
          </button>
          {steps.map((step) => (
            <button key={step.id} type="button" className={filter === step.id ? "on" : ""} onClick={() => setFilter(filter === step.id ? null : step.id)}>
              #{step.id}
            </button>
          ))}
          <button type="button" className={filter === "errors" ? "on" : ""} onClick={() => setFilter(filter === "errors" ? null : "errors")}>
            errors
          </button>
        </span>
      </header>
      <div
        className="live-log"
        ref={box}
        onScroll={(event) => {
          const element = event.currentTarget;
          pinned.current = element.scrollHeight - element.scrollTop - element.clientHeight < 24;
        }}
      >
        {shown.map((line) => (
          <div key={line.key} className={`line ${line.tone}${fresh.has(line.key) ? " fresh" : ""}`} title={line.text}>
            <span className="t">{hhmmss(line.at)}</span>
            <span className="n" style={{ color: colorOf(line.node) }}>{line.node ?? "fleet"}</span>
            <span className="s">{line.step === null ? "" : `#${line.step}`}</span>
            <span className="x">{line.text}</span>
          </div>
        ))}
        <div>
          <span className="prompt">❯ </span>
          {live ? <span className="cursor" /> : <span style={{ color: "var(--muted)" }}>the plan has stopped changing</span>}
        </div>
      </div>
    </section>
  );
}

// ---------- what needs the user ----------

/**
 * The plan's state when it needs the user: waiting for approval, blocked at a step (why, and what to do), stopped, or
 * rejected. Nothing while it runs or once it is done.
 */
function PlanBanner({ plan, onInspect }: { plan: LivePlan; onInspect: (id: number) => void }) {
  const [acting, act] = usePlanAction(plan.id);
  const block = useMemo(() => blockOf(plan), [plan]);
  if (plan.id === "demo") return null;

  if (plan.status === "awaiting-approval") {
    return (
      <div className="live-banner accent">
        <div className="head">
          <span className="icon">◆</span>
          <b>WAITING FOR YOUR APPROVAL</b>
          <span className="muted">{plan.steps.length} steps · nothing has changed yet</span>
        </div>
        <div className="hint">Read the steps below (click one for its instructions and check). Approving starts the run on your machines.</div>
        <div className="actions">
          <button type="button" className="primary" disabled={acting} onClick={() => act("approve")}>
            ▶ approve and run
          </button>
          <button type="button" disabled={acting} onClick={() => act("reject")}>
            ✕ reject
          </button>
        </div>
      </div>
    );
  }

  if (plan.status === "rejected") {
    return (
      <div className="live-banner dim">
        <div className="head">
          <span className="icon">✕</span>
          <b>REJECTED</b>
          <span className="muted">this plan will not run</span>
        </div>
      </div>
    );
  }

  if (!block) return null;
  const { step, stopped, why, tiers } = block;
  const endless = why.short.includes("never finishes");
  return (
    <div className={`live-banner ${stopped ? "warn" : "bad"}`}>
      <div className="head">
        <span className="icon">{stopped ? "■" : "⚠"}</span>
        <b>{stopped ? "STOPPED" : "BLOCKED"}</b>
        {step && (
          <span>
            at{" "}
            <button type="button" className="link" onClick={() => onInspect(step.id)}>
              #{step.id} {step.title}
            </button>
          </span>
        )}
        {!stopped && step && (
          <span className="muted">
            {step.attempts > 0 ? `${step.attempts} of 3 attempts` : "no attempt spent"}{tiers.length > 0 ? ` · ${tiers.join(" → ")}` : ""}
          </span>
        )}
      </div>
      <div className="why">{why.short}</div>
      <div className="hint">{why.hint}</div>
      <div className="actions">
        {stopped ? (
          <button type="button" className="primary" disabled={acting} onClick={() => act("approve")}>
            ▶ resume
          </button>
        ) : (
          <>
            {!endless && (
              <button type="button" className="primary" disabled={acting} onClick={() => act("approve")} title="Three fresh attempts at this step, then the rest of the plan">
                ↻ retry step {step?.id}
              </button>
            )}
            {step && (
              <button type="button" className={endless ? "primary" : ""} disabled={acting} onClick={() => act("skip", step)} title="Count this step as done without its check, and go on">
                ⤼ skip step {step.id} and go on
              </button>
            )}
          </>
        )}
        {step && (
          <button type="button" disabled={acting} onClick={() => onInspect(step.id)}>
            ▤ details
          </button>
        )}
      </div>
    </div>
  );
}

export type LiveCurrent =
  | { kind: "plan"; planId: string; status: string; title: string; contextId: string | null }
  | { kind: "planning"; contextId: string; title: string; ended: boolean }
  | { kind: "none" };

/**
 * Something newer in this plan's conversation (a plan being worked out, or a newer plan), checked every ten seconds
 * while this plan is not running, so a page left open on an old plan shows what came next.
 */
function useNewer(plan: LivePlan | null, following: boolean): LiveCurrent | null {
  const [newer, setNewer] = useState<LiveCurrent | null>(null);
  const contextId = plan?.contextId ?? null;
  const planId = plan?.id ?? null;
  const quiet = !!plan && plan.status !== "running" && plan.status !== "approved";
  useEffect(() => {
    if (following || !contextId || !quiet) {
      setNewer(null);
      return;
    }
    let stopped = false;
    const check = () => {
      if (document.hidden) return;
      fetch(`/api/live/current?context=${contextId}`, { cache: "no-store" })
        .then((res) => (res.ok ? res.json() : null))
        .then((current: LiveCurrent | null) => {
          if (stopped || !current) return;
          const differs = current.kind === "planning" ? !current.ended : current.kind === "plan" && current.planId !== planId;
          setNewer(differs ? current : null);
        })
        .catch(() => {});
    };
    check();
    const timer = setInterval(check, 10_000);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [contextId, planId, quiet, following]);
  return newer;
}

// ---------- the page ----------

/**
 * A plan as it runs: the pipeline, the machines doing the work and the log, updated every two seconds. following: the
 * page is /live, which moves on by itself to whatever the fleet does next.
 */
export function LiveRun({ planId, following = false }: { planId: string; following?: boolean }) {
  const { plan: fed, nodes, activity, log, error, connected } = useLiveFeed(planId);
  const plan = useMemo(() => (fed ? withRunAttempts(fed) : null), [fed]);
  const colorOf = useMachineColors(nodes);
  const reducedMotion = useReducedMotion();
  const [selected, setSelected] = useState<number | null>(null);
  const [filter, setFilter] = useState<number | "errors" | null>(null);
  const [hover, setHover] = useState<{ id: number; rect: DOMRect } | null>(null);
  const [hidden, setHidden] = useState(false);
  const newer = useNewer(plan, following);

  useEffect(() => {
    const onVisibility = () => setHidden(document.hidden);
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  useEffect(() => {
    if (plan) document.title = `${plan.status === "running" ? "● " : ""}${plan.title} · Fleet live`;
  }, [plan]);

  const steps = useMemo(() => plan?.steps ?? [], [plan?.steps]);
  const stages = useMemo(() => stagesOf(steps), [steps]);
  const failures = useMemo(() => (plan ? stepFailures(plan) : new Map<number, Why>()), [plan]);
  const tiers = useMemo(() => stepTiers(plan?.events), [plan?.events]);
  const busyNodes = useMemo(
    () => new Set(steps.filter((step) => step.status === "running").map((step) => activity.get(step.id)?.node).filter((name): name is string => !!name)),
    [steps, activity],
  );

  if (!plan) {
    return (
      <div className="live-root">
        <div className="live-empty" style={{ gridRow: "1 / -1" }}>
          <span className="live-glow-text">{error ?? "connecting to the fleet…"}</span>
        </div>
      </div>
    );
  }

  const selectedStep = steps.find((step) => step.id === selected) ?? null;
  const hoverStep = hover ? steps.find((step) => step.id === hover.id) ?? null : null;
  const live = plan.status === "running" || plan.status === "approved";
  const done = steps.filter((step) => step.status === "done").length;
  const inspect = (id: number) => {
    setSelected(id);
    setFilter(id);
  };

  return (
    <div className={`live-root${hidden ? " live-paused" : ""}`}>
      <TopBar plan={plan} stages={stages} nodes={nodes} colorOf={colorOf} busyNodes={busyNodes} following={following} />
      <div className="live-progress" aria-hidden="true">
        <div className="fill" style={{ transform: `scaleX(${steps.length ? done / steps.length : 0})` }} />
        {live && !reducedMotion && <div className="shine" />}
      </div>
      <div className="live-main">
        <section className="live-tile focus">
          <header>
            <b>pipeline</b> {stages.length} stages · {steps.length} steps
            <span className="spacer" />
            {!connected && <span style={{ color: "var(--red)" }}>reconnecting…</span>}
            <span>{plan.workingDirectory}</span>
          </header>
          {newer && newer.kind !== "none" && (
            <a className="live-banner info compact" href={newer.kind === "planning" ? `/live?context=${newer.contextId}` : `/live/${newer.planId}`}>
              <span className="icon">◉</span>
              {newer.kind === "planning" ? (
                <span>a new plan is being worked out in this conversation</span>
              ) : (
                <span>
                  newer in this conversation: <b>{newer.title}</b> ({newer.status.replace("-", " ")})
                </span>
              )}
              <span className="go">watch ▸</span>
            </a>
          )}
          <PlanBanner plan={plan} onInspect={inspect} />
          <Pipeline
            steps={steps}
            stages={stages}
            activity={activity}
            colorOf={colorOf}
            failures={failures}
            tiers={tiers}
            selected={selected}
            onSelect={(id) => {
              setSelected((current) => (current === id ? null : id));
              setFilter((current) => (current === id ? null : id));
            }}
            onHover={(id, element) => setHover(id === null || !element ? null : { id, rect: element.getBoundingClientRect() })}
            reducedMotion={reducedMotion}
          />
        </section>
        <div className="live-side">
          <section className="live-tile">
            <header>
              <b>machines</b>
              <span className="spacer" />
              {busyNodes.size} busy
            </header>
            <Machines nodes={nodes} steps={steps} activity={activity} log={log} colorOf={colorOf} />
          </section>
          <section className="live-tile">
            <header>
              <b>{selectedStep ? `step #${selectedStep.id}` : "plan"}</b>
              <span className="spacer" />
              {selectedStep && (
                <button type="button" onClick={() => setSelected(null)} style={{ color: "var(--muted)" }}>
                  ✕
                </button>
              )}
            </header>
            <Inspector
              plan={plan}
              step={selectedStep}
              activity={selectedStep ? activity.get(selectedStep.id) : undefined}
              log={log}
              failure={selectedStep ? failures.get(selectedStep.id) : undefined}
              tierNow={selectedStep ? tiers.get(selectedStep.id) : undefined}
            />
          </section>
        </div>
      </div>
      <LogPane log={log} steps={steps} filter={filter} setFilter={setFilter} colorOf={colorOf} live={live} />
      {hover && hoverStep && <HoverCard step={hoverStep} anchor={hover.rect} log={log} activity={activity.get(hover.id)} />}
    </div>
  );
}
