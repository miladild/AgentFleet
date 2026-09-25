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
   setup with its new private IPv4 address (for example `Setup-Worker.ps1 -AllowFrom 192.168.1.11` on Windows or
   `sudo ./scripts/setup-worker.sh --allow-from 192.168.1.11` on Linux). Do not open Ollama to the whole subnet. The
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
  can be ten times slower. Use a smaller one, or check `ollama ps` on that machine to see how it is loaded.

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
folder (`C:\AgentFleet`). Do the same for a worker's Ollama folder if a worker behaves oddly.

A release download is new to your antivirus too, and its web UI is about 11,000 small files. On the very first start the
scanner may look at each one before it runs, so the web UI can take a minute or more to answer; later starts take a few
seconds. Add an exclusion for the folder you unpacked it to if that first start stalls.

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

- **Blocked at a step.** Open the plan: the step says how many attempts it took and the last problem. Look at the files, fix
  what is wrong (or the step's check), and press **Approve and resume**; finished steps are kept and it continues from
  the blocked one.
- **A check hangs.** It timed out after 120 seconds. Usually something keeps the process alive: a timer, a server, a watch
  mode. Reject the plan and ask for a check that finishes on its own.
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
