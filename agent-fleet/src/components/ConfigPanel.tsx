"use client";

import { useCallback, useEffect, useState } from "react";
import type { McpServerConfig } from "./mcpCatalog";
import { HistorySettings } from "./HistorySettings";
import { SandboxSettings } from "./SandboxSettings";
import { SetupSettings } from "./SetupSettings";
import { ToolsSettings, type McpStatus, type ToolConfig } from "./ToolsSettings";
import type { CommandToolConfig, CommandToolStatus } from "./CommandTools";
import { CopyCommand, DownloadButton, announceFleetChanged, type HubInfo } from "./setupApi";

type FleetConfigNode = {
  name: string;
  url: string;
  model: string;
  purpose: string;
  tier: string | null;
  vision: boolean;
  fallback: boolean;
};

type FleetConfigData = {
  triageModel: string;
  nodes: FleetConfigNode[];
  tools: Record<string, ToolConfig>;
  mcpServers: Record<string, McpServerConfig>;
  mcpStatus: McpStatus[];
  history?: { deleteAfterDays: number };
  customTools?: Record<string, CommandToolConfig>;
  customStatus?: CommandToolStatus[];
};

type NodeStatus = { name: string; ready: boolean; reachable: boolean };
type FeedbackCount = { node: string; up: number; down: number };
type FetchedModel = { name: string; sizeBytes: number };

// One dropdown covers both fields the backend stores: vision nodes have no tier.
const NODE_ROLES: { value: string; label: string; hint: string }[] = [
  { value: "heavy", label: "Heavy", hint: "hard work, planning" },
  { value: "standard", label: "Standard", hint: "ordinary coding, plan steps" },
  { value: "light", label: "Light", hint: "quick questions, trivial edits" },
  { value: "vision", label: "Vision", hint: "reads images and screenshots" },
];

const NAME_PATTERN = /^[a-z0-9][a-z0-9-]{0,31}$/;

function roleOf(node: FleetConfigNode): string {
  return node.vision ? "vision" : (node.tier ?? "standard");
}

function withRole(node: FleetConfigNode, role: string): FleetConfigNode {
  // A vision node cannot be the fallback: it cannot serve text-only work with tools.
  return role === "vision" ? { ...node, vision: true, tier: null, fallback: false } : { ...node, vision: false, tier: role };
}

function formatSize(bytes: number): string {
  if (bytes <= 0) return "";
  const gb = bytes / 1024 / 1024 / 1024;
  return gb >= 0.1 ? `${gb.toFixed(1)} GB` : `${Math.round(bytes / 1024 / 1024)} MB`;
}

