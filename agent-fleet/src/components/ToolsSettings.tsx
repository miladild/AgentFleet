"use client";

import { useEffect, useState } from "react";
import { CommandTools, type CommandToolConfig, type CommandToolStatus } from "./CommandTools";
import { McpImportPanel } from "./McpImportPanel";
import { ToolTry, type ToolParameter } from "./ToolTry";
import {
  CATALOG_CATEGORIES,
  MCP_CATALOG,
  type CatalogEntry,
  type McpServerConfig,
  catalogPlaceholders,
  describeServer,
  fillPlaceholders,
  joinCommandLine,
  looksLikeInlineSecret,
  parsePastedServers,
  splitCommandLine,
} from "./mcpCatalog";

export type ToolConfig = { enabled: boolean; description: string; source: string; parameters?: ToolParameter[] };

type Prerequisites = { npx: boolean; uvx: boolean; docker: boolean; git: boolean; os: string };

function missingHint(requires: "npx" | "uvx", os: string): string {
  if (requires === "npx") return "Node.js is not installed on the hub (or not on the backend's PATH): install it from nodejs.org first.";
  return os === "windows"
    ? "uv is not installed on the hub: in PowerShell, winget install astral-sh.uv, then restart the fleet."
    : "uv is not installed on the hub: curl -LsSf https://astral.sh/uv/install.sh | sh, then restart the fleet.";
}
export type McpStatus = { name: string; type: string; enabled: boolean; connected: boolean; toolCount: number; error: string | null };

type TestResult = { ok: boolean; tools: { name: string; description: string; readOnly: boolean }[]; error: string | null };

type Props = {
  servers: Record<string, McpServerConfig>;
  statuses: McpStatus[];
  tools: Record<string, ToolConfig>;
  customTools: Record<string, CommandToolConfig>;
  customStatus: CommandToolStatus[];
  saving: boolean;
  /** Save with these servers and tool switches; tools and MCP servers apply immediately on the backend. */
  onSave: (servers: Record<string, McpServerConfig>, tools: Record<string, ToolConfig>) => Promise<boolean>;
  onSaveCustom: (customTools: Record<string, CommandToolConfig>) => Promise<boolean>;
  /** The backend changed the config itself (an import): load it again. */
  onReload: () => void;
};

type Pair = { key: string; value: string };
type Form = {
  editing: string | null;
  name: string;
  type: "stdio" | "http";
  commandLine: string;
  url: string;
  env: Pair[];
  headers: Pair[];
  setup: string | null;
  /** Values for {placeholders} in the command or address, such as the folder a server may use. */
  fills: Record<string, string>;
};

const EMPTY_FORM: Form = { editing: null, name: "", type: "stdio", commandLine: "", url: "", env: [], headers: [], setup: null, fills: {} };
const NAME_PATTERN = /^[a-z0-9][a-z0-9-]{0,31}$/;

const toPairs = (map?: Record<string, string> | null): Pair[] => Object.entries(map ?? {}).map(([key, value]) => ({ key, value }));
const toMap = (pairs: Pair[]): Record<string, string> | null => {
  const entries = pairs.filter((p) => p.key.trim()).map((p) => [p.key.trim(), p.value] as const);
  return entries.length ? Object.fromEntries(entries) : null;
};

function formToServer(form: Form): McpServerConfig {
  return form.type === "http"
    ? { type: "http", url: fillPlaceholders(form.url.trim(), form.fills), headers: toMap(form.headers), env: null, enabled: true }
    : { type: "stdio", ...splitCommandLine(fillPlaceholders(form.commandLine.trim(), form.fills)), env: toMap(form.env), enabled: true };
}

function formPlaceholders(form: Form): string[] {
  return catalogPlaceholders(form.type === "http" ? form.url : form.commandLine);
}

function serverToForm(name: string, server: McpServerConfig, setup: string | null = null, editing: string | null = null): Form {
  return {
    editing,
    name,
    type: server.type === "http" ? "http" : "stdio",
    commandLine: server.type === "http" ? "" : joinCommandLine(server),
    url: server.url ?? "",
    env: toPairs(server.env),
    headers: toPairs(server.headers),
    setup,
    fills: {},
  };
}

