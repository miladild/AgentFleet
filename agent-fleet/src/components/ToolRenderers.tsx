"use client";

import { useRenderTool } from "@copilotkit/react-core/v2";
import { z } from "zod";
import { PlanCard } from "./PlanCard";

const PLAN_MARKER = /\[plan:([0-9a-f]{32})\]/;

function ToolFrame({
  label,
  detail,
  children,
}: {
  label: string;
  detail: string;
  children?: React.ReactNode;
}) {
  return (
    <div className="my-2 rounded-md border border-neutral-800 bg-neutral-950 overflow-hidden text-sm">
      <div className="px-3 py-1.5 bg-neutral-900 text-neutral-400 flex items-center justify-between gap-2">
        <span className="font-medium text-neutral-300">{label}</span>
        <span className="truncate text-xs text-neutral-500">{detail}</span>
      </div>
      {children}
    </div>
  );
}

function CodeBlock({ text }: { text: string }) {
  return (
    <pre className="px-3 py-2 overflow-x-auto text-xs text-neutral-200 whitespace-pre-wrap break-words max-h-72 overflow-y-auto">
      <code>{text}</code>
    </pre>
  );
}

/** Registers custom renderers for the fleet's four tools, replacing plain prose
 * descriptions with a small code-block-style view (path/language header, output
 * or new content below), so tool activity reads more like Copilot/Claude Code's
 * inline diffs than a wall of text. Must render inside the CopilotKit provider. */
