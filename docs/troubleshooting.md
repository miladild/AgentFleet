# Troubleshooting

Start with the web UI's **Config > Setup** tab: it checks this computer, every machine, the sandbox, the tools and the
network, says what to do about each problem, and has a button for the fixes it can make itself (downloading a model,
starting Ollama). From a terminal, `.\scripts\Test-Fleet.ps1` checks the same and also the VS Code extension. The web
UI's **Logs** button shows the backend's log live.

## A machine is red

Red means the backend could not reach that machine's Ollama or could not find its model. `Test-Fleet.ps1` (or the
backend's `/health` page) says why.

| Reason | What it means | Fix |
|---|---|---|
| `timeout` | Nothing answered | The machine is off, asleep, on another address, or a firewall drops the hub. See below. |
| `model_missing` | Ollama answers but the model is not installed | The machine's card in **Config > Machines** has a **Download** button. Or on that machine: `ollama pull <model>` |
| `unreachable` | The connection was refused or the address does not exist. The machine is up but Ollama is not listening on the network, or the address is wrong | Set `OLLAMA_HOST=0.0.0.0` and restart Ollama. `Setup-Worker.ps1` does this. |
| `http_error` | Something answered, but not like Ollama | Check the port and that the address is Ollama's |

### Red at night, green in the morning

Almost always one of three things:

1. **The hub's address changed.** Routers hand out addresses that change. If a worker's firewall allows only the hub's old
   address, the worker silently drops everything from the new one, while other things (SSH, ping) may still work. Give the
   hub and every worker a fixed address with a DHCP reservation in your router. If the hub must move, rerun the worker
   setup with its new private IPv4 address (for example `Setup-Worker.ps1 -AllowFrom 192.0.2.10` on Windows or
   `sudo ./scripts/setup-worker.sh --allow-from 192.0.2.10` on Linux). Do not open Ollama to the whole subnet. The
   backend logs a warning when the address it uses to reach a worker changes.
2. **The worker slept.** Turn off sleep (`Setup-Worker.ps1 -KeepAwake`) and prefer a wired connection.
3. **Wi-Fi power saving.** Linux laptops and mini PCs put Wi-Fi to sleep. `setup-worker.sh --wifi-powersave-off`.

### Windows firewall rules from Ollama's installer

Ollama's Windows installer can add an inbound "allow" rule for `ollama.exe` that admits every address. A stricter port
rule next to it then does nothing. `Setup-Worker.ps1 -AllowFrom <hub-private-ip>` now narrows all enabled inbound Ollama
rules to the named peer IPs. `-RestrictOllamaRules` remains accepted for compatibility.

## The web UI opens but chat fails

- **"Refused: the fleet only answers requests addressed to..."** You reached the backend by a host name it does not know.
  Use `localhost` or an IP address, or list the name in `FLEET_ALLOWED_HOSTS`.
- **`fetch failed` or a stream error.** The machine that was chosen is unreachable. Check the panel on the left; requests
  for a down machine normally go to the fallback node, but a vision request has no fallback (nothing else can read images).
- **Nothing happens for minutes.** The model may be spilling out of graphics memory. A model bigger than your card's memory
  can be ten times slower. Use a smaller one, or check `ollama ps` on that machine to see how it is loaded: the
  `CONTEXT` column is the size the fleet asked for, and a `PROCESSOR` split such as `20%/80% CPU/GPU` means it did not
  fit. Lower that machine's `contextLength` ([configuration.md](configuration.md#nodes)) or use a smaller model. A machine
  that times out rests for ten minutes (the Setup tab says so) while the fallback answers.
  If `CONTEXT` shows a huge number such as 262144 with only part of the model on the GPU, something other than the fleet
  loaded it: Ollama's own default window on a new version is the model's maximum, and any program that asks without a size
  gets it, and every change of size makes Ollama reload the model (10 to 25 seconds). The fleet always asks for a size,
  and its own health checks now ask at the size already loaded. To make Ollama's default sane on a worker, set
  `OLLAMA_CONTEXT_LENGTH=32768` in that machine's Ollama environment and restart Ollama.
