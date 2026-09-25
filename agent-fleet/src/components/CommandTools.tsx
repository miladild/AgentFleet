"use client";

import { useState } from "react";
import { ToolOutput, ToolTry } from "./ToolTry";

export type CommandToolParameter = { name: string; description?: string | null; required: boolean };

export type CommandToolConfig = {
  description: string;
  command: string;
  parameters?: CommandToolParameter[] | null;
  workingDirectory?: string | null;
  timeoutSeconds?: number | null;
  readOnly: boolean;
  enabled: boolean;
};

export type CommandToolStatus = { name: string; enabled: boolean; offered: boolean; problem: string | null };

type Form = {
  editing: string | null;
  name: string;
  description: string;
  command: string;
  workingDirectory: string;
  timeoutSeconds: string;
  readOnly: boolean;
  parameters: Record<string, { description: string; required: boolean }>;
};

const EMPTY: Form = { editing: null, name: "", description: "", command: "", workingDirectory: "", timeoutSeconds: "", readOnly: false, parameters: {} };

// Starting points for the commands people most often want as a tool.
const TEMPLATES: { label: string; name: string; tool: CommandToolConfig }[] = [
  {
    label: ".NET tests",
    name: "run_dotnet_tests",
    tool: {
      description: "Runs the .NET tests of a project or solution and reports which failed. Use it after changing C# code.",
      command: "dotnet test {project} --nologo",
      parameters: [{ name: "project", description: "Path to the test project, the solution, or its folder", required: true }],
      readOnly: false,
      enabled: true,
    },
  },
  {
    label: "npm script",
    name: "run_npm_script",
    tool: {
      description: "Runs an npm script from package.json, such as test, build or lint, in a JavaScript or TypeScript project.",
      command: "npm run {script}",
      workingDirectory: "{folder}",
      parameters: [
        { name: "script", description: "The script's name in package.json, for example test", required: true },
        { name: "folder", description: "The project folder, where package.json is", required: true },
      ],
      readOnly: false,
      enabled: true,
    },
  },
  {
    label: "pytest",
    name: "run_pytest",
    tool: {
      description: "Runs Python tests with pytest and reports which failed.",
      command: "python -m pytest {path} -q",
      parameters: [{ name: "path", description: "A test file or folder", required: true }],
      readOnly: false,
      enabled: true,
    },
  },
  {
    label: "Format C#",
    name: "format_dotnet",
    tool: {
      description: "Formats C# code with dotnet format, following the project's .editorconfig.",
      command: "dotnet format {project}",
      parameters: [{ name: "project", description: "Path to the project or solution", required: true }],
      readOnly: false,
      enabled: true,
    },
  },
];

const placeholders = (text: string) => [...new Set([...text.matchAll(/\{([A-Za-z_][A-Za-z0-9_]{0,40})\}/g)].map((m) => m[1]))];

function toForm(name: string, tool: CommandToolConfig, editing: string | null): Form {
  return {
    editing,
    name,
    description: tool.description,
    command: tool.command,
    workingDirectory: tool.workingDirectory ?? "",
    timeoutSeconds: tool.timeoutSeconds ? String(tool.timeoutSeconds) : "",
    readOnly: tool.readOnly,
    parameters: Object.fromEntries((tool.parameters ?? []).map((p) => [p.name, { description: p.description ?? "", required: p.required }])),
  };
}

function toTool(form: Form, enabled: boolean): CommandToolConfig {
  const used = [...new Set([...placeholders(form.command), ...placeholders(form.workingDirectory)])];
  return {
    description: form.description.trim(),
    command: form.command.trim(),
    workingDirectory: form.workingDirectory.trim() || null,
    timeoutSeconds: Number(form.timeoutSeconds) || null,
    readOnly: form.readOnly,
    enabled,
    parameters: used.map((name) => ({
      name,
      description: form.parameters[name]?.description.trim() || null,
      required: form.parameters[name]?.required ?? true,
    })),
  };
}

const input = "text-xs px-2 py-1 rounded bg-neutral-950 border border-neutral-700 text-neutral-200";

/**
 * Tools made from a command, in the Tools tab. The command runs as a program with separate arguments, never through a
 * shell, so what the model fills in is always one argument.
 */
