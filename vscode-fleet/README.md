# Agent Fleet chat participant

A VS Code extension that adds `@fleet` to the Chat panel and provides **Fleet
Router** as a selectable chat model. Both paths call your Agent Fleet backend,
which routes across the machines and models you have configured. The VS Code
model picker can use Fleet Router in ordinary chat and agent workflows; the
`@fleet` participant remains available with its plan buttons and session
integration.

## Before you start

The Agent Fleet backend must be running and reachable from the machine where VS
Code runs. The extension talks straight to the backend (default port 8000). It
does not go through the web UI.

The address is a setting, **`agentFleet.backendUrl`**:

- VS Code on the same machine as the backend: leave the default,
  `http://localhost:8000`.
- VS Code on another machine on your network: set it to the hub's address, for
  example `http://192.168.1.10:8000`. Give the hub a fixed address (a DHCP
  reservation on your router) so this does not stop working when the lease
  changes.

## Select Fleet Router from the model picker

After installing the extension and reopening VS Code, open the Chat model
picker and select **Agent Fleet · Fleet Router**. If it is hidden, run **Chat:
Manage Language Models**, find Agent Fleet, and make Fleet Router visible. The
provider API requires VS Code 1.104 or newer. Current VS Code BYOK model access
supports chat without a Copilot plan, though Business and Enterprise policies
can restrict BYOK models.

Fleet Router is the fleet's existing routing policy, not a selector for an
individual node or Ollama model. Its requests go directly to the configured
backend, use its fleet tools, and keep the same backend URL and LAN boundary as
`@fleet`. It advertises tool calling for agent mode and streams text and tool
calls. Image input is not supported. In backend plan mode, a proposed plan is
shown in chat as text; type `approve` to approve it or give feedback to revise
it. The richer Approve/Reject buttons remain available through `@fleet`.

## Install

1. From `vscode-fleet/`:
   ```powershell
   npm install
   npm run compile
   npx --yes @vscode/vsce package --allow-missing-repository
   code --uninstall-extension local.agent-fleet-chat
   code --install-extension agent-fleet-chat-<version>.vsix
   ```
   Uninstalling first avoids VS Code keeping an old copy of the extension next
   to the new one.
2. If you also use VS Code Insiders, repeat the two `code` commands with
   `code-insiders`. The two builds keep separate extension folders.
3. **Fully close and reopen VS Code.** "Developer: Reload Window" does not
   reliably load a freshly installed `.vsix`.
4. Open VS Code Chat and type `@fleet what does the % operator do in Python?`
   You should see a streamed answer ending in `via <machine>`.

If typing `@fleet` shows a list of workspace files instead of a chat
participant, the extension is not loaded in that VS Code build. Check
`code --list-extensions` and `code-insiders --list-extensions` and install into
the one you actually use.

## Plan mode from VS Code

Start the request with `/plan`:
`@fleet /plan add rate limiting to the API in C:\src\myapp`. Plan mode then
applies to that chat (its follow-ups too), whatever the web UI's switch says;
the switch still turns it on for every chat. The strongest machine explores the
code with read-only tools and answers with a plan (the goal, the steps, which
kind of machine does each, any diagram), followed by buttons:

- **Approve and run** starts the plan in the background on the backend, one
  step at a time, with the fleet running each step's own check.
- **Reject** discards it. To change the plan instead, reply with what to change.
- **Stop** halts a running plan. The plan card also shows the plan's status.

You can close VS Code once a plan is approved. The backend keeps running it,
spreading the steps over your machines by tier, and carries on after a restart.
`@fleet /status` shows the current plan's report in the chat (steps, attempts,
which machine did what, the files changed) with **Stop it**, or **Approve and
resume** when it is blocked. The web UI's **Plans** button shows the same.
Typing `approve` in the chat also approves the pending plan.

**Watch it live** (under `@fleet /status`, after approving, or the command
**Agent Fleet: Watch the running plan live**) opens the web UI's live view of
the plan in VS Code's Simple Browser: the steps as a pipeline, the machine on
each, and a log of what they do, updated every two seconds. The web UI is looked
for on the backend's machine at port 3000; `agentFleet.webUrl` sets another
address.

