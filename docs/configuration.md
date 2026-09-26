# Configuration

Almost everything is in one file, `fleet.config.json`. A few settings that belong to the machine rather than the fleet are
environment variables.

## Where the file is

| How you run it | Path |
|---|---|
| `npm run dev` | `agent-fleet\fleet.config.json` |
| Installed with `Install-Autostart.ps1` | `<install folder>\backend\fleet.config.json`, by default `C:\AgentFleet\backend\fleet.config.json` |
| Either, if you set `FLEET_CONFIG_PATH` | wherever you point it |

If the file does not exist, the backend creates one on first start with a single node: this machine, running
`qwen2.5-coder:7b`. After that the file is the only source of truth. Edit it by hand, from the web UI's **Config** panel,
or with `Add-FleetNode.ps1`. Machines, the triage model, tools, MCP servers, the history setting and the sandbox apply as soon as they are saved from the Config panel (or through
`PUT /api/fleet-config` and `PUT /api/setup/sandbox`). A change made by hand in the file applies when the backend starts. The
mode and plan-mode switches apply at once. `agent-fleet\fleet.config.example.json` is a complete example.

The backend refuses to start on an invalid file and says what is wrong, for example "Exactly one node must be the
fallback, found 2."

## The file

```json
{
  "triageModel": "llama3.2:latest",
  "mode": "conservative",
  "planModeEnabled": false,
  "nodes": [ ... ],
  "tools": { ... },
  "mcpServers": { ... },
  "sandbox": { ... },
  "history": { "deleteAfterDays": 90 }
}
```

