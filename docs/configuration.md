# Configuration

Almost everything is in one file, `fleet.config.json`. A few settings that belong to the machine rather than the fleet are
environment variables.

## Where the file is

| How you run it | Path |
|---|---|
| `npm run dev` | `agent-fleet\fleet.config.json` |
| Installed with `Install-Autostart.ps1` | `<install-root>\backend\fleet.config.json` (set `AGENT_FLEET_INSTALL_ROOT` or pass `-InstallRoot`) |
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
  "history": { "deleteAfterDays": 90 },
  "chatApprovals": { "enabled": false, "timeoutSeconds": 240 },
  "weeklyProbe": { "enabled": false, "dayOfWeek": "sunday", "startHour": 2, "hours": 4 },
  "notifyUrl": "http://192.0.2.10:8080/fleet"
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
  "url": "http://192.0.2.21:11434/v1",
  "model": "qwen2.5-coder:7b",
  "purpose": "Ordinary coding work",
  "tier": "standard",
  "vision": false,
  "fallback": false,
  "caveman": "off",
  "ponytail": "off"
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
| `caveman` | `off`, `lite`, `full` or `ultra`: asks this machine's model to answer more briefly while preserving useful detail and safety conditions. Missing means `off`. |
| `ponytail` | `off`, `lite`, `full` or `ultra`: asks this machine's model to make small, correct coding changes and avoid speculative work. Missing means `off`. |
| `thinking` | `off` (default) or `model`. A model that thinks before it answers is told not to (`off`); `model` leaves it to the model. A small model can spend its whole answer (4096 tokens) on thinking and end the turn with no tool call and no text: measured on a 9B worker, one turn in three. Choose `model` only for a machine whose model does better thinking. |
| `workspace` | Optional. Where approved plan steps run their file tools and checks on this machine, over SSH. See [Worker workspace](#worker-workspace). Not allowed on the hub or a vision node. |

At least one text node (not vision) is required.

These are prompt styles configured per machine, not skills installed on worker computers. The selected style follows a
request through fallback; set the same levels on machines that may hand work to each other when you want consistent
responses.

#### Worker workspace

Set it in **Config > Machines**, or by hand:

```json
"workspace": {
  "host": "192.0.2.21",
  "port": 22,
  "user": "fleetwork",
  "keyPath": "C:\\Users\\me\\.ssh\\fleetwork",
  "hostKey": "SHA256:...",
  "root": "D:/fleet-work",
  "platform": "windows"
}
```

| Key | Meaning |
|---|---|
| `host`, `port` | The worker's SSH address, without a user name or path. `port` defaults to 22. |
| `user` | A dedicated SSH account without admin rights, used only for plan work. |
| `keyPath` | The private key on the hub that logs in as that account. |
| `hostKey` | The worker's SSH host key fingerprint (`SHA256:...`), read on the worker itself. A machine answering with another key is refused. |
| `root` | The absolute folder on the worker that holds the plan workspaces: `/...` on Linux, `C:/...` on Windows (backslashes become forward slashes). |
| `platform` | `linux` or `windows`. |

A workspace with any of these missing or invalid is rejected when the configuration is saved or loaded.

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

### `chatApprovals`

Off when missing. When it is on, a tool call the model makes in ordinary chat waits for your answer before it runs. Read-only
tools never wait, nor do the fleet's own bookkeeping calls (plan proposals, decision pins and record searches). A plan never
waits: an approved plan runs unattended, as before. The Tools tab of the Config panel in the web UI has a switch for it and
for the wait.

| Field | Meaning |
|---|---|
| `enabled` | `true` to ask before a chat tool that can change something runs. Missing or `false` means chat runs tools without a question, as it always has. |
| `timeoutSeconds` | How long a call waits for an answer before it is refused. Missing means 240. A value outside 30 to 280 is brought into that range: the web proxy drops a stream that stays silent for 300 seconds, and the chat is silent while a call waits. |

A call that is rejected, or not answered in time, does not run. The model is told so and carries on, and can say what did not
run. The web UI shows each waiting call as a card with **Approve** and **Reject** on the chat page and on the live view, so a
request that arrives while you look at the other page is still seen. A chat started from VS Code is asked too: the
extension shows its own Allow and Refuse prompt for it, and the web UI card still works. Waiting calls are listed at
`GET /api/approvals` and answered with `POST /api/approvals/{id}/approve` or `/reject`; see the [HTTP interface](#http-interface).

### `notifyUrl`

Optional, off when missing. An address on the private network that gets one small JSON message (`POST`) when a plan needs
you: a step was parked, the run ended with steps waiting, the plan is done, the run deadline passed. See
[planning.md](planning.md). The fleet is LAN-only, so the address must be loopback, a private or link-local address, a
Tailscale address, or a name that resolves to one; a public address is refused when the file loads. Redirects are not
followed, and there is no way to put a user name or password in the address. The message carries the event, the plan's id
and title, the step's number and title, the counts of done, parked and total steps and a cause word, never a file's
contents or a check's output. It is edited in the file; a save from the Config panel keeps it.

### `weeklyProbe`

Off when missing. When it is on, the worker machines' context fit is measured once a week, inside a window you set, and the
fleet says so when a machine's fit has got worse since its last measurement. The measurement is the same one as the fit check
in the lab, started on a timer.

| Field | Meaning |
|---|---|
| `enabled` | `true` to measure the workers once a week. Missing or `false` means nothing is measured on a timer. |
| `dayOfWeek` | The day the window opens, as an English name such as `"sunday"`, in any letter case. Anything else means `"sunday"`. |
| `startHour` | The hour the window opens, `0` to `23`, in the machine's local time. Anything else means `2`. |
| `hours` | How long the window stays open, `1` to `12`. It may run past midnight. Anything else means `4`. |

A machine is measured when its newest measurement is more than six days old or there is none. The fleet measures one machine
at a time, and only while the window is open. A machine is skipped while a plan is running or approved, while it has a model
loaded in memory, or when the measurement is refused for another reason. A skipped machine is tried again at the next check,
every ten minutes, in the same window. No new measurement starts once the window has closed. A measurement that is already
running is not cut short by the window.

The hub is never measured this way. The hub is the machine the backend runs on, and the node the plans fall back to. Vision
machines and OpenAI-compatible servers are not measured either.

When a machine's fit is worse than at its previous measurement of the same model, the Setup tab shows a warning named "Weekly
fit measurement" with the reason, and, if `notifyUrl` is set, one message goes to that address. The message names the machine
and the model, gives the reason with the old and new numbers where it has them, and says it is about speed only, not coding skill. A regression is sent once:
one that is still there the following week is not sent again. The reasons are: the recommended context size went down by at
least one size, the model no longer fully fits on the graphics card, or the speed at one size fell by more than a quarter.
A first measurement, or a machine whose model has changed, is never a regression. The last run and any open regression are
kept in `weekly-fit-state.json`, next to the measurement history.

`GET /api/lab/weekly` shows these settings, the next window, the last complete run, and for each worker its last measurement
and any open regression (see the [HTTP interface](#http-interface)). The block is read and saved through
`GET` and `PUT /api/fleet-config` like the other blocks. The Setup tab of the Config panel in the web UI has the switch, the
day, start hour and window length, and this status.

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
| `FLEET_NETWORK_TIMEOUT_SECONDS` | 900 | How long to wait for one model reply (10 to 900). Ollama sends nothing while a model writes a tool call such as a whole file, so a slow machine is silent for as long as it takes to generate the answer (up to 4096 tokens: about 6 minutes at 11 tokens a second); a shorter wait throws that work away |
| `FLEET_HEALTH_PROBE_TIMEOUT_SECONDS` | 3 | Health probe timeout (1 to 30) |
| `FLEET_HEALTH_CACHE_SECONDS` | 10 | How often nodes are probed (1 to 300) |
| `SHELL_EXECUTION_TIMEOUT_SECONDS` | 120 | How long one `run_command` may run |
| `FLEET_PLAN_STEP_TOOL_ROUNDS` | 25 | How many rounds of tool calls one model turn at a plan step may make before it ends and the step's check decides (5 to 200) |
| `FLEET_PLAN_STEP_MINUTES` | 240 | The most one plan step may work (its model calls and checks, not waiting for a machine) before it stops with what it tried (5 to 1440). A backstop: what stops a step that is not getting anywhere is the repair ladder, and a step that is getting closer is not stopped for how long it has been at it |
| `FLEET_PLAN_ATTEMPT_MINUTES` | 30 | The longest one round of model work (its tool calls, not the check) may run (5 to 240). A slow machine, 11 tokens a second, needs most of 20 minutes for a round that writes a test file |
| `FLEET_PLAN_ROUNDS_PER_RUNG` | 3 | How many rounds (the model works, then the approved check runs) a plan step gets on each rung of the repair ladder before it climbs to the next (1 to 10) |
| `FLEET_CONTEXT_LENGTH` | 32768 | The most tokens of context a machine is asked for when it does not set `contextLength` |
| `FLEET_CONTEXT_FIT` | `classic` | The policy for shortening conversations that exceed the model's window: `classic` (the default), `maf-truncate` (Agent Framework's truncation), or `maf-collapse` (Agent Framework's truncation plus tool-result collapsing). Both Agent Framework policies are designed to change the start of the prompt only every few calls, which Ollama's prompt cache favours. Experimental; leave unset unless you are comparing policies. Applies to every machine's model calls except the routing model. |
| `FLEET_LAB_DIR` | `lab` beside the plans folder | Where fit-measurement history, scorecard notification state and imported benchmark results are stored. The Models tab of the Config panel shows the benchmark counts by machine and model and takes new imports; the counts are not a recommendation. |
| `FLEET_STEP_GUIDANCE` | unset | Set to `tests` to add experimental test-writing guidance to plan steps that list test files. |
| `SANDBOX_*` | | The same settings as the `sandbox` section, used when the file does not give them |
| `HUB_OLLAMA_URL`, `HUB_OLLAMA_MODEL`, `TRIAGE_OLLAMA_MODEL` | | Seed values for the first-ever start only |

### Fit measurement settings

The optional `lab` object in `fleet.config.json` controls the context-fit recommendation:

```json
"lab": {
  "minSpeedFraction": 0.70,
  "fullFitShare": 0.90,
  "constrainedSpeedFraction": 0.80
}
```

`minSpeedFraction` is the minimum fraction of the speed at the smallest tested context that a recommended size must keep. `fullFitShare` is the share of the model that must sit on the graphics card: a machine whose model fits at the smallest size is only offered sizes that still fit, and one that already spills at the smallest size is flagged "does not fully fit". For that second case `constrainedSpeedFraction` (the larger of it and `minSpeedFraction` applies) is the stricter speed floor, because each larger size spills more. The web UI words each size ("stays on the graphics card" or "runs partly on the processor") by the same `fullFitShare`, which the latest measurement carries. These defaults keep the recommendation speed-based; they do not score coding ability or rank models.

For the web UI (`agent-fleet\.env.local`, or the environment):

| Variable | Default | Meaning |
|---|---|---|
| `AGENT_URL` | `http://localhost:8000` | Where the backend is |
| `FLEET_WEB_HOST` | `127.0.0.1` | The address the web UI listens on. `0.0.0.0` lets other machines open it. |
| `PORT` | 3000 | The web UI's port |

For the PowerShell scripts in `scripts\`:

| Variable | Default | Meaning |
|---|---|---|
| `AGENT_FLEET_INSTALL_ROOT` | the configured install folder, else the standard application-data folder | Where `Install-Autostart.ps1` installs, and where `Deploy-Fleet.ps1`, `Test-Fleet.ps1`, `Add-FleetNode.ps1` and `Get-FleetModels.ps1` look. `-InstallRoot` on any of them wins. |

## Windows tray configuration

`Install-FleetTray.ps1` writes `fleet-install.json` in the install root, alongside the `backend` folder. The tray reads
this record to find the install and its two programs. It contains no credentials. When it is absent, the tray derives
the location and addresses from the registered backend and frontend, using the shared install resolver. An uninstalled
release folder with `backend\AgentFleet.exe` beside `scripts` can also be opened as a portable install.

| Field | Meaning |
|---|---|
| `installRoot` | Absolute install folder |
| `backendUrl` | Backend address used for health and plan checks |
| `webUrl` | Web UI address opened in the browser |
| `backendTask` | Backend scheduled task name, or service name in Service mode |
| `frontendTask` | Frontend scheduled task name, or service name in Service mode |
| `mode` | `Task` or `Service` |
| `version` | Version recorded by the installer |

`scripts\Install-FleetTray.ps1` adds or updates the tray registration without rebuilding or copying the fleet. It
supports `-InstallRoot`, `-BackendName`, `-FrontendName`, `-TrayName`, `-DesktopShortcut`, `-DryRun` and `-Uninstall`.
The task and shortcuts launch the tray directly with
`powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "<scripts>\tray\Fleet-Tray.ps1"`.
Shortcuts add `--open` and use a minimized window style. `--open` opens or starts the fleet and
`--close` closes it. `Install-Autostart.ps1` adds the tray unless `-NoTray` is supplied and removes it on uninstall.
Keep the scripts folder in place because the task and shortcuts run from it. Linux and macOS are unchanged.

For isolated scripted checks, set `FLEET_TRAY_NO_BROWSER=1` in the tray's environment. Open then records the web URL in
`<install-root>\tray-open-url.txt`, and the resident tray records its PID in `tray.pid` for cleanup. It launches no
browser. `-StartMenuFolder` and `-DesktopFolder` let installer checks keep shortcuts in a temporary folder.

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
| `POST /api/plans/{id}/approve`, `/reject`, `/stop` | Act on a plan. Approve accepts `recoveryScope` (`allow-hub-rescue`, the default, or `worker-only`), `healChecks` (`auto`, the default, or `ask`), `autoRetries` (0 to 5, default 2: how many times a parked step is retried by the fleet itself before it waits for you), and `review` (`auto` or `off`, default `off`: whether a second opinion reviewer reads each step's changed files) |
| `POST /api/plans/{id}/retry?step=`, `/skip?step=` | Try one parked or stopped step again with a fresh repair ladder, or count it as done without its check; the rest of the plan is left alone |
| `POST /api/plans` | Save a plan written elsewhere (the VS Code extension's Copilot tool uses it): `title`, `goal`, `workingDirectory` and `steps` as in `propose_plan`; each step may also include optional `retries` (0 to 5), `rescue` (`worker-only` or `allow-hub-rescue`), and `review` (`off` or `auto`) to override the plan's settings for that step only; unknown values are ignored. `dryRun` only reviews it, `approve` starts it. A plan that would fail is refused with its `problems` |
| `GET /api/approvals` | Chat tool calls waiting for an answer (see [`chatApprovals`](#chatapprovals)): each one's id, chat, tool, arguments, the times, and `secondsLeft` |
| `POST /api/approvals/{id}/approve`, `/reject` | Answer one waiting call. `200` with `{"decided": true}`, or `404` when the call is unknown or was already answered or timed out |
| `GET`, `PUT`, `DELETE /api/sessions[/{id}]` | Saved conversations (the visible chats of the durable record) |
| `GET /api/contexts[/{id}[/events|deliveries|artifacts|export]]`, `POST .../decisions|compact` | The durable record. See [context.md](context.md) |
| `GET /api/contexts/storage?olderThanDays=`, `POST /api/contexts/cleanup` | The record's size and a preview of a cleanup; deleting chats not used for a number of days |
| `GET /api/logs/tail?lines=` | End of today's log |
| `GET /api/setup` | The Setup tab's checklist: this computer's hardware, suggested models and every check with its fix |
| `POST /api/lab/fit` | Start a context-fit measurement with `{ "node": "name", "force": false, "confirmHub": false }`; returns `202` with the job id, or `409` with a refusal reason |
| `GET /api/lab/fit`, `GET /api/lab/fit/latest?node=name`, `POST /api/lab/fit/{id}/cancel` | List recent measurement jobs, get a node's latest result, or cancel a job |
| `GET /api/lab/weekly` | The weekly fit measurement (see [`weeklyProbe`](#weeklyprobe)): its settings, its next window, its last complete run, and each worker's last measurement and any open regression. Read only |
| `GET /api/lab/scorecards?days=7` | Count recent plan-step outcomes by machine and model; `days` defaults to 7 and must be 1 to 90 |
| `POST /api/lab/results/import` | Keep a finished benchmark batch: `{ "resultsJsonl": "...", "run": the run.json content (optional), "models": { "node": "model" } (optional, used when run.json names no machines), "label": up to 80 characters (optional) }`. Answers `{ imported, duplicates, skipped, models }`. A plan already stored counts as a duplicate, so the same file twice adds nothing. `413` for text over 2 MB or a body over 8 MB, `400` for an empty results file, a label over 80 characters, or a body that is not an import. See [importing results](../bench/README.md#importing-results) |
| `GET /api/lab/models?days=` | Benchmark counts by machine and model: runs, done and correct, false accepts, parked, timed out, errors, distinct tasks, too few to judge, and the last run, plus the imports they came from. `days` (1 to 3650) counts only runs that started in that window (the import time, when the batch start is not recorded); omitted means all. Counts only: no ranking, no fraction, no speed |
| `DELETE /api/lab/results/{importId}` | Remove one import. `200` with `{ removed }`, or `404` when there is no such import |
| `PATCH /api/fleet-config/nodes/{name}/context` | Change only one node's `contextLength` with `{ "contextLength": 24576, "ifVersion": "..." }`; a stale version returns `409`. A successful save creates the normal timestamped config backup. |
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
