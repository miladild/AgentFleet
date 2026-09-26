"use client";

import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { PlanStep } from "../PlanCard";
import { useLiveFeed, type LiveNode, type LivePlan, type LogLine, type StepActivity } from "./useLiveFeed";

// ---------- colours and helpers ----------

const PALETTE = ["#bb9af7", "#7dcfff", "#9ece6a", "#ff9e64", "#e0af68", "#2ac3de", "#ff4fa3", "#73daca"];

/** Each machine keeps one colour everywhere: its card, its badge on a step and its name in the log. */
function useMachineColors(nodes: LiveNode[]) {
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
const TIER_LABEL: Record<string, string> = { heavy: "HEAVY", standard: "STD", light: "LIGHT" };

function hhmmss(ms: number) {
  return new Date(ms).toLocaleTimeString([], { hour12: false });
}

function duration(ms: number) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

function useReducedMotion() {
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

function Clock({ from, until }: { from: number | null; until: number | null }) {
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
}: {
  plan: LivePlan;
  stages: Stage[];
  nodes: LiveNode[];
  colorOf: (name: string | null | undefined) => string;
  busyNodes: Set<string>;
}) {
  const [acting, setActing] = useState(false);
  const done = plan.steps.filter((step) => step.status === "done").length;
  const started = plan.approvedUtc ? Date.parse(plan.approvedUtc) : null;
  const finished = plan.status === "done" || plan.status === "blocked" || plan.status === "rejected" ? Date.parse(plan.updatedUtc) : null;

  async function act(action: "stop" | "approve") {
    if (action === "stop" && !window.confirm("Stop this plan? Finished steps stay done; you can resume it later.")) return;
    setActing(true);
    try {
      await fetch(`/api/plans/${plan.id}/${action}`, { method: "POST" });
    } finally {
      setActing(false);
    }
  }

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
      {plan.id !== "demo" && plan.status === "blocked" && (
        <button type="button" className="mod" disabled={acting} onClick={() => act("approve")}>
          ▶ approve and resume
        </button>
      )}
      <a className="mod hide-narrow" href="/live?pick" title="Other plans">≡</a>
    </div>
  );
}

const StepNode = memo(function StepNode({
  placed,
  activity,
  color,
  selected,
  burst,
  glitch,
  onSelect,
  onHover,
}: {
  placed: Placed;
  activity: StepActivity | undefined;
  color: string;
  selected: boolean;
  burst: boolean;
  glitch: boolean;
  onSelect: (id: number) => void;
  onHover: (id: number | null, element: HTMLElement | null) => void;
}) {
  const { step } = placed;
  const node = activity?.node ?? null;
  const footer =
    step.status === "running"
      ? activity?.lastAction ?? "thinking…"
      : step.status === "done"
        ? `passed · ${step.attempts} attempt${step.attempts === 1 ? "" : "s"}`
        : step.status === "failed"
          ? step.note ?? "check failed"
          : step.verify
            ? `check: ${step.verify}`
            : "waiting";
  return (
    <div
      role="button"
      tabIndex={0}
      className={`live-node ${step.status}${selected ? " selected" : ""}${glitch ? " fresh" : ""}`}
      style={{ left: placed.x, top: placed.y, width: NODE_W, height: NODE_H }}
      onClick={() => onSelect(step.id)}
      onKeyDown={(event) => event.key === "Enter" && onSelect(step.id)}
      onMouseEnter={(event) => onHover(step.id, event.currentTarget)}
      onMouseLeave={() => onHover(null, null)}
    >
      <div className="head">
        <span className="num">#{step.id}</span>
        <span className={`tier ${step.tier}`}>{TIER_LABEL[step.tier] ?? step.tier}</span>
        {step.parallelGroup && <span title="Parallel group">⫘ {step.parallelGroup}</span>}
        {step.status === "running" ? <span className="glyph"><span className="ring" /></span> : <span className="glyph">{GLYPH[step.status] ?? "○"}</span>}
      </div>
      <div className="name">{step.title}</div>
      <div className="foot">
        {node && (step.status === "running" || step.status === "done") && (
          <span className="live-chip" style={{ ["--m" as string]: color }}>{node}</span>
        )}
        <span className="act" title={footer}>{footer}</span>
        <span className="tries" title={`${step.attempts} of 3 attempts used`}>
          {[0, 1, 2].map((index) => (
            <i key={index} className={index < step.attempts ? "used" : ""} />
          ))}
        </span>
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
}: {
  steps: PlanStep[];
  stages: Stage[];
  activity: Map<number, StepActivity>;
  colorOf: (name: string | null | undefined) => string;
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

  const vertical = size.width < 640 || size.height > size.width * 1.15;
  const graph = useMemo(() => layout(stages, vertical), [stages, vertical]);
  const scale = Math.max(0.5, Math.min(1.15, (size.width - 8) / graph.width, (size.height - 8) / graph.height));
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
              activity={activity.get(placed.step.id)}
              color={colorOf(activity.get(placed.step.id)?.node)}
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

function Inspector({ plan, step, activity, log }: { plan: LivePlan; step: PlanStep | null; activity: StepActivity | undefined; log: LogLine[] }) {
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
        <span className={`live-glow-text`}>{step.status}</span> · {step.tier} · {step.attempts} of 3 attempts
        {activity?.node ? ` · ${activity.node}` : ""} · {activity?.toolCalls ?? 0} tool calls
      </div>
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

function LogPane({
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

// ---------- the page ----------

/** A plan as it runs: the pipeline, the machines doing the work and the log, updated every two seconds. */
export function LiveRun({ planId }: { planId: string }) {
  const { plan, nodes, activity, log, error, connected } = useLiveFeed(planId);
  const colorOf = useMachineColors(nodes);
  const reducedMotion = useReducedMotion();
  const [selected, setSelected] = useState<number | null>(null);
  const [filter, setFilter] = useState<number | "errors" | null>(null);
  const [hover, setHover] = useState<{ id: number; rect: DOMRect } | null>(null);
  const [hidden, setHidden] = useState(false);

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

  return (
    <div className={`live-root${hidden ? " live-paused" : ""}`}>
      <TopBar plan={plan} stages={stages} nodes={nodes} colorOf={colorOf} busyNodes={busyNodes} />
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
          <Pipeline
            steps={steps}
            stages={stages}
            activity={activity}
            colorOf={colorOf}
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
            <Inspector plan={plan} step={selectedStep} activity={selectedStep ? activity.get(selectedStep.id) : undefined} log={log} />
          </section>
        </div>
      </div>
      <LogPane log={log} steps={steps} filter={filter} setFilter={setFilter} colorOf={colorOf} live={live} />
      {hover && hoverStep && <HoverCard step={hoverStep} anchor={hover.rect} log={log} activity={activity.get(hover.id)} />}
    </div>
  );
}