/** "192.168.1.21", "pc2:11434" or "http://pc2:11434" all become "http://host:11434/v1". */
export function normalizeNodeAddress(raw: string): string {
  let value = raw.trim();
  if (!value) return "";
  if (!/^https?:\/\//i.test(value)) value = `http://${value}`;
  try {
    const url = new URL(value);
    if (!url.port && url.protocol === "http:") url.port = "11434";
    const path = url.pathname.replace(/\/+$/, "");
    url.pathname = path.endsWith("/v1") ? path : `${path}/v1`;
    return url.toString().replace(/\/$/, "");
  } catch {
    return value;
  }
}

async function fetchModels(url: string): Promise<{ models: FetchedModel[] } | { error: string }> {
  try {
    const res = await fetch(`/api/fleet-config/models?url=${encodeURIComponent(url)}`);
    const data = await res.json();
    return res.ok ? { models: data.models ?? [] } : { error: data.error ?? "Could not reach that machine." };
  } catch {
    return { error: "Could not reach the backend." };
  }
}

function ModelChips({ models, current, onPick }: { models: FetchedModel[]; current: string; onPick: (name: string) => void }) {
  if (models.length === 0) {
    return <p className="text-[11px] text-amber-300">Ollama answers but has no models installed. On that machine: ollama pull &lt;model&gt;</p>;
  }
  return (
    <div className="flex flex-wrap gap-1.5">
      {models.map((m) => (
        <button
          key={m.name}
          type="button"
          onClick={() => onPick(m.name)}
          className={`text-[11px] px-2 py-0.5 rounded-full border font-mono transition-colors ${
            m.name === current || `${current}:latest` === m.name
              ? "bg-emerald-600/20 text-emerald-300 border-emerald-600/40"
              : "bg-neutral-900 text-neutral-400 border-neutral-700 hover:bg-neutral-700"
          }`}
        >
          {m.name}
          {formatSize(m.sizeBytes) && <span className="text-neutral-500"> · {formatSize(m.sizeBytes)}</span>}
        </button>
      ))}
    </div>
  );
}

const unreachableHint =
  "Check that Ollama is running on that machine and listening on the network (OLLAMA_HOST=0.0.0.0), and that its firewall lets this machine in. scripts/Setup-Worker.ps1 (Windows) or scripts/setup-worker.sh (Linux) set both up.";

/** How to get another computer ready: the worker scripts with this hub's address filled in, or the same by hand. */
function PrepareComputer() {
  const [open, setOpen] = useState(false);
  const [hub, setHub] = useState<HubInfo | null>(null);

  useEffect(() => {
    if (open && !hub) {
      fetch("/api/setup/hub")
        .then((res) => res.json())
        .then(setHub)
        .catch(() => {});
    }
  }, [open, hub]);

  const address = hub?.addresses[0] ?? "THIS-COMPUTERS-IP";
  return (
    <details className="text-[11px] text-neutral-400" open={open} onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary className="cursor-pointer text-sky-400">How do I get another computer ready?</summary>
      <div className="mt-2 space-y-2">
        <p>
          The other computer needs Ollama, listening on the network, with its firewall letting only this computer in. Copy Agent Fleet to it
          (the same download, or a clone; only the scripts folder is used), then run the line for its system in that folder.
          {hub && hub.addresses.length > 1 && ` This computer has several addresses (${hub.addresses.join(", ")}); use the one on the same network as that computer.`}
        </p>
        <p className="text-neutral-300">Windows, in PowerShell as administrator:</p>
        <CopyCommand command={`.\\scripts\\Setup-Worker.ps1 -AllowFrom ${address} -KeepAwake`} />
        <p className="text-neutral-300">Linux:</p>
        <CopyCommand command={`sudo bash scripts/setup-worker.sh --allow-from ${address}`} />
        <details>
          <summary className="cursor-pointer text-neutral-400">Without Agent Fleet on that computer (Windows, by hand)</summary>
          <div className="mt-1 space-y-1">
            <p>Install Ollama from ollama.com, then in PowerShell as administrator:</p>
            <CopyCommand
              command={`[Environment]::SetEnvironmentVariable('OLLAMA_HOST', '0.0.0.0', 'Machine')\nNew-NetFirewallRule -DisplayName 'Ollama from the Agent Fleet hub' -Direction Inbound -Protocol TCP -LocalPort 11434 -RemoteAddress ${address} -Action Allow`}
            />
            <p>
              Then quit Ollama from the notification area and start it again. Ollama&apos;s installer may also have added its own rule that lets every
              computer in; the script narrows that one too.
            </p>
          </div>
        </details>
        <p>
          Then type that computer&apos;s address above and press Connect. Models can be downloaded onto it from here: nothing else needs doing
          on it. Ollama has no password, so never forward its port on your router.
        </p>
      </div>
    </details>
  );
}

function AddMachine({ nodes, onAdd, saving }: { nodes: FleetConfigNode[]; onAdd: (node: FleetConfigNode) => Promise<boolean>; saving: boolean }) {
  const [address, setAddress] = useState("");
  const [connecting, setConnecting] = useState(false);
  const [models, setModels] = useState<FetchedModel[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [model, setModel] = useState("");
  const [role, setRole] = useState("standard");
  const [name, setName] = useState("");
  const [wanted, setWanted] = useState("qwen2.5-coder:7b");

  const url = normalizeNodeAddress(address);

  function suggestName(): string {
    let n = nodes.length;
    while (nodes.some((node) => node.name === `worker${n}`)) n++;
    return `worker${n}`;
  }

  async function connect() {
    if (!url) return;
    setConnecting(true);
    setError(null);
    setModels(null);
    const result = await fetchModels(url);
    setConnecting(false);
    if ("error" in result) {
      setError(result.error);
      return;
    }
    setModels(result.models);
    setModel(result.models[0]?.name ?? "");
    if (!name) setName(suggestName());
  }

  async function add() {
    const trimmed = name.trim();
    if (!NAME_PATTERN.test(trimmed)) return setError("Name: 1 to 32 lowercase letters, digits or hyphens, for example worker1.");
    if (nodes.some((node) => node.name === trimmed)) return setError(`There is already a machine called ${trimmed}.`);
    if (!model) return setError("Pick the model this machine should serve.");
    if (nodes.some((node) => node.url === url && node.model === model && roleOf(node) === role)) {
      return setError("That machine already serves this model in this role.");
    }
    const hint = NODE_ROLES.find((r) => r.value === role)?.hint ?? "";
    const ok = await onAdd(withRole({ name: trimmed, url, model, purpose: hint, tier: "standard", vision: false, fallback: false }, role));
    if (ok) {
      setAddress("");
      setModels(null);
      setModel("");
      setName("");
      setError(null);
    }
  }

  return (
    <div className="border border-neutral-700 rounded-md p-3 space-y-2">
      <h4 className="text-xs text-neutral-300">Add a machine</h4>
      <p className="text-[11px] text-neutral-500">
        Any computer on your network running Ollama. Type its address: an IP or name is enough, the port and /v1 are added.
      </p>
      <div className="flex gap-2">
        <input
          value={address}
          onChange={(e) => {
            setAddress(e.target.value);
            setModels(null);
            setError(null);
          }}
          onKeyDown={(e) => e.key === "Enter" && connect()}
          placeholder="192.168.1.21"
          className="flex-1 text-xs px-2 py-1.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
        />
        <button
          type="button"
          onClick={connect}
          disabled={!url || connecting}
          className="text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-50"
        >
          {connecting ? "Connecting..." : "Connect"}
        </button>
      </div>
      {url && address.trim() !== url && <p className="text-[11px] text-neutral-600 font-mono">{url}</p>}

      {models && (
        <div className="space-y-2">
          <p className="text-[11px] text-emerald-400">
            Connected. {models.length > 0 ? "Pick the model this machine should serve, or download another:" : "It has no models yet. Download one onto it:"}
          </p>
          {models.length > 0 && <ModelChips models={models} current={model} onPick={setModel} />}
          <div className="flex flex-wrap items-center gap-2">
            <input
              value={wanted}
              onChange={(e) => setWanted(e.target.value.trim())}
              className="w-48 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
              title="Any model from ollama.com/library"
            />
            <DownloadButton
              compact
              url={url}
              model={wanted}
              label="Download onto it"
              onDone={async () => {
                const result = await fetchModels(url);
                if (!("error" in result)) {
                  setModels(result.models);
                  setModel(result.models.find((m) => m.name === wanted || m.name === `${wanted}:latest`)?.name ?? wanted);
                }
              }}
            />
          </div>
          <div className="flex flex-wrap gap-3 items-end">
            <div>
              <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Name</label>
              <input
                value={name}
                onChange={(e) => setName(e.target.value.toLowerCase())}
                className="w-32 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
              />
            </div>
            <div>
              <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Role</label>
              <select
                value={role}
                onChange={(e) => setRole(e.target.value)}
                className="text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200"
              >
                {NODE_ROLES.map((r) => (
                  <option key={r.value} value={r.value}>
                    {r.label} - {r.hint}
                  </option>
                ))}
              </select>
            </div>
            <button
              type="button"
              onClick={add}
              disabled={saving || !model}
              className="text-xs px-3 py-1.5 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-50"
            >
              {saving ? "Saving..." : "Add and save"}
            </button>
          </div>
        </div>
      )}
      {error && (
        <div className="text-[11px] text-red-300 space-y-0.5">
          <p>{error}</p>
          {models === null && !error.startsWith("Name") && <p className="text-neutral-500">{unreachableHint}</p>}
        </div>
      )}
      <PrepareComputer />
    </div>
  );
}

function MachineCard({
  node,
  index,
  status,
  feedback,
  canRemove,
  onChange,
  onRemove,
  onMakeFallback,
}: {
  node: FleetConfigNode;
  index: number;
  status?: NodeStatus;
  feedback?: FeedbackCount;
  canRemove: boolean;
  onChange: (node: FleetConfigNode) => void;
  onRemove: () => void;
  onMakeFallback: () => void;
}) {
  const [models, setModels] = useState<FetchedModel[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [open, setOpen] = useState(false);

  async function check() {
    setChecking(true);
    setError(null);
    const result = await fetchModels(node.url);
    setChecking(false);
    if ("error" in result) setError(result.error);
    else setModels(result.models);
  }

  const dot = !status ? "bg-neutral-600" : status.ready ? "bg-emerald-400" : status.reachable ? "bg-amber-400" : "bg-red-500";
  const dotTitle = !status
    ? "not checked yet (new or just saved)"
    : status.ready
      ? "ready"
      : status.reachable
        ? "reachable, but the model is not installed there"
        : "not reachable";

  return (
    <div className="bg-neutral-800/60 border border-neutral-700 rounded-md p-3 space-y-2">
      <div className="flex items-center gap-2">
        <span className={`w-2 h-2 rounded-full shrink-0 ${dot}`} title={dotTitle} />
        <input
          value={node.name}
          onChange={(e) => onChange({ ...node, name: e.target.value.toLowerCase() })}
          aria-label="Name"
          className="w-32 text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-100 font-mono"
        />
        <select
          value={roleOf(node)}
          onChange={(e) => onChange(withRole(node, e.target.value))}
          aria-label="Role"
          className="text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-200"
        >
          {NODE_ROLES.map((role) => (
            <option key={role.value} value={role.value}>
              {role.label} - {role.hint}
            </option>
          ))}
        </select>
        <span className="text-xs font-mono text-neutral-400 truncate" title={node.model}>
          {node.model || "(no model)"}
        </span>
        {feedback && feedback.up + feedback.down > 0 && (
          <span className="text-[11px] text-neutral-500 whitespace-nowrap" title="Thumbs up and down on this machine's answers in the chat">
            👍 {feedback.up} · 👎 {feedback.down}
          </span>
        )}
        <label
          className={`ml-auto flex items-center gap-1 text-[11px] text-neutral-400 ${node.vision ? "opacity-40" : ""}`}
          title="Answers when routing fails, finishes tool rounds, and stands in when another machine is down. Routing runs on this machine."
        >
          <input type="radio" name="fallback-node" checked={node.fallback} disabled={node.vision} onChange={onMakeFallback} />
          fallback
        </label>
        <button type="button" onClick={() => setOpen((v) => !v)} className="text-[11px] text-sky-400 hover:underline">
          {open ? "Less" : "Edit"}
        </button>
        <button
          type="button"
          onClick={onRemove}
          disabled={!canRemove}
          title={node.fallback ? "Make another machine the fallback first" : canRemove ? "Remove this machine" : "A fleet needs at least one machine"}
          className="text-[11px] text-red-400 hover:text-red-300 disabled:opacity-30"
        >
          Remove
        </button>
      </div>
      {status && !status.ready && (
        <div className="space-y-1">
          <p className="text-[11px] text-amber-300">
            {status.reachable ? `Reachable, but ${node.model} is not downloaded there yet. Download it, or pick an installed model under Edit.` : unreachableHint}
          </p>
          {status.reachable && <DownloadButton compact url={node.url} model={node.model} />}
        </div>
      )}
      {open && (
        <div className="space-y-2 pt-1">
          <div>
            <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Address</label>
            <div className="flex gap-2">
              <input
                value={node.url}
                onChange={(e) => onChange({ ...node, url: e.target.value })}
                onBlur={() => onChange({ ...node, url: normalizeNodeAddress(node.url) })}
                placeholder="http://host:11434/v1"
                className="flex-1 text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-300 font-mono"
              />
              <button
                type="button"
                onClick={check}
                disabled={checking}
                className="shrink-0 text-xs px-2 py-1 rounded bg-neutral-700 text-neutral-200 border border-neutral-600 hover:bg-neutral-600 disabled:opacity-50"
              >
                {checking ? "Checking..." : "Check and list models"}
              </button>
            </div>
            {error && <p className="text-[11px] text-red-400 mt-1">{error}</p>}
          </div>
          {models && <ModelChips models={models} current={node.model} onPick={(model) => onChange({ ...node, model })} />}
          <div className="flex gap-2">
            <div className="flex-1">
              <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Model</label>
              <input
                value={node.model}
                onChange={(e) => onChange({ ...node, model: e.target.value })}
                className="w-full text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-300 font-mono"
              />
            </div>
            <div className="flex-1">
              <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Note (for you)</label>
              <input
                value={node.purpose}
                onChange={(e) => onChange({ ...node, purpose: e.target.value })}
                className="w-full text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-300"
              />
            </div>
          </div>
          <p className="text-[10px] text-neutral-600">Machine {index + 1}</p>
        </div>
      )}
    </div>
  );
}

type Tab = "setup" | "machines" | "tools" | "sandbox" | "routing" | "history";

/** Fleet configuration: machines, tools and routing. Everything applies when saved; nothing needs a restart. */
export function ConfigPanel() {
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState<Tab>("setup");
  const [draft, setDraft] = useState<FleetConfigData | null>(null);
  const [saved, setSaved] = useState<string>("");
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [statuses, setStatuses] = useState<NodeStatus[]>([]);
  const [feedback, setFeedback] = useState<FeedbackCount[]>([]);
  const [triageModels, setTriageModels] = useState<FetchedModel[] | null>(null);

  const snapshot = (data: FleetConfigData) => JSON.stringify({ nodes: data.nodes, triageModel: data.triageModel });

  // Anything on the page can open the panel on a tab (the first-run banner, a check's button).
  useEffect(() => {
    function onOpen(event: Event) {
      const wantedTab = (event as CustomEvent<{ tab?: Tab }>).detail?.tab;
      setOpen(true);
      if (wantedTab) setTab(wantedTab);
      setDraft(null);
    }
    window.addEventListener("fleet:open-config", onOpen);
    return () => window.removeEventListener("fleet:open-config", onOpen);
  }, []);

  useEffect(() => {
    if (open && draft === null) {
      fetch("/api/fleet-config")
        .then((res) => res.json())
        .then((data: FleetConfigData) => {
          setDraft(data);
          setSaved(snapshot(data));
        })
        .catch(() => setMessage({ text: "Could not load the configuration from the backend.", error: true }));
    }
  }, [open, draft]);

  const loadStatus = useCallback(() => {
    fetch("/api/fleet-status", { cache: "no-store" })
      .then((res) => res.json())
      .then((data: { nodes?: NodeStatus[] }) => setStatuses(data.nodes ?? []))
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (!open) return;
    loadStatus();
    const timer = setInterval(loadStatus, 5000);
    return () => clearInterval(timer);
  }, [open, loadStatus]);

  useEffect(() => {
    if (!open) return;
    fetch("/api/feedback", { cache: "no-store" })
      .then((res) => (res.ok ? res.json() : []))
      .then(setFeedback)
      .catch(() => {});
  }, [open]);

  async function save(next: FleetConfigData): Promise<boolean> {
    setSaving(true);
    setMessage(null);
    try {
      const res = await fetch("/api/fleet-config", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          triageModel: next.triageModel,
          nodes: next.nodes,
          tools: Object.fromEntries(Object.entries(next.tools).map(([name, tool]) => [name, tool.enabled])),
          mcpServers: next.mcpServers,
          // Left out when unknown, so the backend keeps what it has.
          customTools: next.customTools,
        }),
      });
      const data = await res.json();
      if (!res.ok) {
        setMessage({ text: data.error ?? "Save failed.", error: true });
        return false;
      }
      const merged: FleetConfigData = {
        triageModel: data.triageModel,
        nodes: data.nodes,
        tools: data.tools,
        mcpServers: data.mcpServers,
        mcpStatus: data.mcpStatus,
        history: data.history,
        customTools: data.customTools,
        customStatus: data.customStatus,
      };
      setDraft(merged);
      setSaved(snapshot(merged));
      setMessage({ text: data.message ?? "Saved.", error: false });
      setTimeout(loadStatus, 1500);
      announceFleetChanged();
      return true;
    } catch {
      setMessage({ text: "Could not reach the backend.", error: true });
      return false;
    } finally {
      setSaving(false);
    }
  }

  const dirty = draft !== null && snapshot(draft) !== saved;
  const fallback = draft?.nodes.find((node) => node.fallback);

  function setNode(index: number, node: FleetConfigNode) {
    if (!draft) return;
    setDraft({ ...draft, nodes: draft.nodes.map((n, i) => (i === index ? node : n)) });
  }

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        title="Machines, models, tools and routing"
        className="absolute top-4 left-40 rounded-full px-4 py-2 text-sm font-medium bg-neutral-800 text-neutral-300 border border-neutral-700 hover:bg-neutral-700 transition-colors"
      >
        Config
      </button>

      {open && (
        <div className="fixed inset-y-0 left-0 right-[420px] z-50 flex items-center justify-center bg-black/60 p-6" onClick={() => setOpen(false)}>
          <div className="bg-neutral-900 text-neutral-100 rounded-lg max-w-3xl w-full max-h-[88vh] flex flex-col" onClick={(e) => e.stopPropagation()}>
            <div className="flex items-center gap-4 px-6 pt-5 border-b border-neutral-800">
              <h2 className="text-sm font-medium text-neutral-300 pb-3">Fleet configuration</h2>
              <div className="flex text-xs">
                {([
                  ["setup", "Setup"],
                  ["machines", `Machines${draft ? ` (${draft.nodes.length})` : ""}`],
                  ["tools", "Tools"],
                  ["sandbox", "Sandbox"],
                  ["routing", "Routing"],
                  ["history", "History"],
                ] as const).map(([key, label]) => (
                  <button
                    key={key}
                    type="button"
                    onClick={() => setTab(key)}
                    className={`px-3 pb-3 ${tab === key ? "text-neutral-100 border-b-2 border-sky-400" : "text-neutral-500 hover:text-neutral-300"}`}
                  >
                    {label}
                  </button>
                ))}
              </div>
              <button type="button" onClick={() => setOpen(false)} className="ml-auto pb-3 text-neutral-400 hover:text-neutral-100">
                Close
              </button>
            </div>

            <div className="flex-1 overflow-y-auto px-6 py-4">
              {tab === "sandbox" ? (
                <SandboxSettings />
              ) : draft === null ? (
                <p className="text-sm text-neutral-500">{message?.text ?? "Loading..."}</p>
              ) : tab === "setup" ? (
                <SetupSettings
                  nodes={draft.nodes}
                  onOpenTab={(next) => setTab(next as Tab)}
                  onUseModel={(nodeName, model) =>
                    save({ ...draft, nodes: draft.nodes.map((node) => (node.name === nodeName ? { ...node, model } : node)) })
                  }
                />
              ) : tab === "machines" ? (
                <div className="space-y-3">
                  <p className="text-xs text-neutral-500">
                    Each machine runs Ollama with one model and has a role. The router sends hard work to heavy machines,
                    ordinary work to standard ones, and quick questions to light ones. Changes apply when saved.
                  </p>
                  {draft.nodes.map((node, i) => (
                    <MachineCard
                      key={i}
                      node={node}
                      index={i}
                      status={statuses.find((s) => s.name === node.name)}
                      feedback={feedback.find((f) => f.node === node.name)}
                      canRemove={draft.nodes.length > 1 && !node.fallback}
                      onChange={(next) => setNode(i, next)}
                      onRemove={() => setDraft({ ...draft, nodes: draft.nodes.filter((_, j) => j !== i) })}
                      onMakeFallback={() => setDraft({ ...draft, nodes: draft.nodes.map((n, j) => ({ ...n, fallback: j === i })) })}
                    />
                  ))}
                  <AddMachine nodes={draft.nodes} saving={saving} onAdd={(node) => save({ ...draft, nodes: [...draft.nodes, node] })} />
                </div>
              ) : tab === "tools" ? (
                <ToolsSettings
                  servers={draft.mcpServers ?? {}}
                  statuses={draft.mcpStatus ?? []}
                  tools={draft.tools}
                  customTools={draft.customTools ?? {}}
                  customStatus={draft.customStatus ?? []}
                  saving={saving}
                  onSave={(mcpServers, tools) => save({ ...draft, mcpServers, tools })}
                  onSaveCustom={(customTools) => save({ ...draft, customTools })}
                  onReload={() => setDraft(null)}
                />
              ) : tab === "history" ? (
                <HistorySettings
                  deleteAfterDays={draft.history?.deleteAfterDays ?? 0}
                  onSaved={(deleteAfterDays, text) => {
                    setDraft({ ...draft, history: { deleteAfterDays } });
                    setMessage({ text, error: false });
                  }}
                />
              ) : (
                <div className="space-y-4">
                  <div>
                    <label className="block text-xs uppercase tracking-wide text-neutral-500 mb-1">Routing model</label>
                    <p className="text-xs text-neutral-500 mb-2">
                      A small, fast model that reads each message and picks which machine should answer. It runs on the
                      fallback machine{fallback ? ` (${fallback.name})` : ""}, so it must be installed there. With only one
                      text machine it is not used at all.
                    </p>
                    <div className="flex gap-2">
                      <input
                        value={draft.triageModel}
                        onChange={(e) => setDraft({ ...draft, triageModel: e.target.value })}
                        className="flex-1 text-sm px-3 py-1.5 rounded-md bg-neutral-800 border border-neutral-700 text-neutral-200 font-mono"
                      />
                      {fallback && (
                        <button
                          type="button"
                          onClick={async () => {
                            const result = await fetchModels(fallback.url);
                            setTriageModels("error" in result ? [] : result.models);
                          }}
                          className="shrink-0 text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600"
                        >
                          Models on {fallback.name}
                        </button>
                      )}
                    </div>
                    {triageModels && (
                      <div className="mt-2">
                        <ModelChips models={triageModels} current={draft.triageModel} onPick={(m) => setDraft({ ...draft, triageModel: m })} />
                      </div>
                    )}
                    <p className="text-[11px] text-neutral-600 mt-2">A 1B to 3B model is plenty, for example llama3.2:3b or qwen2.5:1.5b.</p>
                    {fallback && draft.triageModel.trim() && (
                      <div className="mt-2">
                        <DownloadButton compact url={fallback.url} model={draft.triageModel.trim()} label={`Download ${draft.triageModel.trim()} onto ${fallback.name}`} />
                      </div>
                    )}
                  </div>
                  <p className="text-xs text-neutral-500">
                    <strong className="text-neutral-400">Hub mode</strong> (top right) decides how freely the heavy machine is used,
                    and <strong className="text-neutral-400">Plan mode</strong> makes the fleet plan before it changes anything.
                  </p>
                </div>
              )}
            </div>

            <div className="flex items-center gap-3 px-6 py-3 border-t border-neutral-800">
              {(tab === "machines" || tab === "routing") && (
                <button
                  type="button"
                  onClick={() => draft && save(draft)}
                  disabled={saving || !dirty}
                  className="rounded-full px-4 py-1.5 text-sm font-medium bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 transition-colors disabled:opacity-40"
                >
                  {saving ? "Saving..." : dirty ? "Save" : "Saved"}
                </button>
              )}
              {message ? (
                <span className={`text-xs ${message.error ? "text-red-300" : "text-neutral-400"}`}>{message.text}</span>
              ) : (
                <span className="text-xs text-neutral-600">Everything here applies as soon as it is saved. No restart.</span>
              )}
              {dirty && !(tab === "machines" || tab === "routing") && <span className="ml-auto text-[11px] text-amber-300">Unsaved machine changes: see the Machines tab.</span>}
            </div>
          </div>
        </div>
      )}
    </>
  );
}
