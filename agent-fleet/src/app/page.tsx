"use client";

import { useEffect, useRef, useState } from "react";
import { CopilotSidebar } from "@copilotkit/react-core/v2";
import { Fragment } from "react";
import { AttachmentMenuButton } from "@/components/AttachmentMenuButton";
import { ToolRenderers } from "@/components/ToolRenderers";
import { SessionPanel } from "@/components/SessionPanel";
import { PlansPanel } from "@/components/PlansPanel";
import { ContextPanel } from "@/components/ContextPanel";
import { ConfigPanel } from "@/components/ConfigPanel";
import { SetupBanner } from "@/components/SetupBanner";

type FleetMode = "conservative" | "aggressive";

// Small hand-rolled renderer for the usage guide's markdown subset (headings,
// bullet lists, blockquotes, paragraphs, **bold**, `code`) - avoids pulling in
// react-markdown, whose current version's types don't support React 19.
function renderInline(text: string, keyPrefix: string) {
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g);
  return parts.map((part, i) => {
    if (part.startsWith("**") && part.endsWith("**")) {
      return <strong key={`${keyPrefix}-${i}`}>{part.slice(2, -2)}</strong>;
    }
    if (part.startsWith("`") && part.endsWith("`")) {
      return <code key={`${keyPrefix}-${i}`}>{part.slice(1, -1)}</code>;
    }
    return <Fragment key={`${keyPrefix}-${i}`}>{part}</Fragment>;
  });
}

function renderMarkdown(markdown: string) {
  const blocks = markdown.trim().split(/\n\s*\n/);
  return blocks.map((block, blockIndex) => {
    const key = `block-${blockIndex}`;
    const lines = block.split("\n");

    if (lines[0].startsWith("# ")) {
      return <h1 key={key}>{renderInline(lines[0].slice(2), key)}</h1>;
    }
    if (lines[0].startsWith("## ")) {
      return <h2 key={key}>{renderInline(lines[0].slice(3), key)}</h2>;
    }
    // Groups wrapped continuation lines (no marker) into the item/quote line
    // above them, so hand-wrapped markdown source still renders as one item.
    function groupContinuations(marker: string): string[] | null {
      if (!lines[0].startsWith(marker)) return null;
      const items: string[] = [];
      for (const line of lines) {
        if (line.startsWith(marker)) {
          items.push(line.slice(marker.length));
        } else if (line.trim() !== "" && items.length > 0) {
          items[items.length - 1] += " " + line.trim();
        }
      }
      return items;
    }

    const listItems = groupContinuations("- ");
    if (listItems) {
      return (
        <ul key={key}>
          {listItems.map((item, i) => (
            <li key={`${key}-${i}`}>{renderInline(item, `${key}-${i}`)}</li>
          ))}
        </ul>
      );
    }

    const quoteLines = groupContinuations("> ");
    if (quoteLines) {
      return (
        <blockquote key={key}>
          {quoteLines.map((line, i) => (
            <Fragment key={`${key}-${i}`}>
              {renderInline(line, `${key}-${i}`)}
              <br />
            </Fragment>
          ))}
        </blockquote>
      );
    }
    return <p key={key}>{renderInline(block, key)}</p>;
  });
}

function HelpPanel() {
  const [open, setOpen] = useState(false);
  const [guide, setGuide] = useState<string | null>(null);

  useEffect(() => {
    if (open && guide === null) {
      fetch("/api/usage-guide")
        .then((res) => res.text())
        .then(setGuide)
        .catch(() => setGuide("Could not load the usage guide."));
    }
  }, [open, guide]);

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="How to use Agent Fleet"
        className="absolute top-4 left-4 rounded-full w-9 h-9 flex items-center justify-center text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
      >
        ?
      </button>

      {open && (
        <div
          className="fixed inset-y-0 left-0 right-[420px] z-50 flex items-center justify-center bg-black/60 p-6"
          onClick={() => setOpen(false)}
        >
          <div
            className="bg-neutral-900 text-neutral-100 rounded-lg max-w-xl w-full max-h-[80vh] overflow-y-auto p-8 usage-guide"
            onClick={(e) => e.stopPropagation()}
          >
            <button
              type="button"
              onClick={() => setOpen(false)}
              className="float-right text-neutral-400 hover:text-neutral-100"
            >
              Close
            </button>
            {guide === null ? (
              <p>Loading...</p>
            ) : (
              renderMarkdown(guide)
            )}
          </div>
        </div>
      )}
    </>
  );
}

