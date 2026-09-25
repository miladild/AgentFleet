"use client";

import { useState } from "react";

export type ToolParameter = { name: string; type: string; description: string | null; required: boolean };

type RunResult = { ok: boolean; output: string; milliseconds: number; ran?: string };

/** Turns what was typed into the value the tool expects: numbers, true/false, and JSON for arrays and objects. */
function toValue(type: string, raw: string): unknown {
  if (type === "integer" || type === "number") return Number(raw);
  if (type === "boolean") return raw.trim().toLowerCase() === "true";
  if (type === "array" || type === "object") return JSON.parse(raw);
  return raw;
}

/**
 * Runs one tool with values typed in a form and shows exactly what the model would get back. Works for built-in
 * tools, MCP tools and command tools.
 */
export function ToolTry({ name, parameters, onClose }: { name: string; parameters: ToolParameter[]; onClose: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<RunResult | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  async function run() {
    setProblem(null);
    const args: Record<string, unknown> = {};
    for (const parameter of parameters) {
      const raw = values[parameter.name] ?? "";
      if (!raw.trim()) {
        if (parameter.required) return setProblem(`${parameter.name} is required.`);
        continue;
      }
      try {
        args[parameter.name] = toValue(parameter.type, raw);
      } catch {
        return setProblem(`${parameter.name} must be JSON (${parameter.type}).`);
      }
    }
    setRunning(true);
    setResult(null);
    try {
      const res = await fetch("/api/tools/run", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ name, arguments: args }),
      });
      const data = await res.json();
      if (!res.ok) setProblem(data.error ?? `HTTP ${res.status}`);
      else setResult(data);
    } catch {
      setProblem("Could not reach the backend.");
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="mt-2 bg-neutral-950/70 border border-neutral-700 rounded-md p-3 space-y-2">
      <div className="flex items-center justify-between">
        <span className="text-xs text-neutral-300">
          Try <span className="font-mono text-neutral-100">{name}</span>. It really runs, on the hub, like when the model calls it.
        </span>
        <button type="button" onClick={onClose} className="text-[11px] text-neutral-500 hover:text-neutral-300">
          Close
        </button>
      </div>
      {parameters.length === 0 && <p className="text-[11px] text-neutral-500">It takes no values.</p>}
      {parameters.map((parameter) => (
        <label key={parameter.name} className="block">
          <span className="block text-[10px] uppercase tracking-wide text-neutral-500">
            {parameter.name}
            {parameter.required ? "" : " (optional)"} <span className="normal-case tracking-normal text-neutral-600">{parameter.type}</span>
          </span>
          {parameter.description && <span className="block text-[11px] text-neutral-500">{parameter.description}</span>}
          <input
            value={values[parameter.name] ?? ""}
            onChange={(e) => setValues({ ...values, [parameter.name]: e.target.value })}
            onKeyDown={(e) => e.key === "Enter" && run()}
            className="mt-0.5 w-full text-xs px-2 py-1 rounded bg-neutral-900 border border-neutral-700 text-neutral-200 font-mono"
          />
        </label>
      ))}
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={run}
          disabled={running}
          className="text-xs px-3 py-1 rounded-md bg-sky-600/20 text-sky-200 border border-sky-600/40 hover:bg-sky-600/30 disabled:opacity-50"
        >
          {running ? "Running..." : "Run"}
        </button>
        {problem && <span className="text-[11px] text-red-300">{problem}</span>}
      </div>
      {result && <ToolOutput result={result} />}
    </div>
  );
}

export function ToolOutput({ result }: { result: RunResult }) {
  return (
    <div className="space-y-1">
      <p className={`text-[11px] ${result.ok ? "text-emerald-400" : "text-amber-300"}`}>
        {result.ok ? "Done" : "It reported a problem"} in {result.milliseconds} ms.
        {result.ran && <span className="text-neutral-500 font-mono"> Ran: {result.ran}</span>}
      </p>
      <pre className="text-[11px] text-neutral-300 whitespace-pre-wrap break-words max-h-64 overflow-y-auto bg-neutral-900 border border-neutral-800 rounded p-2">
        {result.output || "(no output)"}
      </pre>
    </div>
  );
}