function PairsEditor({ label, hint, pairs, onChange }: { label: string; hint: string; pairs: Pair[]; onChange: (pairs: Pair[]) => void }) {
  return (
    <div>
      <div className="flex items-center justify-between">
        <span className="text-[10px] uppercase tracking-wide text-neutral-500">{label}</span>
        <button type="button" onClick={() => onChange([...pairs, { key: "", value: "" }])} className="text-[11px] text-sky-400 hover:underline">
          + add
        </button>
      </div>
      {pairs.length === 0 && <p className="text-[11px] text-neutral-600">{hint}</p>}
      {pairs.map((pair, i) => (
        <div key={i} className="mt-1">
          <div className="flex gap-1">
            <input
              value={pair.key}
              onChange={(e) => onChange(pairs.map((p, j) => (j === i ? { ...p, key: e.target.value } : p)))}
              placeholder="NAME"
              className="w-40 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
            />
            <input
              value={pair.value}
              onChange={(e) => onChange(pairs.map((p, j) => (j === i ? { ...p, value: e.target.value } : p)))}
              placeholder="${env:MY_TOKEN}"
              className="flex-1 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
            />
            <button type="button" onClick={() => onChange(pairs.filter((_, j) => j !== i))} className="px-2 text-neutral-500 hover:text-red-400" title="Remove">
              ×
            </button>
          </div>
          {looksLikeInlineSecret(pair.key, pair.value) && (
            <p className="text-[11px] text-amber-300 mt-0.5">
              This looks like a real secret. It would be saved in fleet.config.json in plain text. Put it in an environment variable
              on the hub and write <code>{"${env:NAME}"}</code> here instead.
            </p>
          )}
        </div>
      ))}
    </div>
  );
}

function StatusBadge({ server, status }: { server: McpServerConfig; status?: McpStatus }) {
  if (!server.enabled) return <span className="text-[11px] text-neutral-500">off</span>;
  if (!status) return <span className="text-[11px] text-amber-300">not connected yet, press Save</span>;
  if (status.connected) return <span className="text-[11px] text-emerald-400">connected, {status.toolCount} tools</span>;
  return <span className="text-[11px] text-red-400">failed to start</span>;
}