## A plan made with Copilot

Work the plan out with Copilot, then hand it over in one of two ways.

- In agent mode, ask Copilot to send it to the fleet ("run this plan on my
  machines overnight"), or mention `#fleetPlan`. Copilot calls the extension's
  **Run a plan on Agent Fleet** tool with a check command and a tier for every
  step. The fleet reviews the plan and tells Copilot what to fix; VS Code then
  shows you the steps and asks before it starts. `#fleetStatus` lets Copilot
  report how it is going.
- In the same chat, send `@fleet /plan` or `@fleet run the plan above`. VS Code
  gives `@fleet` only its own turns, so the extension reads the rest of the chat
  with the chat's **Copy All** command and puts your clipboard text back (an
  image on the clipboard is lost). The strongest machine turns the plan into a
  fleet plan for you to approve. The `agentFleet.readWholeChat` setting turns
  this off.

## Which tools does `@fleet` see?

`@fleet` gets tools from three places, configured in different files. Only the
third is VS Code's own configuration.

| | Built-in tools | Fleet MCP servers | VS Code tools |
|---|---|---|---|
| Examples | `read_file`, `edit_file`, `search_files`, `find_files`, `write_file`, `run_command`, `run_git_command`, `web_search`, `web_fetch` | Any MCP server you add to the fleet | Tools from VS Code's `mcp.json`, and other extensions' language model tools |
| Defined in | C# in `agent-fleet/agent/Program.cs` | `mcpServers` in `fleet.config.json`, or the web UI's Config panel | `.vscode/mcp.json`, or the user-level `mcp.json` |
| Turned on or off in | `fleet.config.json` or the Config panel | The same, per server and per tool | `mcp.json` (add or remove the server) |
| Runs | On the hub, inside the backend | On the hub, started by the backend | Through VS Code: a local process it launches, or a remote server it calls |
| In the web UI at `localhost:3000` | Yes | Yes | **No** |
| In `@fleet` inside VS Code | Yes | Yes | Yes |
| Confirmation | No prompt | No prompt | The tool can ask inline through VS Code's native confirmation flow; MCP servers also have a trust prompt |

The rule of thumb: **`.vscode/mcp.json` configures VS Code, not the fleet.** The
backend never reads that file. If the web UI should have a tool too, add the MCP
server to the fleet instead (Config panel, "MCP servers"); it uses the same field
names as `mcp.json`, so an entry can be pasted across. The extension suppresses a
VS Code MCP tool when its full description and normalized input schema uniquely
match an enabled fleet MCP tool. If the schemas differ, both may still appear;
configure a given server in one place to avoid that.

The fleet's routed model decides whether to call a tool. The selected model
provider calls the backend for generation; VS Code runs any tool the model
requests and hands the result back so the fleet can continue. In the `@fleet`
flow, the extension passes VS Code's chat invocation token to
`vscode.lm.invokeTool`, so a tool that requests confirmation through its
`prepareInvocation` hook gets the native inline Continue/Cancel prompt.

## Editor context

The request includes bounded text from file attachments and `#file` references,
attached selections, and the active selection; it also includes the active
editor's path. The extension reads at most 12,000 characters per item and 24,000
total. This content is marked as untrusted project data in the prompt. If VS Code
and the backend run on different machines, file tools still run on the hub; the
extension provides attached content and workspace-relative paths as context,
but the hub cannot write into a separate VS Code machine's workspace.

### Adding an MCP server for `@fleet` only (VS Code's config)

`@fleet` picks up whatever VS Code has registered in `vscode.lm.tools`, so you
configure these the normal VS Code way. Two scopes:

- **One workspace**: `.vscode/mcp.json` in that workspace's folder.
- **Every workspace**: run **MCP: Open User Configuration** from the Command
  Palette and add the server there.

Format, for example with Microsoft Learn's documentation server:

```json
{
  "servers": {
    "microsoft-learn": {
      "type": "http",
      "url": "https://learn.microsoft.com/api/mcp"
    }
  }
}
```

Local servers that run as a process use `"type": "stdio"` with `command` and
`args` instead of `url`.

After adding or changing a server:

