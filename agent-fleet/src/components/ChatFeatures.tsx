"use client";

import { useAgentContext, useConfigureSuggestions, useFrontendTool } from "@copilotkit/react-core/v2";
import { z } from "zod";
import { setProjectFolder, useProjectFolder } from "./projectFolder";
import { openConfig } from "./setupApi";

const TABS = ["setup", "machines", "tools", "sandbox", "routing", "history"] as const;

function Note({ text }: { text: string }) {
  return <div className="my-1 text-xs text-neutral-500">{text}</div>;
}

/**
 * What the chat knows about this page and can do on it, through CopilotKit: the project folder goes to the fleet as
 * context with every message, a few starting suggestions help a first chat, and two small tools let the assistant
 * open the settings on the right tab or set the project folder when the user names one.
 */
export function ChatFeatures() {
  const folder = useProjectFolder();

  useAgentContext({
    description: "The project folder the user is working in (resolve relative paths against it)",
    value: folder || "none set; ask for the full path when a task needs a project",
  });

  useConfigureSuggestions(
    {
      available: "before-first-message",
      suggestions: folder
        ? [
            { title: "What is this project?", message: `Give me an overview of the project in ${folder}: what it is, how it is laid out, and how to build and test it.` },
            { title: "Run the tests", message: `Run the tests of the project in ${folder} and tell me what failed and why.` },
            { title: "Review my changes", message: `In ${folder}, show the git status and review the changes that are not committed yet.` },
            { title: "Find the TODOs", message: `Search ${folder} for TODO and FIXME comments and list them by file.` },
          ]
        : [
            { title: "What can you do?", message: "What can you do for me, and which tools can you use?" },
            { title: "Help me set up", message: "Help me finish setting up the fleet. Open the setup checklist for me." },
            { title: "Explain some code", message: "Explain async and await in C# with a short example." },
            { title: "Write a script", message: "Write a small Python script that renames every .jpeg file in a folder to .jpg, and explain how to run it." },
          ],
    },
    [folder],
  );

  useFrontendTool(
    {
      name: "open_fleet_settings",
      description:
        "Opens the fleet's settings in the user's web UI on one tab: setup (the checklist, model downloads, starting Ollama), machines " +
        "(add or change computers and their models), tools (built-in tools, MCP servers, the user's own command tools), sandbox (the code " +
        "sandbox, Docker over SSH), routing (the routing model), history (how long chats are kept). Use it when the user asks how to set " +
        "something up or change a setting, then tell them what to do there.",
      parameters: z.object({ tab: z.enum(TABS).describe("Which tab to open") }),
      handler: async ({ tab }) => {
        openConfig(tab);
        return `Opened the ${tab} tab of the fleet's settings for the user.`;
      },
      render: ({ args, status }) => (
        <Note text={status === "complete" ? `Opened the ${args?.tab ?? ""} settings.` : "Opening the settings..."} />
      ),
    },
    [],
  );

  useFrontendTool(
    {
      name: "set_project_folder",
      description:
        "Sets the project folder the user is working in, shown in the web UI and sent with every later message. Use it when the user says " +
        "which project to work on (a full path on the hub machine), so they do not have to repeat it.",
      parameters: z.object({ path: z.string().describe("The project's full folder path on the hub") }),
      handler: async ({ path }) => {
        setProjectFolder(path);
        return `The project folder is now ${path}. Later messages carry it as context.`;
      },
      render: ({ args, status }) => (
        <Note text={status === "complete" ? `Project folder set to ${args?.path ?? ""}.` : "Setting the project folder..."} />
      ),
    },
    [],
  );

  return null;
}