export function CommandTools({
  tools,
  statuses,
  saving,
  onSave,
}: {
  tools: Record<string, CommandToolConfig>;
  statuses: CommandToolStatus[];
  saving: boolean;
  onSave: (tools: Record<string, CommandToolConfig>) => Promise<boolean>;
}) {
  const [form, setForm] = useState<Form | null>(null);
  const [trying, setTrying] = useState<string | null>(null);
  const [testValues, setTestValues] = useState<Record<string, string>>({});
  const [test, setTest] = useState<{ ok: boolean; output: string; milliseconds: number; ran?: string } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const used = form ? [...new Set([...placeholders(form.command), ...placeholders(form.workingDirectory)])] : [];

  function validate(): string | null {
    if (!form) return null;
    const name = form.name.trim();
    if (!/^[a-z][a-z0-9_]{1,47}$/.test(name)) return "Name: 2 to 48 lowercase letters, digits or underscores, starting with a letter, for example run_tests.";
    if (form.editing !== name && tools[name]) return `There is already a tool called ${name}.`;
    if (!form.description.trim()) return "Say what it does: the model decides when to use it from this.";
    if (!form.command.trim()) return "Enter the command, for example dotnet test {project}.";
    if (placeholders(form.command.trim().split(/\s+/)[0] ?? "").length) return "The program itself must be fixed; placeholders only fill in its arguments.";
    return null;
  }

  async function runTest() {
    const error = validate();
    if (error) return setProblem(error);
    setProblem(null);
    setBusy(true);
    setTest(null);
    try {
      const res = await fetch("/api/tools/custom/test", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ name: form!.name.trim(), tool: toTool(form!, true), arguments: testValues }),
      });
      const data = await res.json();
      if (!res.ok) setProblem(data.error ?? `HTTP ${res.status}`);
      else setTest(data);
    } catch {
      setProblem("Could not reach the backend.");
    } finally {
      setBusy(false);
    }
  }

  async function save() {
    const error = validate();
    if (error) return setProblem(error);
    const name = form!.name.trim();
    const next = { ...tools };
    if (form!.editing && form!.editing !== name) delete next[form!.editing];
    next[name] = toTool(form!, form!.editing ? (tools[form!.editing]?.enabled ?? true) : true);
    if (await onSave(next)) {
      setForm(null);
      setTest(null);
      setProblem(null);
    }
  }

  return (
    <div className="space-y-2">
      <div className="flex items-baseline justify-between">
        <h3 className="text-xs uppercase tracking-wide text-neutral-500">Your own tools</h3>
        {!form && (
          <button type="button" onClick={() => { setForm({ ...EMPTY }); setTest(null); setProblem(null); }} className="text-[11px] text-sky-400 hover:underline">
            + New tool from a command
          </button>
        )}
      </div>
      <p className="text-xs text-neutral-500">
        Turn a command you run often into a tool: <code>dotnet test {"{project}"}</code> becomes a tool the model calls with a
        project. It runs as a program, not through a shell, so a value the model fills in is always one argument.
      </p>

      {Object.keys(tools).length === 0 && !form && <p className="text-xs text-neutral-600">None yet.</p>}
      {Object.entries(tools).map(([name, tool]) => {
        const status = statuses.find((s) => s.name === name);
        return (
          <div key={name} className="bg-neutral-800/60 border border-neutral-700 rounded-md p-3">
            <div className="flex items-center gap-3">
              <span className="font-mono text-sm text-neutral-100">{name}</span>
              {tool.readOnly && <span className="text-[10px] text-sky-300">read-only</span>}
              <span className={`text-[11px] ${!tool.enabled ? "text-neutral-500" : status?.problem ? "text-red-400" : "text-emerald-400"}`}>
                {!tool.enabled ? "off" : status?.problem ? "not offered" : "on"}
              </span>
              <div className="ml-auto flex items-center gap-3 text-xs">
                {tool.enabled && !status?.problem && (
                  <button type="button" onClick={() => setTrying(trying === name ? null : name)} className="text-sky-400 hover:underline">
                    Try
                  </button>
                )}
                <button type="button" disabled={saving} onClick={() => onSave({ ...tools, [name]: { ...tool, enabled: !tool.enabled } })} className="text-neutral-300 hover:text-white disabled:opacity-40">
                  {tool.enabled ? "Turn off" : "Turn on"}
                </button>
                <button type="button" onClick={() => { setForm(toForm(name, tool, name)); setTest(null); setProblem(null); }} className="text-neutral-300 hover:text-white">
                  Edit
                </button>
                <button
                  type="button"
                  disabled={saving}
                  onClick={() => {
                    const { [name]: _removed, ...rest } = tools;
                    void onSave(rest);
                  }}
                  className="text-red-400 hover:text-red-300 disabled:opacity-40"
                >
                  Remove
                </button>
              </div>
            </div>
            <p className="text-xs text-neutral-400 mt-1">{tool.description}</p>
            <p className="text-[11px] font-mono text-neutral-500 break-all">
              {tool.command}
              {tool.workingDirectory ? `   (in ${tool.workingDirectory})` : ""}
            </p>
            {status?.problem && <p className="text-[11px] text-red-300">{status.problem}</p>}
            {trying === name && (
              <ToolTry
                name={name}
                parameters={(tool.parameters ?? []).map((p) => ({ name: p.name, type: "string", description: p.description ?? null, required: p.required }))}
                onClose={() => setTrying(null)}
              />
            )}
          </div>
        );
      })}

      {form && (
        <div className="border border-neutral-700 rounded-md p-3 space-y-3">
          {!form.editing && (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-[11px] text-neutral-500">Start from:</span>
              {TEMPLATES.map((template) => (
                <button
                  key={template.name}
                  type="button"
                  onClick={() => { setForm(toForm(template.name, template.tool, null)); setTest(null); setProblem(null); setTestValues({}); }}
                  className="text-[11px] px-2 py-0.5 rounded-full border border-neutral-700 text-neutral-300 hover:bg-neutral-700"
                >
                  {template.label}
                </button>
              ))}
            </div>
          )}
          <div className="flex flex-wrap gap-3">
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Name</span>
              <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value.toLowerCase() })} placeholder="run_tests" className={`${input} w-44 font-mono`} />
            </label>
            <label className="block flex-1 min-w-60">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">What it does (the model reads this)</span>
              <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Runs the tests and reports which failed." className={`${input} w-full`} />
            </label>
          </div>
          <label className="block">
            <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Command</span>
            <input value={form.command} onChange={(e) => setForm({ ...form, command: e.target.value })} placeholder="dotnet test {project}" className={`${input} w-full font-mono`} />
            <span className="block text-[11px] text-neutral-600 mt-0.5">
              Put {"{name}"} where the model fills something in. Wrap text with spaces in &quot;double quotes&quot;. For pipes or several
              commands, point it at a script file.
            </span>
          </label>
          <div className="flex flex-wrap gap-3">
            <label className="block flex-1 min-w-60">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Run it in (optional)</span>
              <input value={form.workingDirectory} onChange={(e) => setForm({ ...form, workingDirectory: e.target.value })} placeholder="C:\projects\app  or  {folder}" className={`${input} w-full font-mono`} />
            </label>
            <label className="block">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500 mb-0.5">Time limit (seconds)</span>
              <input value={form.timeoutSeconds} onChange={(e) => setForm({ ...form, timeoutSeconds: e.target.value })} placeholder="120" className={`${input} w-20`} />
            </label>
            <label className="flex items-center gap-1.5 text-xs text-neutral-300 self-end pb-1" title="Only tools that change nothing may run while a plan waits for approval">
              <input type="checkbox" checked={form.readOnly} onChange={(e) => setForm({ ...form, readOnly: e.target.checked })} />
              it only reads (allowed while planning)
            </label>
          </div>

          {used.length > 0 && (
            <div className="space-y-1.5">
              <span className="block text-[10px] uppercase tracking-wide text-neutral-500">What the model fills in</span>
              {used.map((name) => {
                const parameter = form.parameters[name] ?? { description: "", required: true };
                const update = (patch: Partial<typeof parameter>) =>
                  setForm({ ...form, parameters: { ...form.parameters, [name]: { ...parameter, ...patch } } });
                return (
                  <div key={name} className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-xs text-neutral-200 w-28 truncate">{name}</span>
                    <input value={parameter.description} onChange={(e) => update({ description: e.target.value })} placeholder="what to pass, for the model" className={`${input} flex-1 min-w-48`} />
                    <label className="flex items-center gap-1 text-[11px] text-neutral-400">
                      <input type="checkbox" checked={parameter.required} onChange={(e) => update({ required: e.target.checked })} />
                      required
                    </label>
                    <input
                      value={testValues[name] ?? ""}
                      onChange={(e) => setTestValues({ ...testValues, [name]: e.target.value })}
                      placeholder="value to try with"
                      className={`${input} w-44 font-mono`}
                    />
                  </div>
                );
              })}
            </div>
          )}

          <div className="flex items-center gap-2">
            <button type="button" onClick={runTest} disabled={busy} className="text-xs px-3 py-1.5 rounded-md bg-neutral-700 text-neutral-100 hover:bg-neutral-600 disabled:opacity-50">
              {busy ? "Running..." : "Try it"}
            </button>
            <button
              type="button"
              onClick={save}
              disabled={saving}
              className="text-xs px-3 py-1.5 rounded-md bg-emerald-600/20 text-emerald-300 border border-emerald-600/40 hover:bg-emerald-600/30 disabled:opacity-50"
            >
              {saving ? "Saving..." : form.editing ? "Save changes" : "Add and save"}
            </button>
            <button type="button" onClick={() => { setForm(null); setTest(null); setProblem(null); }} className="text-xs text-neutral-500 hover:text-neutral-300">
              Cancel
            </button>
          </div>
          {problem && <p className="text-xs text-red-300">{problem}</p>}
          {test && <ToolOutput result={test} />}
        </div>
      )}
    </div>
  );
}