export function ToolRenderers() {
  useRenderTool(
    {
      name: "run_sandboxed_code",
      parameters: z.object({
        language: z.string(),
        code: z.string(),
      }),
      render: ({ parameters, status, result }) => {
        const lang = parameters?.language ?? "code";
        if (status !== "complete") {
          return (
            <ToolFrame label={`Running ${lang} in sandbox`} detail="throwaway container">
              {parameters?.code && <CodeBlock text={parameters.code} />}
            </ToolFrame>
          );
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:") || /Exit code: [^0]/.test(text);
        return (
          <ToolFrame
            label={`Ran ${lang} in sandbox`}
            detail={failed ? "failed" : "exit code 0"}
          >
            {parameters?.code && <CodeBlock text={parameters.code} />}
            <div className={`px-3 py-2 border-t border-neutral-800 text-xs whitespace-pre-wrap ${failed ? "text-red-400" : "text-emerald-400"}`}>
              {text}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "write_file",
      parameters: z.object({
        path: z.string(),
        content: z.string(),
      }),
      render: ({ parameters, status, result }) => {
        if (status !== "complete") {
          return (
            <ToolFrame label="Writing file" detail={parameters?.path ?? ""}>
              {parameters?.content && <CodeBlock text={parameters.content} />}
            </ToolFrame>
          );
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:");
        return (
          <ToolFrame
            label={failed ? "Failed to write file" : "Wrote file"}
            detail={parameters?.path ?? ""}
          >
            {parameters?.content && <CodeBlock text={parameters.content} />}
            <div className={`px-3 py-1.5 border-t border-neutral-800 text-xs ${failed ? "text-red-400" : "text-emerald-400"}`}>
              {text}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "read_file",
      parameters: z.object({
        path: z.string(),
        startLine: z.number().optional(),
        endLine: z.number().optional(),
      }),
      render: ({ parameters, status, result }) => {
        const range =
          parameters?.startLine || parameters?.endLine
            ? ` (lines ${parameters?.startLine ?? 1}-${parameters?.endLine ?? "end"})`
            : "";
        if (status !== "complete") {
          return <ToolFrame label="Reading file" detail={`${parameters?.path ?? ""}${range}`} />;
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        return (
          <ToolFrame label="Read file" detail={`${parameters?.path ?? ""}${range}`}>
            <CodeBlock text={text} />
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "list_directory",
      parameters: z.object({
        path: z.string(),
      }),
      render: ({ parameters, status, result }) => {
        if (status !== "complete") {
          return <ToolFrame label="Listing directory" detail={parameters?.path ?? ""} />;
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const entries = text.split("\n").filter(Boolean);
        return (
          <ToolFrame label="Listed directory" detail={parameters?.path ?? ""}>
            <ul className="px-3 py-2 text-xs text-neutral-300 space-y-0.5">
              {entries.map((entry, i) => (
                <li key={i} className={entry.endsWith("/") ? "text-sky-400" : ""}>
                  {entry}
                </li>
              ))}
            </ul>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "edit_file",
      parameters: z.object({
        path: z.string(),
        oldText: z.string(),
        newText: z.string(),
        replaceAll: z.boolean().optional(),
      }),
      render: ({ parameters, status, result }) => {
        const prefixed = (text: string, prefix: string) =>
          text
            .split("\n")
            .map((line) => prefix + line)
            .join("\n");
        const diff = (
          <div className="text-xs font-mono">
            {parameters?.oldText && (
              <pre className="px-3 py-2 bg-red-950/30 text-red-300 whitespace-pre-wrap break-words max-h-48 overflow-y-auto">
                {prefixed(parameters.oldText, "- ")}
              </pre>
            )}
            {parameters?.newText && (
              <pre className="px-3 py-2 bg-emerald-950/30 text-emerald-300 whitespace-pre-wrap break-words max-h-48 overflow-y-auto">
                {prefixed(parameters.newText, "+ ")}
              </pre>
            )}
          </div>
        );
        if (status !== "complete") {
          return (
            <ToolFrame label="Editing file" detail={parameters?.path ?? ""}>
              {diff}
            </ToolFrame>
          );
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:");
        return (
          <ToolFrame label={failed ? "Edit failed" : "Edited file"} detail={parameters?.path ?? ""}>
            {diff}
            <div className={`px-3 py-1.5 border-t border-neutral-800 text-xs ${failed ? "text-red-400" : "text-emerald-400"}`}>
              {text}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "search_files",
      parameters: z.object({
        pattern: z.string(),
        path: z.string().optional(),
        fileGlob: z.string().optional(),
        ignoreCase: z.boolean().optional(),
      }),
      render: ({ parameters, status, result }) => {
        const detail = `/${parameters?.pattern ?? ""}/${parameters?.fileGlob ? ` in ${parameters.fileGlob}` : ""}`;
        if (status !== "complete") {
          return <ToolFrame label="Searching files" detail={detail} />;
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:");
        return (
          <ToolFrame label={failed ? "Search failed" : "Searched files"} detail={detail}>
            <CodeBlock text={text} />
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "find_files",
      parameters: z.object({
        pattern: z.string(),
        path: z.string().optional(),
      }),
      render: ({ parameters, status, result }) => {
        const detail = parameters?.pattern ?? "";
        if (status !== "complete") {
          return <ToolFrame label="Finding files" detail={detail} />;
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        return (
          <ToolFrame label="Found files" detail={detail}>
            <CodeBlock text={text} />
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "propose_plan",
      parameters: z.object({
        title: z.string().optional(),
        goal: z.string().optional(),
        steps: z.array(z.object({ title: z.string().optional() })).optional(),
      }),
      render: ({ parameters, status, result }) => {
        if (status !== "complete") {
          const titles = (parameters?.steps ?? []).map((s) => s?.title).filter(Boolean);
          return (
            <ToolFrame label="Drafting a plan" detail={parameters?.title ?? ""}>
              {titles.length > 0 && (
                <ol className="px-3 py-2 list-decimal pl-7 text-xs text-neutral-400 space-y-0.5">
                  {titles.map((t, i) => (
                    <li key={i}>{t}</li>
                  ))}
                </ol>
              )}
            </ToolFrame>
          );
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const id = PLAN_MARKER.exec(text)?.[1];
        if (!id) {
          // The plan was not saved (for example a diagram with a syntax error the model was told to fix).
          return (
            <ToolFrame label="Plan not saved yet" detail={parameters?.title ?? ""}>
              <div className="px-3 py-2 text-xs text-amber-400 whitespace-pre-wrap">{text}</div>
            </ToolFrame>
          );
        }
        return <PlanCard planId={id} />;
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "blocked_by_plan_mode",
      parameters: z.object({ tool: z.string().optional() }),
      render: ({ parameters }) => (
        <ToolFrame label="Blocked while planning" detail={parameters?.tool ?? ""}>
          <div className="px-3 py-1.5 text-xs text-amber-400">
            Nothing was changed. Plan mode only allows reading until you approve a plan.
          </div>
        </ToolFrame>
      ),
    },
    [],
  );

  useRenderTool(
    {
      name: "get_plan",
      parameters: z.object({ planId: z.string().optional() }),
      render: ({ status }) => (
        <ToolFrame label={status === "complete" ? "Checked the plan" : "Checking the plan"} detail="" />
      ),
    },
    [],
  );

  useRenderTool(
    {
      name: "complete_step",
      parameters: z.object({ planId: z.string().optional(), stepId: z.number().optional(), note: z.string().optional() }),
      render: ({ parameters, status, result }) => {
        const step = parameters?.stepId ?? "";
        if (status !== "complete") return <ToolFrame label={`Verifying step ${step}`} detail="running its verify command" />;
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const ok = /verified and marked done|marked done/.test(text) && !text.startsWith("Error");
        return (
          <ToolFrame label={ok ? `Step ${step} done` : `Step ${step} not done`} detail={ok ? "verified" : "verification failed"}>
            <div className={`px-3 py-1.5 text-xs whitespace-pre-wrap max-h-48 overflow-y-auto ${ok ? "text-emerald-400" : "text-red-400"}`}>
              {text.replace(PLAN_MARKER, "").trim()}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "fail_step",
      parameters: z.object({ planId: z.string().optional(), stepId: z.number().optional(), reason: z.string().optional() }),
      render: ({ parameters, status }) => (
        <ToolFrame
          label={status === "complete" ? `Stopped at step ${parameters?.stepId ?? ""}` : "Stopping the plan"}
          detail="plan blocked"
        >
          {parameters?.reason && <div className="px-3 py-1.5 text-xs text-red-400 whitespace-pre-wrap">{parameters.reason}</div>}
        </ToolFrame>
      ),
    },
    [],
  );

  useRenderTool(
    {
      name: "run_git_command",
      parameters: z.object({
        repositoryPath: z.string(),
        arguments: z.string(),
      }),
      render: ({ parameters, status, result }) => {
        const cmd = `git ${parameters?.arguments ?? ""}`;
        if (status !== "complete") {
          return <ToolFrame label="Running git" detail={parameters?.repositoryPath ?? ""}>
            <CodeBlock text={cmd} />
          </ToolFrame>;
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:") || /Exit code: [^0]/.test(text);
        return (
          <ToolFrame label="Ran git" detail={parameters?.repositoryPath ?? ""}>
            <CodeBlock text={cmd} />
            <div className={`px-3 py-2 border-t border-neutral-800 text-xs whitespace-pre-wrap ${failed ? "text-red-400" : "text-emerald-400"}`}>
              {text}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  useRenderTool(
    {
      name: "run_command",
      parameters: z.object({
        command: z.string(),
        workingDirectory: z.string().optional(),
      }),
      render: ({ parameters, status, result }) => {
        if (status !== "complete") {
          return (
            <ToolFrame label="Running command" detail={parameters?.workingDirectory ?? ""}>
              {parameters?.command && <CodeBlock text={parameters.command} />}
            </ToolFrame>
          );
        }
        const text = typeof result === "string" ? result : JSON.stringify(result);
        const failed = text.startsWith("Error:") || /Exit code: [^0]/.test(text);
        return (
          <ToolFrame
            label={failed ? "Command failed" : "Ran command"}
            detail={parameters?.workingDirectory ?? ""}
          >
            {parameters?.command && <CodeBlock text={parameters.command} />}
            <div className={`px-3 py-2 border-t border-neutral-800 text-xs whitespace-pre-wrap ${failed ? "text-red-400" : "text-emerald-400"}`}>
              {text}
            </div>
          </ToolFrame>
        );
      },
    },
    [],
  );

  return null;
}