| Field | Meaning |
|---|---|
| `triageModel` | The small model that decides which machine gets a request. It runs on the fallback node, so install it there. Not used when there is only one text node. |
| `mode` | `conservative` or `aggressive`. See [adding-machines.md](adding-machines.md#hub-mode). Changed from the web UI. |
| `planModeEnabled` | Plan mode on or off. Changed from the web UI. |

### `nodes`

```json
{
  "name": "worker1",
  "url": "http://192.168.1.21:11434/v1",
  "model": "qwen2.5-coder:7b",
  "purpose": "Ordinary coding work",
  "tier": "standard",
  "vision": false,
  "fallback": false
}
```

| Field | Meaning |
|---|---|
| `name` | 1 to 32 lowercase letters, digits or hyphens. Must be unique. It is what the router picks, and what the UI shows. |
| `url` | The machine's Ollama address, ending in `/v1`. Without it, `/v1` is added for you. |
| `model` | The model that machine serves, exactly as `ollama list` shows it there. |
| `purpose` | A note for humans. Shown in the UI; the router does not read it. |
| `tier` | `heavy`, `standard` or `light`. Missing means `standard`. Ignored for a vision node. |
| `vision` | Reads images. Used only when a message contains one. Never the fallback. |
| `fallback` | Exactly one node has this. Missing everywhere means the first heavy text node. |
| `contextLength` | The most tokens of conversation this machine's model may be asked to see (Ollama's `num_ctx`). Each request asks for what it needs, up to this. Missing means 32768 (or `FLEET_CONTEXT_LENGTH`). Lower it for a machine whose model spills out of graphics memory; see [planning.md](planning.md#how-much-a-model-sees) |
| `api` | `ollama` (default): Ollama's native API, which is what lets the fleet set the context size. `openai`: any OpenAI-compatible server (LM Studio, vLLM, llama.cpp); it then chooses its own context size |

At least one text node (not vision) is required.

### `tools`

`"tools": { "run_command": { "enabled": false } }`. A tool that is not listed is on. See
[tools-and-mcp.md](tools-and-mcp.md) for the names.

### `mcpServers`

Same shape as VS Code's `mcp.json`:

| Field | For | Meaning |
|---|---|---|
| `type` | both | `stdio` (a program the backend starts) or `http` (a remote server) |
| `command`, `args`, `env` | stdio | What to run. `${env:NAME}` in any value is replaced with that environment variable. |
| `url`, `headers` | http | Where to connect. `${env:NAME}` works here too. |
| `enabled` | both | `false` keeps the entry but does not connect |

### `customTools`

Tools made from a command, usually from **Config > Tools > Your own tools**. See
[tools-and-mcp.md](tools-and-mcp.md#your-own-tools-from-a-command).

| Field | Meaning |
|---|---|
| (the key) | The tool's name: 2 to 48 lowercase letters, digits or underscores, not a built-in tool's name |
| `description` | What it does; the model reads this to decide when to use it |
| `command` | The command line with `{name}` placeholders for what the model fills in. The program (first word) is fixed |
| `parameters` | One `{ "name", "description", "required" }` per placeholder |
| `workingDirectory` | Where it runs; may contain placeholders. Missing means the backend's working folder |
| `timeoutSeconds` | 1 to 600, default 120 |
| `readOnly` | It changes nothing, so it stays available while a plan waits for approval |
| `enabled` | Offered to the model |
### `sandbox`

Where `run_sandboxed_code` runs its container. See [tools-and-mcp.md](tools-and-mcp.md#the-sandbox).

| Field | Meaning |
|---|---|
| `mode` | `auto` (default), `local`, `ssh` or `off` |
| `host`, `port`, `user`, `keyPath`, `sudo` | For `ssh` mode: the machine with Docker, and how to log in to it with a key |
| `hostKey` | For `ssh` mode: that machine's SSH host key fingerprint (`SHA256:...`), saved by the Sandbox tab after a test. A machine answering with another key is refused. Missing means any key is accepted |
| `timeoutSeconds` | How long a snippet may run, 1 to 120 (default 20) |

### `history`

How long the durable record keeps chats. See [context.md](context.md#keeping-it-small).

| Field | Meaning |
|---|---|
| `deleteAfterDays` | Delete a chat and its record once nothing has happened in it for this many days, 1 to 3650. `0` or missing keeps everything. A chat an unfinished plan runs in is kept. |

## Environment variables

The backend reads these from its own environment, not from `.env.local`. When you run it with `npm run dev` or as a task,
set them in your user environment variables. For a Windows service set them on the service.

| Variable | Default | Meaning |
|---|---|---|
| `FLEET_CONFIG_PATH` | next to the backend program | The config file |
| `FLEET_CONTEXTS_DIR` | `contexts` next to the program | The durable record: one SQLite database with every conversation, tool call, decision, plan step, handoff and file hash. See [context.md](context.md) |
| `FLEET_SESSIONS_DIR` | `sessions` next to the program | Old JSON sessions, imported into the record once at startup |
| `FLEET_PLANS_DIR` | `plans` next to the program | Saved plans |
| `FLEET_FILES_ROOT` | the backend's working folder | What relative paths in file tools are relative to |
| `FLEET_ALLOWED_HOSTS` | none | Extra host names the backend answers to (comma separated). See [security.md](security.md) |
| `FLEET_FRONTEND_URL` | `http://localhost:3000` | Where the backend finds the web UI, used to check plan diagrams |
| `FLEET_NETWORK_TIMEOUT_SECONDS` | 300 | How long to wait for one model reply (10 to 900) |
| `FLEET_HEALTH_PROBE_TIMEOUT_SECONDS` | 3 | Health probe timeout (1 to 30) |
| `FLEET_HEALTH_CACHE_SECONDS` | 10 | How often nodes are probed (1 to 300) |
| `SHELL_EXECUTION_TIMEOUT_SECONDS` | 120 | How long one `run_command` may run |
| `FLEET_PLAN_STEP_TOOL_ROUNDS` | 25 | How many rounds of tool calls one attempt at a plan step may make before it ends and the step's check decides (5 to 200) |
| `FLEET_PLAN_CHEAP_ATTEMPTS` | 1 | How many of a plan step's three attempts run on a machine of the step's own tier before the rest go to the strongest machine |
| `FLEET_CONTEXT_LENGTH` | 32768 | The most tokens of context a machine is asked for when it does not set `contextLength` |
| `SANDBOX_*` | | The same settings as the `sandbox` section, used when the file does not give them |
| `HUB_OLLAMA_URL`, `HUB_OLLAMA_MODEL`, `TRIAGE_OLLAMA_MODEL` | | Seed values for the first-ever start only |

For the web UI (`agent-fleet\.env.local`, or the environment):

| Variable | Default | Meaning |
|---|---|---|
| `AGENT_URL` | `http://localhost:8000` | Where the backend is |
| `FLEET_WEB_HOST` | `127.0.0.1` | The address the web UI listens on. `0.0.0.0` lets other machines open it. |
| `PORT` | 3000 | The web UI's port |

For the PowerShell scripts in `scripts\`:

| Variable | Default | Meaning |
|---|---|---|
| `AGENT_FLEET_INSTALL_ROOT` | the folder the registered backend service or task runs from, else `C:\AgentFleet` | Where `Install-Autostart.ps1` installs, and where `Deploy-Fleet.ps1`, `Test-Fleet.ps1`, `Add-FleetNode.ps1` and `Get-FleetModels.ps1` look. `-InstallRoot` on any of them wins. |

## Ports

| Port | What |
|---|---|
| 3000 | Web UI |
| 8000 | Backend (also `@fleet`'s address) |
| 11434 | Ollama on each machine |

## HTTP interface

The backend answers these on port 8000. They are what the web UI and the VS Code extension use.

| Path | What |
|---|---|
| `GET /health` | Overall status and each node. 503 if the fallback node is down. |
| `GET /api/fleet-status` | Nodes with recent activity |
| `GET`, `PUT /api/fleet-config` | Read or change the configuration |
| `GET /api/fleet-config/models?url=` | Models installed at an Ollama address |
| `GET`, `POST /api/fleet-mode`, `/api/plan-mode` | The two switches |
| `GET /api/plans`, `/api/plans/{id}`, `/api/plans/{id}/markdown`, `/api/plans/{id}/report` | Plans, a readable copy, and the run report |
| `POST /api/plans/{id}/approve`, `/reject`, `/stop` | Act on a plan |
| `POST /api/plans` | Save a plan written elsewhere (the VS Code extension's Copilot tool uses it): `title`, `goal`, `workingDirectory` and `steps` as in `propose_plan`; `dryRun` only reviews it, `approve` starts it. A plan that would fail is refused with its `problems` |
| `GET`, `PUT`, `DELETE /api/sessions[/{id}]` | Saved conversations (the visible chats of the durable record) |
| `GET /api/contexts[/{id}[/events|deliveries|artifacts|export]]`, `POST .../decisions|compact` | The durable record. See [context.md](context.md) |
| `GET /api/contexts/storage?olderThanDays=`, `POST /api/contexts/cleanup` | The record's size and a preview of a cleanup; deleting chats not used for a number of days |
| `GET /api/logs/tail?lines=` | End of today's log |
| `GET /api/setup` | The Setup tab's checklist: this computer's hardware, suggested models and every check with its fix |
| `GET /api/setup/hub` | This computer's name and private addresses (for the worker setup commands) |
| `POST /api/setup/pull` `{url, model}`, `GET /api/setup/pulls`, `POST /api/setup/pulls/{id}/cancel` | Download a model onto a machine in the background, and follow or cancel it |
| `POST /api/setup/start-ollama` | Start the Ollama app on this computer (not when the backend runs as a Windows service) |
| `GET`, `PUT /api/setup/sandbox` | The sandbox settings; a PUT applies them at once |
| `POST /api/setup/sandbox/key`, `/test`, `/install-key`, `/prepare` | Make the fleet's SSH key, test a sandbox setting, add the key to a machine with its password (used once, never stored), download the container images |
| `POST /api/contexts/{id}/feedback` `{rating, messageId, excerpt}`, `GET /api/feedback` | A thumbs up or down on an answer, kept with the machine that answered; counts per machine |
| `POST /api/tools/run` `{name, arguments}` | Run one tool as the model would (the Try links); plan and context tools are refused |
| `POST /api/tools/custom/test` `{name, tool, arguments}` | Run a command tool that is not saved yet |
| `GET /api/tools/prerequisites` | Whether `npx`, `uvx`, `docker` and `git` are on the backend's PATH |
| `GET`, `POST /api/tools/import` | MCP servers found in other apps' settings (secrets masked), and adding chosen ones |
| `POST /` | The chat itself, over the [AG-UI](https://github.com/ag-ui-protocol/ag-ui) protocol |

There is no authentication. See [security.md](security.md).
