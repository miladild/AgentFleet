"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  MAX_LABEL_LENGTH,
  groupRowText,
  importBody,
  importFileProblem,
  importSummary,
  nodeNamesFromResults,
  pluralize,
  formatUtcDateTime,
  type BenchImport,
  type BenchImportResult,
  type BenchModels,
} from "./modelsPresentation";

type Message = { text: string; error: boolean };

const COLUMNS = ["Machine", "Model", "Runs", "Done and correct", "False accepts", "Parked", "Timed out", "Errors", "Tasks", "Last run"];

async function readJson<T>(response: Response): Promise<T | null> {
  try {
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

/** Benchmark counts by machine and model, and the import of a finished batch. Counts only: nothing here ranks models. */
export function ModelsTab() {
  const [data, setData] = useState<BenchModels | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [resultsFile, setResultsFile] = useState<File | null>(null);
  const [runFile, setRunFile] = useState<File | null>(null);
  const [label, setLabel] = useState("");
  const [nodeNames, setNodeNames] = useState<string[]>([]);
  const [models, setModels] = useState<Record<string, string>>({});
  const [importing, setImporting] = useState(false);
  const [importMessage, setImportMessage] = useState<Message | null>(null);
  const [formKey, setFormKey] = useState(0);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [removeMessage, setRemoveMessage] = useState<Message | null>(null);

  const load = useCallback(async () => {
    try {
      const response = await fetch("/api/lab/models", { cache: "no-store" });
      const answer = await readJson<Partial<BenchModels> & { error?: string }>(response);
      if (!response.ok || !answer) {
        setLoadError(answer?.error ?? "Could not read the counts from the backend.");
        return;
      }
      setData({
        days: answer.days ?? null,
        groups: Array.isArray(answer.groups) ? answer.groups : [],
        imports: Array.isArray(answer.imports) ? answer.imports : [],
      });
      setLoadError(null);
    } catch {
      setLoadError("Could not reach the backend.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  // The machines named in the results file decide which model boxes the form shows. Read once per chosen file.
  useEffect(() => {
    let current = true;
    if (!resultsFile || importFileProblem(resultsFile)) {
      setNodeNames([]);
      return;
    }
    resultsFile
      .text()
      .then((text) => {
        if (current) setNodeNames(nodeNamesFromResults(text));
      })
      .catch(() => {
        if (current) setNodeNames([]);
      });
    return () => {
      current = false;
    };
  }, [resultsFile]);

  const resultsProblem = resultsFile ? importFileProblem(resultsFile) : null;
  const runProblem = runFile ? importFileProblem(runFile) : null;
  const missingModel = runFile === null && nodeNames.some((node) => !(models[node] ?? "").trim());
  const canImport = resultsFile !== null && resultsProblem === null && runProblem === null && !missingModel && !importing;

  async function importRun(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!resultsFile) return;
    setImporting(true);
    setImportMessage(null);
    try {
      const resultsJsonl = await resultsFile.text();
      const runJson = runFile ? await runFile.text() : null;
      const nodes = nodeNamesFromResults(resultsJsonl);
      const unnamed = runJson === null ? nodes.filter((node) => !(models[node] ?? "").trim()) : [];
      if (unnamed.length > 0) {
        setImportMessage({ text: `Say which model ran on ${unnamed.join(", ")}.`, error: true });
        return;
      }
      const response = await fetch("/api/lab/results/import", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(importBody({ resultsJsonl, runJson, nodes, models, label })),
      });
      const answer = await readJson<BenchImportResult & { error?: string }>(response);
      if (!response.ok || !answer) {
        setImportMessage({ text: answer?.error ?? "The import failed.", error: true });
        return;
      }
      setImportMessage({ text: importSummary(answer), error: false });
      setResultsFile(null);
      setRunFile(null);
      setLabel("");
      setModels({});
      setFormKey((key) => key + 1);
      await load();
    } catch {
      setImportMessage({ text: "Could not reach the backend.", error: true });
    } finally {
      setImporting(false);
    }
  }

  async function removeImport(item: BenchImport) {
    setRemoving(item.importId);
    setRemoveMessage(null);
    try {
      const response = await fetch(`/api/lab/results/${encodeURIComponent(item.importId)}`, { method: "DELETE" });
      const answer = await readJson<{ removed?: number }>(response);
      const removed = answer?.removed;
      if (response.status === 404) {
        setRemoveMessage({ text: "That import is already gone.", error: true });
      } else if (!response.ok || typeof removed !== "number") {
        setRemoveMessage({ text: "The import could not be removed.", error: true });
      } else {
        setRemoveMessage({ text: `Removed ${pluralize(removed, "run")}.`, error: false });
      }
      await load();
    } catch {
      setRemoveMessage({ text: "Could not reach the backend.", error: true });
    } finally {
      setRemoving(null);
      setConfirming(null);
    }
  }

  return (
    <div className="space-y-5">
      <div className="space-y-2 text-xs text-neutral-500">
        <p>
          These are counts of the task runs you imported from benchmark runs, by machine and model: how many runs each machine and model
          did, and how each one ended. They are not a recommendation. Nothing here ranks models, and run times are not shown.
        </p>
        <p>
          Done and correct: the plan finished and its hidden tests passed. A false accept is a plan that said done while its hidden tests
          failed; it is counted on its own and never as done and correct. Parked, timed out and errors count the runs in which a step was
          parked, the run timed out, or an error was recorded. A machine and model with fewer than five runs is marked too few to judge.
        </p>
      </div>

      {loadError && <p className="text-xs text-red-300">{loadError}</p>}

      {data === null ? (
        loadError === null && <p className="text-xs text-neutral-500">Loading...</p>
      ) : data.groups.length === 0 ? (
        <div className="space-y-2 rounded-md border border-neutral-700 p-3 text-xs text-neutral-400">
          <p className="text-neutral-200">No benchmark runs imported yet.</p>
          <p>
            To add some, run a batch as bench/README.md describes, or try one candidate model on one worker with
            scripts/Test-CandidateModel.ps1, then import its results.jsonl below, with its run.json if you have it.
            To see which models each machine has installed, run scripts/Get-FleetModels.ps1 on the hub.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <caption className="pb-2 text-left text-[11px] text-neutral-500">Counts by machine and model</caption>
            <thead>
              <tr className="border-b border-neutral-700 text-neutral-500">
                {COLUMNS.map((column) => (
                  <th key={column} scope="col" className="py-1.5 pr-3 font-normal">
                    {column}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.groups.map((group) => {
                const row = groupRowText(group);
                return (
                  <tr key={`${group.node}|${group.model}`} className="border-b border-neutral-800 align-top text-neutral-200">
                    <th scope="row" className="py-1.5 pr-3 font-mono font-normal">
                      {row.machine}
                    </th>
                    <td className="py-1.5 pr-3 font-mono">{row.model}</td>
                    <td className="py-1.5 pr-3">
                      {row.runs}
                      {row.tag && (
                        <span className="ml-2 rounded border border-neutral-600 px-1.5 py-0.5 text-[10px] text-neutral-400">{row.tag}</span>
                      )}
                    </td>
                    <td className="py-1.5 pr-3">{row.doneAndCorrect}</td>
                    <td className="py-1.5 pr-3">{row.falseAccepts}</td>
                    <td className="py-1.5 pr-3">{row.parked}</td>
                    <td className="py-1.5 pr-3">{row.timedOut}</td>
                    <td className="py-1.5 pr-3">{row.errors}</td>
                    <td className="py-1.5 pr-3">{row.tasks}</td>
                    <td className="whitespace-nowrap py-1.5">{row.lastRun}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <section aria-labelledby="models-import-title" className="space-y-3 rounded-md border border-neutral-700 p-3">
        <h3 id="models-import-title" className="text-xs uppercase tracking-wide text-neutral-500">
          Import a benchmark run
        </h3>
        <p className="text-[11px] text-neutral-500">
          Choose results.jsonl from a finished batch. The file is read in this browser and sent once; importing the same file again adds
          nothing. Files over 2 MB are not sent. run.json is optional: without it, say which model each machine ran.
        </p>
        <form key={formKey} onSubmit={importRun} className="space-y-3">
          <div className="space-y-1">
            <label htmlFor="models-results" className="block text-[11px] text-neutral-300">
              results.jsonl (required)
            </label>
            <input
              id="models-results"
              type="file"
              accept=".jsonl"
              onChange={(event) => setResultsFile(event.target.files?.[0] ?? null)}
              className="block text-xs text-neutral-300"
            />
            {resultsProblem && <p role="alert" className="text-[11px] text-red-300">{resultsProblem}</p>}
          </div>
          <div className="space-y-1">
            <label htmlFor="models-run" className="block text-[11px] text-neutral-300">
              run.json (optional)
            </label>
            <input
              id="models-run"
              type="file"
              accept=".json"
              onChange={(event) => setRunFile(event.target.files?.[0] ?? null)}
              className="block text-xs text-neutral-300"
            />
            {runProblem && <p role="alert" className="text-[11px] text-red-300">{runProblem}</p>}
          </div>
          {resultsFile && runFile === null && nodeNames.length > 0 && (
            <fieldset className="space-y-2">
              <legend className="text-[11px] text-neutral-300">Which model did each machine run?</legend>
              {nodeNames.map((node, index) => (
                <div key={node} className="space-y-1">
                  <label htmlFor={`models-node-${index}`} className="block text-[11px] text-neutral-400">
                    Model on {node}
                  </label>
                  <input
                    id={`models-node-${index}`}
                    value={models[node] ?? ""}
                    onChange={(event) => {
                      const value = event.target.value;
                      setModels((current) => ({ ...current, [node]: value }));
                    }}
                    className="w-full max-w-xs rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-xs text-neutral-200"
                  />
                </div>
              ))}
            </fieldset>
          )}
          <div className="space-y-1">
            <label htmlFor="models-label" className="block text-[11px] text-neutral-300">
              Label (optional)
            </label>
            <input
              id="models-label"
              value={label}
              maxLength={MAX_LABEL_LENGTH}
              onChange={(event) => setLabel(event.target.value)}
              placeholder="for example standard tier, worker only"
              className="w-full max-w-xs rounded border border-neutral-700 bg-neutral-950 px-2 py-1 text-xs text-neutral-200"
            />
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="submit"
              disabled={!canImport}
              className="rounded-md border border-emerald-600/40 bg-emerald-600/20 px-3 py-1.5 text-xs text-emerald-300 hover:bg-emerald-600/30 disabled:opacity-50"
            >
              {importing ? "Importing..." : "Import"}
            </button>
            {importMessage && (
              <p role={importMessage.error ? "alert" : "status"} className={`text-xs ${importMessage.error ? "text-red-300" : "text-emerald-300"}`}>
                {importMessage.text}
              </p>
            )}
          </div>
        </form>
      </section>

      {data && (
        <section aria-labelledby="models-imports-title" className="space-y-2">
          <h3 id="models-imports-title" className="text-xs uppercase tracking-wide text-neutral-500">
            Imports
          </h3>
          {data.imports.length === 0 ? (
            <p className="text-xs text-neutral-500">Nothing imported yet.</p>
          ) : (
            <ul className="space-y-2">
              {data.imports.map((item) => (
                <li key={item.importId} className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
                  <span className="text-neutral-200">{item.label || "No label"}</span>
                  <span className="text-neutral-500">
                    {pluralize(item.rows, "run")}, imported {formatUtcDateTime(item.importedAtUtc)}
                  </span>
                  {confirming === item.importId ? (
                    <span className="flex flex-wrap items-center gap-2 text-neutral-300">
                      Remove this import? Its runs leave the counts above. This cannot be undone.
                      <button
                        type="button"
                        disabled={removing !== null}
                        onClick={() => void removeImport(item)}
                        className="rounded-md border border-red-600/40 bg-red-600/20 px-2 py-1 text-red-200 hover:bg-red-600/30 disabled:opacity-50"
                      >
                        {removing === item.importId ? "Removing..." : "Yes, remove"}
                      </button>
                      <button
                        type="button"
                        disabled={removing !== null}
                        onClick={() => setConfirming(null)}
                        className="text-neutral-400 hover:text-neutral-200 disabled:opacity-50"
                      >
                        Cancel
                      </button>
                    </span>
                  ) : (
                    <button type="button" onClick={() => setConfirming(item.importId)} className="text-red-300 hover:text-red-200">
                      Remove
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {removeMessage && (
            <p role={removeMessage.error ? "alert" : "status"} className={`text-xs ${removeMessage.error ? "text-red-300" : "text-emerald-300"}`}>
              {removeMessage.text}
            </p>
          )}
        </section>
      )}
    </div>
  );
}