- **The hub's large model stays loaded all day and slows the computer it runs on.** A routine health check never loads the hub's
  own model (the node marked as the fallback): it asks only while the model is loaded anyway, or when a plan asks after an outage.
  If `ollama ps` still shows the hub model with nobody using it, something else is asking it: another program, or a fleet backend
  from an older build. Ollama unloads a model after five minutes unused (`OLLAMA_KEEP_ALIVE` changes that).
- **A file the model wrote ends with "[the rest was removed to keep this conversation inside the model's window]".** That text
  stands in for old tool output the fleet shortened when a conversation outgrew the model's window, and a small model can copy
  it into a file. The fleet refuses a write that contains the text, and it no longer changes the calls the model sees in its
  own history (a model copies their shape: an old write shown without its content made the next write have none), leaving
  out the oldest calls and their results instead. If you still see the text, the conversation is too long for that machine's
  window: raise its `contextLength` or use a model with a longer one.
- **A file called `nul` (or `con`, `aux`, `prn`, `com1`, `lpt1`) appears in a Linux worker's project.** The model ran a
  Windows command such as `> nul` on Linux. A Windows hub cannot hold such a file, so it is left on the worker and not
  synced; it does no harm and goes with the workspace.
- **A small model writes nothing for minutes, round after round.** A model that thinks before it answers can spend its
  whole answer (4096 tokens, minutes on a slow machine) on thinking and end the turn with no tool call and no text; the
  plan log shows a round with no edit and the step climbing to the hub. Nodes are told not to think by default
  (`thinking`, [configuration.md](configuration.md#nodes)); if you set a node to `model`, set it back to `off`.
- **The model forgets the task, ignores the tools, or answers something else.** Most often the conversation did not fit in
  what the model may see. The fleet sizes each request for Ollama; if the log says a request "needs about N tokens but
  the most it may use is M", give that machine a larger `contextLength` (if its memory allows) or a model with a longer
  window. A machine set to `"api": "openai"` chooses its own size, which may be small.

## The page says “This page couldn’t load” or has no styling

A successful response from `/` only means the server returned HTML. If the browser shows Next.js's generic error page, open
the browser console and check whether `/_next/static/...` files return 404. That points to a missing or mixed web build,
not a stopped backend.

Development and production must use separate Next.js output directories. `scripts/serve.mjs dev` and Playwright use
`.next-dev`; production uses `.next`. If you invoke `next dev` directly, set `NEXT_DIST_DIR=.next-dev`. To restore the
production UI, run `npm run build` from `agent-fleet`, then restart the web UI process or service. The backend does not
need a restart for a missing front-end chunk.

## The backend will not start

Look at the end of the log (`logs\agent-<date>.log` next to the program) or run it in a terminal (`npm run dev:agent`).
Common causes:

- `fleet.config.json` is invalid. The message names the problem.
- Port 8000 is in use. Something else (or an old copy) holds it: `Get-NetTCPConnection -LocalPort 8000`.
- A node URL is not an Ollama address. It must be `http://host:11434/v1`.

**Antivirus.** The backend is a program you built yourself, so it is unsigned and brand new to your antivirus, and it starts
other programs and talks to the network, which is exactly what security suites watch for. Norton in particular has stopped
or held it more than once while this was developed: the symptom was a backend that started but did not answer some
requests, and setup steps that stalled. If the backend hangs or vanishes right after it is built or published, look in your
antivirus's history or quarantine first, restore it, and add an exclusion for the repository folder and for the install
folder (`<install-root>`). Do the same for a worker's Ollama folder if a worker behaves oddly.

A release download is new to your antivirus too, and its web UI is about 11,000 small files. On the very first start the
scanner may look at each one before it runs, so the web UI can take a minute or more to answer; later starts take a few
seconds. Add an exclusion for the folder you unpacked it to if that first start stalls.

**It stopped and did not come back.** The two scheduled tasks start at your logon and then again every five minutes while
they are not running, so a backend or web UI that was stopped, crashed, or gave up (its port still held, its disk not yet
mounted) is started again within five minutes, as long as you are logged in. A task is switched off while
`Deploy-Fleet.ps1` replaces it and on again afterwards. Check with `Get-ScheduledTask AgentFleetBackend, AgentFleetFrontend`:
the state should be `Running` or `Ready`, not `Disabled`. A running task can show the last result `0x800710E0`: that is
the repeat being ignored because it is already running, not an error. An older install gets the repeat by running
`.\scripts\Install-Autostart.ps1 -SkipBuild` again. After a reboot nothing starts until you log in; see
[security.md](security.md) before changing that.

If it runs fine in a terminal but not as a Windows service, the service's account is different from yours: it has a different
user profile, different environment variables and a different `PATH`. The scheduled-task install runs as you and avoids
this.

## The code sandbox

**Config > Sandbox**, **Test** names the failing step and the fix. The usual ones:

- **"did not accept the fleet's key"**: the key is not on that machine yet, or the account name is wrong. Use **Add the
  key to that machine**, or add the public key by hand.
- **"not allowed to use Docker"**: on that machine, `sudo usermod -aG docker <account>`, then sign out and back in.
- **"sudo asked for a password"**: untick **use sudo** and use the docker group instead.
- **"Docker is installed but not running"**: start Docker Desktop, or `sudo systemctl start docker` on Linux.
- **"identity ... is not the one the fleet remembered"**: the machine at that address has a different SSH host key. If you
  reinstalled it, **Test** and **Save** again. If you did not, find out what is answering at that address before you do.
- The first call timed out: the container images were still downloading. **Test**, then **Download them now**.

## A tool does nothing, or the model says it cannot

- **The model says it cannot do something a tool can.** Small models under-claim. Say so ("you can run any command with
  run_command") and ask again, or split the request.
- **A tool call failed with a message.** Read it: it is the real error. Tools return errors to the model, which usually
  corrects itself.
- **`run_sandboxed_code` is missing.** No Docker was found. Install Docker Desktop, or set `sandbox` to an SSH host. See
  [tools-and-mcp.md](tools-and-mcp.md#the-sandbox).
- **An MCP server's tools are missing.** The Config panel shows why it did not start, including what it printed. Common
  causes: `npx` not installed, a missing `${env:...}` variable, a wrong URL.
- **`web_search` returns nothing.** DuckDuckGo rate-limits scripted requests and answers with a challenge page when it is
  hit too fast. Wait a few minutes.

## Plans

- **Blocked, with a parked step.** The plan stopped because nothing more could run. A parked step is retried automatically
  first with a fresh repair ladder before you are notified; each plan has a default budget of 2 automatic retries per step
  (settable 0 to 5 when approving), shown in the run log as "step retried" lines. Once the budget is used the step is parked
  and you are notified. Open it (or `@fleet /status` in VS Code): the report says which step is parked and why, which steps
  waited for it, and what to do. Fix the cause, then **retry step** (a fresh repair ladder with a new budget) or **skip step**
  (it counts as done without its check). **Approve and resume** retries every parked step. Steps that did not depend on the
  parked one have already run.
- **"Waiting ... before retrying" in the run log.** No machine could answer, or a worker could not be reached. The step waits,
  asks the machine for a small real answer each time it wakes (and every minute during a long pause, ending the pause as soon as
  the machine answers), and carries on by itself; waiting costs no attempt. It keeps going until the run deadline, so a night-long outage is survived and nothing needs doing unless it outlasts the deadline.
- **"The check was changed" (check healed) in the run log.** The step's saved check could not run or never finished, and Fleet
  replaced it by one that still tests the work. The report lists the old and the new check under "Checks changed by Fleet". If
  you would rather be asked first, approve the plan with **Broken checks: Ask me first**.
- **A step stopped with "the fleet could not change it by itself".** Its check is broken and no replacement passed the rules
  (it would have passed without the work, or tested less). Change the step's check to one that finishes and tests the result,
  or skip the step, then approve again.
- **A step is parked with cause `no-change`.** The step names files to change, but nothing was changed and its check passes
  anyway, so the check cannot show that the work was done. If the step names test files, at least one of them must also have
  changed. The step is automatically retried first (with a fresh repair ladder and budget); once retries are spent you are
  notified. Retry the step again to give the model another go with a new budget, or skip it if the work is already in place.
  If it happens often, the plan's check is too weak: it should fail until the work is done (for example a test the step adds).
- **A step is parked with cause `review`.** The step's check passes, but a model that did not write the code read the changed
  files against the step's text and found a requirement unmet. Its numbered problems went back to the step's model like a
  failing check (each one is a "step review" line in the run log), the step climbed the repair ladder, and the plan's
  automatic retries are used up. Read the problems: if they are right, retry the step; if the reviewer is wrong, skip the step.
  Where the reviewer is more trouble than help, choose **Off** under **Second opinion** when approving.
- **"Worker workspace unavailable: existing worker file ... differs from the hub checkout".** The worker's copy of the
  project holds a change that never reached the hub (the backend stopped in the middle of a step, or someone edited the
  folder on the worker), so Fleet will not overwrite it. A file Fleet itself put there and nobody touched since is replaced
  without asking, which includes the files of a round Fleet undid. To go on, copy over what you want to keep, or delete that
  plan's folder under the worker's workspace root, then **retry step**.
- **"Blocked by unattended plan policy".** A step tried a command that only a person may run: git, a remote service, a delete of
  folders, a change to a security setting or to the machine's configuration. The command did not run. If the project really
  needs it, do it yourself and retry the step.
- **A check fails on a Windows worker with "running scripts is disabled on this system".** PowerShell's execution policy blocks
  a program's script (npm's, for example). Fleet runs the same command through `cmd` instead; it never changes the policy. You
  can also set the policy yourself for the worker account, for example `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`,
  if you accept what that allows.
- **A step failed because the machine lacks something** ("is not recognized", "command not found"). A missing runtime
  (`node`, `dotnet`, `python`) is a worker setup problem: Fleet tries another ready worker of the same tier and otherwise
  parks the step naming the worker. A missing project dependency is installed once with the project's own command.
- **The plan was sent back while planning** ("The plan was NOT saved, because it would fail when it runs"). That is the
  fleet catching a bad check, or a dependency on an unknown step, on itself or on a later step, before the night; the planner
  fixes it and proposes again. If it keeps failing, say in your request how the project is tested (for example "tests run with
  `npm test`").
- **A check hangs.** It timed out after 120 seconds. Usually something keeps the process alive: a timer, a server, a watch
  mode. After two identical timeouts Fleet audits the check and may replace it with one that finishes; otherwise the step is
  parked.
- **I did not hear about it.** Notices are one line in the run log and one message in VS Code per newly parked step. For
  messages elsewhere, set `notifyUrl` in the fleet configuration (see [configuration.md](configuration.md#notifyurl)).
- **The plan has no diagram.** The model's diagram failed validation twice, or the web UI was not reachable from the
  backend (`FLEET_FRONTEND_URL`). The plan says so.
- **Planning is slow.** It runs on your strongest model. See "Nothing happens for minutes" above.

## VS Code: `@fleet` does nothing

- If typing `@fleet` shows a list of files instead of a chat participant, the extension is not loaded in that VS Code build.
  `code --list-extensions` and `code-insiders --list-extensions` show which build has it. Install with
  `.\scripts\Install-VSCodeExtension.ps1` and **fully close and reopen** VS Code.
- "Could not reach the fleet backend": set `agentFleet.backendUrl` in VS Code's settings. On the hub it is
  `http://localhost:8000`; from another machine it is the hub's address and the hub must listen on the network
  ([security.md](security.md)).

## Still stuck

Open an issue with the output of `Test-Fleet.ps1` and the last lines of the backend log. Remove anything private first: the
log can contain file paths and command text.