type LogTail = { file: string | null; lines: string[] };

function LogPanel() {
  const [open, setOpen] = useState(false);
  const [tail, setTail] = useState<LogTail | null>(null);
  const preRef = useRef<HTMLPreElement>(null);

  useEffect(() => {
    if (!open) return;
    function load() {
      fetch("/api/logs?lines=150")
        .then((res) => res.json())
        .then(setTail)
        .catch(() => {});
    }
    load();
    const interval = setInterval(load, 3000);
    return () => clearInterval(interval);
  }, [open]);

  useEffect(() => {
    if (preRef.current) {
      preRef.current.scrollTop = preRef.current.scrollHeight;
    }
  }, [tail]);

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="View the backend's request log"
        className="absolute top-4 left-16 rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
      >
        Logs
      </button>

      {open && (
        <div
          className="fixed inset-y-0 left-0 right-[420px] z-50 flex items-center justify-center bg-black/60 p-6"
          onClick={() => setOpen(false)}
        >
          <div
            className="bg-neutral-900 text-neutral-100 rounded-lg max-w-3xl w-full h-[80vh] p-6 flex flex-col"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between mb-3 shrink-0">
              <h2 className="text-sm font-medium text-neutral-300">
                Backend log{tail?.file ? ` — ${tail.file}` : ""}
              </h2>
              <button
                type="button"
                onClick={() => setOpen(false)}
                className="text-neutral-400 hover:text-neutral-100"
              >
                Close
              </button>
            </div>
            <pre
              ref={preRef}
              className="text-xs font-mono text-neutral-300 whitespace-pre-wrap overflow-y-auto flex-1 bg-neutral-950 rounded p-3 border border-neutral-800"
            >
              {tail === null
                ? "Loading..."
                : tail.lines.length === 0
                  ? "No log entries yet."
                  : tail.lines.join("\n")}
            </pre>
          </div>
        </div>
      )}
    </>
  );
}

function FleetModeToggle() {
  const [mode, setMode] = useState<FleetMode | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    fetch("/api/fleet-mode")
      .then((res) => res.json())
      .then((data) => setMode(data.mode))
      .catch(() => setMode(null));
  }, []);

  async function toggle() {
    if (!mode || pending) return;
    const next: FleetMode = mode === "conservative" ? "aggressive" : "conservative";
    setPending(true);
    try {
      const res = await fetch("/api/fleet-mode", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ mode: next }),
      });
      const data = await res.json();
      setMode(data.mode);
    } finally {
      setPending(false);
    }
  }

  const isAggressive = mode === "aggressive";

  return (
    <button
      type="button"
      onClick={toggle}
      disabled={mode === null || pending}
      title="Switch how much of the workload the hub's GPU takes on"
      className={`absolute top-4 right-4 rounded-full px-4 py-2 text-sm font-medium transition-colors disabled:opacity-50 ${
        isAggressive
          ? "bg-amber-500/20 text-amber-300 border border-amber-500/40"
          : "bg-neutral-800 text-neutral-300 border border-neutral-700"
      }`}
    >
      {mode === null
        ? "Loading..."
        : isAggressive
          ? "Hub mode: Aggressive (night)"
          : "Hub mode: Conservative (day)"}
    </button>
  );
}