1. VS Code shows a trust prompt the first time a server starts or its config
   changes. Approve it (**MCP: Reset Trust** undoes that decision).
2. Restart VS Code if the tools do not appear. **MCP: List Servers** shows each
   server and its state, and the **Configure Tools** button in the chat input
   lists the tools that are available.
3. Ask `@fleet` something that needs the tool. It shows a "Running ..."
   progress line when it calls one.

## Custom instructions and sessions

- `@fleet` reads `.github/copilot-instructions.md` from your workspace, if it
  has one, and sends it with every turn. To use other files, list them in the
  `agentFleet.instructionFiles` setting, paths relative to the workspace
  folder (for example `["AGENTS.md", ".github/copilot-instructions.md"]`); the
  first one that exists is sent.
- The extension tells the fleet which local workspace folders are open, so
  requests such as "run the tests" have a project location. It sends paths
  only when the backend URL is loopback and the workspace uses local files;
  remote workspaces and a backend on another machine are left out.
- Conversations are saved to the same record as the web UI, so a chat
  started in VS Code can be resumed from the web UI's Sessions list. To go
  the other way, see [Continue a fleet chat](#continue-a-fleet-chat).

## Durable context

Each `@fleet` chat has a **context id**, the id of that conversation in the fleet's durable record (`docs/context.md` in the
Agent Fleet repository explains the record). The extension keeps it in the metadata of every response, which VS Code returns
in the history of the next turn of the same chat, so a chat keeps its pinned decisions, plans and handoffs across turns,
machines and restarts of the backend. Nothing is inferred from what you typed.

## Continue a fleet chat

Run **Agent Fleet: Continue a fleet chat** from the Command Palette, or click the Fleet item in the status bar once it
shows. Pick a chat from the fleet's Sessions list (started in the web UI or with `@fleet`), then how to continue it:

- **In a new @fleet chat.** A new chat opens with `@fleet` typed in, and its first message joins the picked conversation.
  From then on `@fleet` sends the conversation's saved transcript with every turn, so turns made in the web UI in the
  meantime are part of it and stay in it. Undoing or editing an earlier request in this VS Code chat does not change that
  transcript.
- **With the Fleet Router model.** Every chat with **Agent Fleet - Fleet Router** in this workspace joins the picked
  conversation until you remove the link. The status bar shows the link; **Agent Fleet: Stop linking the Fleet Router model
  to a fleet chat** removes it. The model is handed the conversation's pinned decisions, recorded files and latest turns by
  the backend. What you say in the model picker is added to the conversation's record, but it does not replace the
  transcript the web UI shows, because VS Code sends its own prompt along with it. While the link is on, side requests VS
  Code makes with this model (chat titles, for example) are recorded too.

## The model picker's experimental carrier

Without a link, the **Fleet Router** entry in the model picker has no id of its own. Its context carrier is experimental and
off by default:

1. Set `agentFleet.contextCarrier` to `true` in VS Code's settings.
2. Choose **Agent Fleet - Fleet Router** in the model picker and send two messages in the same chat.
3. Open the web UI's Sessions list (or `GET /api/contexts`). If the chat shows up **once** and holds both turns, your VS Code
   build returns the carrier. Fully restart VS Code, send a third message, and check that it joins the same entry.
4. If it appears again on every turn, or not at all, turn the setting off: VS Code is not returning the data part, and
   model-picker chats are simply not recorded. `@fleet` is unaffected.

Requests without a carrier are never recorded, so side requests VS Code makes with the same model (titles, summaries) do not
create contexts.

## After changing the extension

Use F5 (Run > Start Debugging) to try changes in an Extension Development Host
without touching the installed copy. To install a change for real, bump
`version` in `package.json`, then repeat the Install steps.

## Known limitations, matching the fleet's own design choices

- Text-file attachments and selections are passed as context; image attachments
  are not forwarded here. Use the web UI at `localhost:3000` for screenshots.
- Hub tools such as `run_command`, `write_file`, and `edit_file` run immediately
  with backend permissions and do not ask for confirmation. VS Code tools can
  request their own native confirmation.
- The model picker entry reports fixed context/output limits and uses
  approximate token counting; actual model limits vary with fleet config.
