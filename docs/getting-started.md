# Getting started

This gets one Windows machine running the fleet, with one model, and shows you how to use it. Adding more machines is a
separate step ([adding-machines.md](adding-machines.md)) and can wait.

## What you need

- Windows 10 or 11.
- About 10 GB of free disk space for the tools and one model.
- A graphics card with 8 GB or more of memory is best. Without one it still works with a smaller model, more slowly.
- An internet connection for the first setup (packages and the model). After that the fleet works offline, except for
  web search.

The setup script installs .NET 9, Node.js and Ollama with winget if they are missing.

## The quickest way: a download

If you only want to use the fleet, not change it, take the ready-built download from the
[Releases page](https://github.com/miladild/AgentFleet/releases) and skip steps 1 and 2 below:

1. Install [Node.js 20 or newer](https://nodejs.org), and [Ollama](https://ollama.com) if this computer will run a
   model. No .NET, no build tools. (Forgot Ollama? The web UI tells you.)
2. Unpack `AgentFleet-<version>-win-x64.zip` (or the `linux-x64.tar.gz`) somewhere it can stay.
3. Windows: double-click **Start Agent Fleet.cmd** in that folder. Linux or macOS: `bash scripts/start-fleet.sh`. Keep
   the window open; the web UI opens in your browser when it is ready (<http://localhost:3000>).
4. The first time, a **Welcome** box points to [the Setup tab](#the-setup-tab). It suggests a model that suits this
   computer and downloads it with one button.

`.\scripts\Install-Autostart.ps1` (Linux: `bash scripts/install-autostart.sh`) starts it at every logon from that folder
instead. To update, stop it, unpack the new version over the old one and start it again: your settings and conversations
are not in the download, so they are kept. Then continue with [step 3](#3-ask-something).

## Linux or macOS

This guide's steps below use Windows PowerShell. On Linux or macOS, install the .NET 9 SDK, Node.js 20 or newer (including
npm), and Ollama from their official download pages, then run the shared hub setup script from the repository root:

```sh
bash scripts/setup-hub.sh
```

The script installs the web UI packages, builds the backend, creates local config files only if absent, and offers to pull
the selected coding model. It does not install system packages or change firewall and listening-address settings. Start
the hub with `cd agent-fleet && npm run dev`, then open <http://localhost:3000>. Keep it running as your normal user and
read [security.md](security.md) before making it reachable from another computer.

To have it start by itself when you log in, see [Run it all the time](#run-it-all-the-time).

## 1. Set up the hub

Open PowerShell in the folder you cloned and run:

```powershell
.\scripts\Setup-Hub.ps1
```

It asks before each install or download. It:

1. checks for .NET 9, Node.js 20+ and Ollama (and installs what is missing),
2. installs the web UI's packages and builds the backend,
3. creates `agent-fleet\.env.local` and `agent-fleet\fleet.config.json` (this machine is the only node),
4. downloads a coding model chosen for your graphics memory (`qwen2.5-coder:7b` for an 8 to 24 GB card),
5. offers to install the `@fleet` VS Code extension.

If it says a tool was installed but is not on PATH yet, open a new PowerShell window and run it again. You can pick a
different model with `-Model`, for example `.\scripts\Setup-Hub.ps1 -Model qwen2.5-coder:14b`.

## 2. Start it

```powershell
cd agent-fleet
npm run dev
```

This starts the backend on <http://localhost:8000> and the web UI on <http://localhost:3000>. Open the web UI. The panel
on the left shows your machine with a green dot when it is ready.

To have it start by itself when you log in, see [Run it all the time](#run-it-all-the-time).

### The Setup tab

**Config**, **Setup** is a checklist of everything the fleet needs, the same checks `Test-Fleet.ps1` runs, each with its
fix. Where the fleet can do the fix itself there is a button:

- **This computer**: its memory, graphics card and free disk space, and three models that suit it, with the recommended
  one marked. **Download and use** downloads one (with a progress bar) and switches this computer's machine to it; a model
  that is already downloaded has **Use it**.
- **Machines and models**: whether Ollama runs here (with **Start Ollama** when it is installed but stopped), whether each
  machine answers and has its model (**Download** when it does not), and the routing model when you have more than one
  machine.
- **Code sandbox**: whether `run_sandboxed_code` can run, and a link to its settings. See
  [tools-and-mcp.md](tools-and-mcp.md#the-sandbox), which covers using Docker on another machine over SSH.
- **Tools**: programs your tools need that are missing (git, Node.js for `npx` servers, `uv` for `uvx` servers), and MCP
  servers that did not start.
- **Network and security**: whether other computers can reach the fleet. There is no login, so this matters.

Until the fleet can answer, a box above the chat list says what is missing and opens this tab.

## 3. Ask something

Try these in order:

1. `What does the % operator do in Python?` A quick answer, no tools.
2. `In C:\path\to\your\project, list the files and tell me what this project does.` It reads your files.
3. `In C:\path\to\your\project, find where <something> is handled.` It searches.

Give the full path to the project. The assistant works on the hub machine's own files.

Easier: type the project's folder once under **Project folder** on the main page (or tell the assistant "work on
C:\path\to\your\project" and it sets it). Every message then carries it, so "run the tests" or "where is login handled?"
work without a path, and the chat offers suggestions for that project. The folder is remembered in your browser.

Under each answer, the thumbs up and down are kept with the machine that answered; **Config > Machines** shows the
count on each machine, which helps when deciding which model to keep. When you ask how to set something up, the
assistant can open the right settings tab for you.

## 4. Try plan mode

Switch **Plan mode** on (top right) and ask for a change:

> In C:\path\to\your\project, add input validation to the signup form.

The assistant explores with read-only tools and proposes a plan as a card. Read it, then press **Approve and run**, or
tell it what to change. Read [planning.md](planning.md) for what happens next.

## Pin something the fleet must remember

Open the web UI's **Context** button while a chat is open. Under "Pin something every agent must respect", type a decision
(for example "use CommonJS, not ES modules") and press **Pin**. From then on every agent that continues that work receives it,
on any machine, including the steps of a plan running overnight. [context.md](context.md) explains what is recorded.

## 5. Check that everything is healthy

```powershell
.\scripts\Test-Fleet.ps1
```

It lists what passed and what to fix, with the command for each problem.

## Use it from VS Code

If you use VS Code Chat:

```powershell
.\scripts\Install-VSCodeExtension.ps1
```

Fully close and reopen VS Code. You can type `@fleet hello` in the chat panel, or
choose **Agent Fleet · Fleet Router** from the model picker. Both call your
backend and generate with its configured fleet models. Enabled web tools can
still contact their configured external services. Details are in
[vscode-fleet/README.md](../vscode-fleet/README.md).

## Run it all the time

```powershell
.\scripts\Install-Autostart.ps1
```

This builds the web UI, publishes the backend to `C:\AgentFleet` (another folder: `-InstallRoot D:\AgentFleet`, or set the `AGENT_FLEET_INSTALL_ROOT` environment variable so every script finds it), and registers two scheduled tasks that start when you
log in and run as you. It needs no administrator rights. Later, `.\scripts\Deploy-Fleet.ps1` updates the running copy
after you change the code, and `.\scripts\Install-Autostart.ps1 -Uninstall` removes it.

If you want it running before anyone logs in, use `-Mode Service` from an elevated PowerShell, and read
[security.md](security.md) first: a Windows service runs as a different account by default.

On Linux or macOS, after `setup-hub.sh`:

```sh
bash scripts/install-autostart.sh
```

It builds the web UI, publishes the backend to `~/.local/share/agent-fleet` (on macOS
`~/Library/Application Support/AgentFleet`), and registers two user services that start when you log in and run as you:
systemd user units on Linux, launchd agents on macOS. It uses no sudo and changes no firewall. Run it again after you change
the code to update the running copy; `--uninstall` removes the services and leaves your config and conversations in place.
On Linux, `--at-boot` also starts them at boot, before you log in. `--dry-run` shows the unit files and every command
without changing anything. The logs are in `journalctl --user -u agent-fleet-backend` (Linux) or next to the install
(macOS), and in the backend's `logs` folder.

The Linux path was tested on Ubuntu 24.04 (install, reboot, update, `--at-boot`, uninstall). The macOS path is written to
the same plan but has not been run on a Mac yet.

## Where things live

| What | Where (development run) | Where (installed, default folder) |
|---|---|---|
| Machines, models, tools, settings | `agent-fleet\fleet.config.json` | `C:\AgentFleet\backend\fleet.config.json` |
| Conversations | `agent-fleet\data\sessions\` | `C:\AgentFleet\backend\sessions\` |
| Plans | `agent-fleet\data\plans\` | `C:\AgentFleet\backend\plans\` |
| Durable record (what the fleet remembers) | `agent-fleet\data\contexts\` | `C:\AgentFleet\backend\contexts\` |
| Logs | `agent\bin\...\logs\` | `C:\AgentFleet\backend\logs\` |

Installed on Linux, the same folders are under `~/.local/share/agent-fleet/backend/`; on macOS under
`~/Library/Application Support/AgentFleet/backend/`.

The web UI's **Logs** button shows the backend log, and **Config** edits machines, tools, MCP servers and how long chats
are kept.

## If something does not work

Run `.\scripts\Test-Fleet.ps1` first. Then see [troubleshooting.md](troubleshooting.md). If you run a security suite
(Norton, McAfee and similar), it may hold or quarantine the freshly built backend: see "Antivirus" in the troubleshooting
page.