function PlanModeToggle() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    fetch("/api/plan-mode")
      .then((res) => res.json())
      .then((data) => setEnabled(data.enabled))
      .catch(() => setEnabled(null));
  }, []);

  async function toggle() {
    if (enabled === null || pending) return;
    setPending(true);
    try {
      const res = await fetch("/api/plan-mode", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ enabled: !enabled }),
      });
      const data = await res.json();
      setEnabled(data.enabled);
    } finally {
      setPending(false);
    }
  }

  return (
    <button
      type="button"
      onClick={toggle}
      disabled={enabled === null || pending}
      title="When on, the agent proposes a plan and waits for you to reply CONFIRM before writing files or running code"
      className={`absolute top-16 right-4 rounded-full px-4 py-2 text-sm font-medium transition-colors disabled:opacity-50 ${
        enabled
          ? "bg-sky-500/20 text-sky-300 border border-sky-500/40"
          : "bg-neutral-800 text-neutral-300 border border-neutral-700"
      }`}
    >
      {enabled === null ? "Loading..." : enabled ? "Plan mode: On" : "Plan mode: Off"}
    </button>
  );
}

type FleetStatus = {
  mode: FleetMode;
  planMode: boolean;
  nodes: { name: string; model: string; ready: boolean; reachable: boolean }[];
  recentActivity: { timestampUtc: string; node: string; reason: string }[];
};

function StatusDot({ ready }: { ready: boolean }) {
  return (
    <span
      className={`inline-block w-2 h-2 rounded-full ${ready ? "bg-emerald-500" : "bg-red-500"}`}
    />
  );
}

function StatusPanel() {
  const [status, setStatus] = useState<FleetStatus | null>(null);

  useEffect(() => {
    function load() {
      fetch("/api/fleet-status")
        .then((res) => res.json())
        .then(setStatus)
        .catch(() => {});
    }
    load();
    const interval = setInterval(load, 4000);
    return () => clearInterval(interval);
  }, []);

  return (
    <div className="w-full max-w-md space-y-6 text-left">
      <div className="text-center space-y-2">
        <h1 className="text-2xl font-semibold">Agent Fleet</h1>
        <p className="text-neutral-400">
          {status && status.nodes.length > 0
            ? `Coding agents routed across ${status.nodes.map((node) => node.name).join(", ")}.`
            : "Coding agents routed across your machines."}
        </p>
      </div>

      <SetupBanner />

      <SessionPanel />

      {status && (
        <>
          <div className="space-y-2">
            <h2 className="text-xs uppercase tracking-wide text-neutral-500">Nodes</h2>
            {status.nodes.map((node) => (
              <div
                key={node.name}
                className="flex items-center justify-between bg-neutral-900 border border-neutral-800 rounded-md px-3 py-2 text-sm"
              >
                <span className="flex items-center gap-2">
                  <StatusDot ready={node.ready} />
                  <span className="font-medium capitalize">{node.name}</span>
                </span>
                <span className="text-neutral-500">{node.model}</span>
              </div>
            ))}
          </div>

          <div className="space-y-2">
            <h2 className="text-xs uppercase tracking-wide text-neutral-500">Recent activity</h2>
            {status.recentActivity.length === 0 ? (
              <p className="text-sm text-neutral-600">Nothing yet.</p>
            ) : (
              <div className="space-y-1 max-h-64 overflow-y-auto">
                {status.recentActivity.map((entry, i) => (
                  <div key={i} className="text-xs text-neutral-500 flex justify-between gap-2">
                    <span className="capitalize text-neutral-300">{entry.node}</span>
                    <span className="truncate">{entry.reason}</span>
                    <span>
                      {new Date(entry.timestampUtc).toLocaleTimeString()}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}

export default function AgentFleetPage() {
  return (
    <main className="h-screen flex items-center justify-center bg-neutral-950 text-neutral-100 relative">
      <ToolRenderers />
      <HelpPanel />
      <LogPanel />
      <ConfigPanel />
      <PlansPanel />
      <ContextPanel />
      <FleetModeToggle />
      <PlanModeToggle />
      <StatusPanel />
      <CopilotSidebar
        defaultOpen={true}
        attachments={{ enabled: true, accept: "image/*" }}
        input={{ addMenuButton: AttachmentMenuButton }}
        labels={{
          modalHeaderTitle: "Agent Fleet",
          welcomeMessageText:
            "Hi! Describe a coding task and I'll route it to the right agent.",
        }}
      />
    </main>
  );
}