/** The Tools part of the Config panel: MCP servers (catalog, custom, paste), their tools, and the built-in tools. */
export function ToolsSettings({ servers, statuses, tools, customTools, customStatus, saving, onSave, onSaveCustom, onReload }: Props) {
  const [tab, setTab] = useState<"catalog" | "custom" | "paste" | "import">("catalog");
  const [prerequisites, setPrerequisites] = useState<Prerequisites | null>(null);
  const [category, setCategory] = useState<string>("all");
  const [filter, setFilter] = useState("");
  const [trying, setTrying] = useState<string | null>(null);

  useEffect(() => {
    fetch("/api/tools/prerequisites")
      .then((res) => (res.ok ? res.json() : null))
      .then(setPrerequisites)
      .catch(() => {});
  }, []);
  const [form, setForm] = useState<Form>(EMPTY_FORM);
  const [test, setTest] = useState<TestResult | null>(null);
  const [testing, setTesting] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [pasted, setPasted] = useState("");
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [showBuiltInDetails, setShowBuiltInDetails] = useState(false);

  const serverTools = (name: string) => Object.entries(tools).filter(([, t]) => t.source === name);
  const builtIns = Object.entries(tools).filter(([, t]) => t.source === "built-in");

  function pick(entry: CatalogEntry) {
    let name = entry.id;
    for (let n = 2; servers[name]; n++) name = `${entry.id}-${n}`;
    setForm(serverToForm(name, entry.server, entry.setup ?? null));
    setTest(null);
    setProblem(null);
    setTab("custom");
  }

  function edit(name: string) {
    setForm(serverToForm(name, servers[name], null, name));
    setTest(null);
    setProblem(null);
    setTab("custom");
  }

  function validate(): string | null {
    const name = form.name.trim();
    if (!NAME_PATTERN.test(name)) return "Name: 1 to 32 lowercase letters, digits or hyphens, for example docs or github.";
    if (form.editing !== name && servers[name]) return `There is already a server called ${name}.`;
    if (form.type === "http" && !/^https?:\/\//.test(form.url.trim())) return "Enter the server's address, starting with http:// or https://.";
    if (form.type === "stdio" && !form.commandLine.trim()) return "Enter the command that starts the server, for example npx -y @scope/server.";
    const unfilled = formPlaceholders(form).filter((key) => !form.fills[key]?.trim());
    if (unfilled.length) return `Fill in ${unfilled.join(", ")} first.`;
    return null;
  }

  async function runTest() {
    const error = validate();
    if (error) {
      setProblem(error);
      return;
    }
    setProblem(null);
    setTesting(true);
    setTest(null);
    try {
      const res = await fetch("/api/mcp/test", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ name: form.name.trim(), server: formToServer(form) }),
      });
      const data = await res.json();
      setTest(res.ok ? data : { ok: false, tools: [], error: data.error ?? `HTTP ${res.status}` });
    } catch {
      setTest({ ok: false, tools: [], error: "Could not reach the backend." });
    } finally {
      setTesting(false);
    }
  }

  async function addFromForm() {
    const error = validate();
    if (error) {
      setProblem(error);
      return;
    }
    const name = form.name.trim();
    const next = { ...servers };
    if (form.editing && form.editing !== name) delete next[form.editing];
    next[name] = { ...formToServer(form), enabled: form.editing ? (servers[form.editing]?.enabled ?? true) : true };
    if (await onSave(next, tools)) {
      setForm(EMPTY_FORM);
      setTest(null);
      setExpanded((e) => ({ ...e, [name]: true }));
    }
  }

  async function addPasted() {
    const { servers: found, error } = parsePastedServers(pasted);
    if (error) {
      setProblem(error);
      return;
    }
    const clash = Object.keys(found).filter((name) => servers[name]);
    if (clash.length) {
      setProblem(`Already configured: ${clash.join(", ")}. Remove or rename those first.`);
      return;
    }
    setProblem(null);
    if (await onSave({ ...servers, ...found }, tools)) setPasted("");
  }

  const pastedPreview = pasted.trim() ? parsePastedServers(pasted) : null;

  return (
    <div className="space-y-5">
      <CommandTools tools={customTools} statuses={customStatus} saving={saving} onSave={onSaveCustom} />

      <div className="space-y-2">
        <div className="flex items-baseline justify-between">
          <h3 className="text-xs uppercase tracking-wide text-neutral-500">MCP servers</h3>
          <span className="text-[11px] text-neutral-500">Changes apply when saved. No restart.</span>
        </div>
        <p className="text-xs text-neutral-500">
          Extra tools from MCP servers, shared by this chat, plan runs and <code>@fleet</code> in VS Code.
        </p>

        {Object.keys(servers).length === 0 && <p className="text-xs text-neutral-600">None yet. Pick one below.</p>}
        {Object.entries(servers).map(([name, server]) => {
          const status = statuses.find((s) => s.name === name);
          const own = serverTools(name);
          return (
            <div key={name} className="bg-neutral-800/60 border border-neutral-700 rounded-md p-3 space-y-1.5">
              <div className="flex items-center gap-3">
                <span className="font-mono text-sm text-neutral-100">{name}</span>
                <span className="text-[10px] uppercase tracking-wide text-neutral-500">{server.type}</span>
                <StatusBadge server={server} status={status} />
                <div className="ml-auto flex items-center gap-3 text-xs">
                  <button type="button" disabled={saving} onClick={() => onSave({ ...servers, [name]: { ...server, enabled: !server.enabled } }, tools)} className="text-neutral-300 hover:text-white disabled:opacity-40">
                    {server.enabled ? "Turn off" : "Turn on"}
                  </button>
                  <button type="button" onClick={() => edit(name)} className="text-neutral-300 hover:text-white">
                    Edit
                  </button>
                  <button
                    type="button"
                    disabled={saving}
                    onClick={() => {
                      const { [name]: _removed, ...rest } = servers;
                      void onSave(rest, tools);
                    }}
                    className="text-red-400 hover:text-red-300 disabled:opacity-40"
                  >
                    Remove
                  </button>
                </div>
              </div>
              <div className="text-[11px] font-mono text-neutral-500 break-all">{describeServer(server)}</div>
              {status && !status.connected && status.error && (
                <pre className="text-[11px] text-red-300 whitespace-pre-wrap break-words max-h-32 overflow-y-auto bg-neutral-950 rounded p-2">{status.error}</pre>
              )}
              {own.length > 0 && (
                <div>
                  <button type="button" onClick={() => setExpanded((e) => ({ ...e, [name]: !e[name] }))} className="text-[11px] text-sky-400 hover:underline">
                    {expanded[name] ? "Hide" : "Show"} its {own.length} tools ({own.filter(([, t]) => t.enabled).length} on)
                  </button>
                  {expanded[name] && (
                    <div className="mt-1 space-y-1">
                      {own.map(([toolName, tool]) => (
                        <label key={toolName} className="flex items-start gap-2 text-xs text-neutral-300">
                          <input
                            type="checkbox"
                            className="mt-0.5"
                            checked={tool.enabled}
                            disabled={saving}
                            onChange={() => onSave(servers, { ...tools, [toolName]: { ...tool, enabled: !tool.enabled } })}
                          />
                          <span className="min-w-0 flex-1">
                            <span className="font-mono text-neutral-200">{toolName}</span>
                            {tool.enabled && (
                              <button type="button" onClick={(e) => { e.preventDefault(); setTrying(trying === toolName ? null : toolName); }} className="ml-2 text-[11px] text-sky-400 hover:underline">
                                Try
                              </button>
                            )}
                            <span className="text-neutral-500 line-clamp-2" title={tool.description}>{tool.description}</span>
                            {trying === toolName && <ToolTry name={toolName} parameters={tool.parameters ?? []} onClose={() => setTrying(null)} />}
                          </span>
                        </label>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>

      <div className="border border-neutral-700 rounded-md">
        <div className="flex border-b border-neutral-700 text-xs">
          {([
            ["catalog", "Pick from a list"],
            ["import", "From your other apps"],
            ["custom", form.editing ? `Edit ${form.editing}` : "Custom server"],
            ["paste", "Paste mcp.json"],
          ] as const).map(([key, label]) => (
            <button
              key={key}
              type="button"
              onClick={() => {
                setTab(key);
                setProblem(null);
              }}
              className={`px-3 py-2 ${tab === key ? "text-neutral-100 border-b-2 border-sky-400" : "text-neutral-500 hover:text-neutral-300"}`}
            >
              {label}
            </button>
          ))}
        </div>

        <div className="p-3 space-y-3">
          {tab === "catalog" && (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-1.5">
                {["all", ...CATALOG_CATEGORIES].map((name) => (
                  <button
                    key={name}
                    type="button"
                    onClick={() => setCategory(name)}
                    className={`text-[11px] px-2 py-0.5 rounded-full border ${
                      category === name ? "border-sky-500/60 bg-sky-500/10 text-sky-200" : "border-neutral-700 text-neutral-400 hover:bg-neutral-800"
                    }`}
                  >
                    {name === "all" ? "All" : name}
                  </button>
                ))}
                <input
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder="Search"
                  className="ml-auto w-36 text-xs px-2 py-0.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200"
                />
              </div>
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                {MCP_CATALOG.filter(
                  (entry) =>
                    (category === "all" || entry.category === category) &&
                    (!filter.trim() || `${entry.title} ${entry.what}`.toLowerCase().includes(filter.trim().toLowerCase())),
                ).map((entry) => {
                  const already = Object.values(servers).some((s) => describeServer(s) === describeServer(entry.server));
                  const missing = entry.requires && prerequisites && !prerequisites[entry.requires];
                  return (
                    <div key={entry.id} className="bg-neutral-800/50 border border-neutral-800 rounded-md p-3 flex flex-col gap-1">
                      <div className="flex items-center justify-between">
                        <span className="text-sm text-neutral-100">{entry.title}</span>
                        <span className="text-[10px] uppercase tracking-wide text-neutral-500">{entry.category}</span>
                      </div>
                      <p className="text-xs text-neutral-400">{entry.what}</p>
                      <p className="text-[11px] text-neutral-500">Needs: {entry.needs}</p>
                      {missing && <p className="text-[11px] text-amber-300">{missingHint(entry.requires!, prerequisites!.os)}</p>}
                      <button
                        type="button"
                        onClick={() => pick(entry)}
                        disabled={already}
                        className="mt-auto self-start text-xs px-3 py-1 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-40"
                      >
                        {already ? "Added" : "Set up"}
                      </button>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {tab === "import" && <McpImportPanel onImported={onReload} />}

          {tab === "custom" && (
            <div className="space-y-3">
              {form.setup && <p className="text-xs text-amber-200 bg-amber-500/10 border border-amber-500/30 rounded p-2">{form.setup}</p>}
              <div className="flex flex-wrap gap-3">
                <div>
                  <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Name</label>
                  <input
                    value={form.name}
                    onChange={(e) => setForm({ ...form, name: e.target.value.toLowerCase() })}
                    placeholder="docs"
                    className="w-36 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
                  />
                </div>
                <div>
                  <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">How it runs</span>
                  <div className="flex gap-3 text-xs text-neutral-300 pt-1">
                    <label className="flex items-center gap-1">
                      <input type="radio" checked={form.type === "stdio"} onChange={() => setForm({ ...form, type: "stdio" })} />
                      a command on the hub
                    </label>
                    <label className="flex items-center gap-1">
                      <input type="radio" checked={form.type === "http"} onChange={() => setForm({ ...form, type: "http" })} />
                      a web address
                    </label>
                  </div>
                </div>
              </div>

              {form.type === "stdio" ? (
                <div>
                  <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Command</label>
                  <input
                    value={form.commandLine}
                    onChange={(e) => setForm({ ...form, commandLine: e.target.value })}
                    placeholder="npx -y @modelcontextprotocol/server-sequential-thinking"
                    className="w-full text-xs px-2 py-1.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
                  />
                  <p className="text-[11px] text-neutral-600 mt-0.5">Runs on the hub as the backend&apos;s account. Wrap an argument with spaces in quotes.</p>
                </div>
              ) : (
                <div>
                  <label className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Address</label>
                  <input
                    value={form.url}
                    onChange={(e) => setForm({ ...form, url: e.target.value })}
                    placeholder="https://example.com/mcp"
                    className="w-full text-xs px-2 py-1.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
                  />
                </div>
              )}

              {formPlaceholders(form).length > 0 && (
                <div className="space-y-1.5 bg-sky-500/5 border border-sky-500/30 rounded p-2">
                  <span className="block text-[11px] text-sky-200">Fill in:</span>
                  {formPlaceholders(form).map((key) => (
                    <label key={key} className="flex items-center gap-2">
                      <span className="w-28 text-xs font-mono text-neutral-300">{key}</span>
                      <input
                        value={form.fills[key] ?? ""}
                        onChange={(e) => setForm({ ...form, fills: { ...form.fills, [key]: e.target.value } })}
                        placeholder={key === "folder" || key === "repository" ? "C:\\projects\\my-app" : ""}
                        className="flex-1 text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
                      />
                    </label>
                  ))}
                </div>
              )}

              {form.type === "stdio" ? (
                <PairsEditor
                  label="Environment variables"
                  hint="None. Add one if the server needs a key, for example API_KEY = ${env:MY_API_KEY}."
                  pairs={form.env}
                  onChange={(env) => setForm({ ...form, env })}
                />
              ) : (
                <PairsEditor
                  label="Headers"
                  hint="None. Add one if the server needs a token, for example Authorization = Bearer ${env:MY_TOKEN}."
                  pairs={form.headers}
                  onChange={(headers) => setForm({ ...form, headers })}
                />
              )}
              <p className="text-[11px] text-neutral-600">
                <code>{"${env:NAME}"}</code> is replaced with that environment variable on the hub, so secrets stay out of the config file.
              </p>

              <div className="flex items-center gap-2">
                <button type="button" onClick={runTest} disabled={testing} className="text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-50">
                  {testing ? "Starting it..." : "Test"}
                </button>
                <button
                  type="button"
                  onClick={addFromForm}
                  disabled={saving}
                  className="text-xs px-3 py-1.5 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-50"
                >
                  {saving ? "Saving..." : form.editing ? "Save changes" : "Add and save"}
                </button>
                {(form.editing || form.name) && (
                  <button type="button" onClick={() => { setForm(EMPTY_FORM); setTest(null); setProblem(null); }} className="text-xs text-neutral-500 hover:text-neutral-300">
                    Clear
                  </button>
                )}
              </div>

              {test && (
                test.ok ? (
                  <div className="text-xs bg-emerald-500/10 border border-emerald-500/30 rounded p-2">
                    <p className="text-emerald-300">Works: it started and offers {test.tools.length} tools.</p>
                    <ul className="mt-1 space-y-0.5 max-h-40 overflow-y-auto">
                      {test.tools.map((t) => (
                        <li key={t.name} className="text-neutral-400">
                          <span className="font-mono text-neutral-200">{t.name}</span>
                          {t.readOnly && <span className="text-[10px] text-sky-300"> read-only</span>} {t.description.slice(0, 120)}
                        </li>
                      ))}
                    </ul>
                  </div>
                ) : (
                  <div className="text-xs bg-red-500/10 border border-red-500/30 rounded p-2">
                    <p className="text-red-300">It did not start.</p>
                    <pre className="mt-1 whitespace-pre-wrap break-words text-red-200 max-h-40 overflow-y-auto">{test.error}</pre>
                  </div>
                )
              )}
            </div>
          )}

          {tab === "paste" && (
            <div className="space-y-2">
              <p className="text-xs text-neutral-500">
                Paste a server entry from a README, VS Code&apos;s <code>mcp.json</code>, or a Claude or Cursor config. Both
                <code> &quot;servers&quot;</code> and <code>&quot;mcpServers&quot;</code> work.
              </p>
              <textarea
                value={pasted}
                onChange={(e) => setPasted(e.target.value)}
                rows={8}
                placeholder={'{\n  "servers": {\n    "context7": { "type": "http", "url": "https://mcp.context7.com/mcp" }\n  }\n}'}
                className="w-full text-xs px-2 py-1.5 rounded bg-neutral-950 border border-neutral-700 text-neutral-200 font-mono"
              />
              {pastedPreview && !pastedPreview.error && (
                <ul className="text-xs text-neutral-400 space-y-0.5">
                  {Object.entries(pastedPreview.servers).map(([name, server]) => (
                    <li key={name}>
                      <span className="font-mono text-neutral-200">{name}</span> ({server.type}) {describeServer(server)}
                    </li>
                  ))}
                </ul>
              )}
              {pastedPreview?.error && <p className="text-xs text-amber-300">{pastedPreview.error}</p>}
              <button
                type="button"
                onClick={addPasted}
                disabled={saving || !pastedPreview || !!pastedPreview.error}
                className="text-xs px-3 py-1.5 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-40"
              >
                Add and save
              </button>
            </div>
          )}

          {problem && <p className="text-xs text-red-300">{problem}</p>}
        </div>
      </div>

      <div className="space-y-2">
        <div className="flex items-baseline justify-between">
          <h3 className="text-xs uppercase tracking-wide text-neutral-500">Built-in tools</h3>
          <button type="button" onClick={() => setShowBuiltInDetails((v) => !v)} className="text-[11px] text-sky-400 hover:underline">
            {showBuiltInDetails ? "Hide descriptions" : "Show descriptions"}
          </button>
        </div>
        <div className={showBuiltInDetails ? "space-y-1" : "grid grid-cols-2 gap-x-4 gap-y-1"}>
          {builtIns.map(([name, tool]) => (
            <label key={name} className="flex items-start gap-2 text-xs text-neutral-300" title={tool.description}>
              <input
                type="checkbox"
                className="mt-0.5"
                checked={tool.enabled}
                disabled={saving}
                onChange={() => onSave(servers, { ...tools, [name]: { ...tool, enabled: !tool.enabled } })}
              />
              <span className="min-w-0 flex-1">
                <span className="font-mono text-neutral-200">{name}</span>
                {tool.enabled && !["propose_plan", "get_plan", "record_decision", "search_context"].includes(name) && (
                  <button type="button" onClick={(e) => { e.preventDefault(); setTrying(trying === name ? null : name); }} className="ml-2 text-[11px] text-sky-400 hover:underline">
                    Try
                  </button>
                )}
                {showBuiltInDetails && <span className="block text-neutral-500">{tool.description}</span>}
              </span>
            </label>
          ))}
        </div>
        {trying && builtIns.some(([name]) => name === trying) && (
          <ToolTry name={trying} parameters={tools[trying]?.parameters ?? []} onClose={() => setTrying(null)} />
        )}
      </div>
    </div>
  );
}
