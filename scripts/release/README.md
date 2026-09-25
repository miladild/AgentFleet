# Agent Fleet

A coding assistant that runs on your own machines. This download is ready to run: nothing to build.

## What you need

- **Node.js 20 or newer** (<https://nodejs.org>) for the web UI.
- **Ollama** (<https://ollama.com>) on this machine, if it will run a model. You do not need to download a model first:
  the web UI does that for you.

The backend is included with its own runtime; you do not need .NET.

## Start it

Windows: double-click **Start Agent Fleet.cmd** in this folder. Keep its window open while you use the fleet; close it to
stop the fleet.

Linux:

```sh
bash scripts/start-fleet.sh
```

The web UI opens in your browser when it is ready (<http://localhost:3000>). The first time, a **Welcome** box leads to
**Config > Setup**, which suggests a model that suits this computer and downloads it with one button, and lists anything
else that needs doing. More machines are added under **Config > Machines**; it shows the command to prepare each one.

From PowerShell instead: `.\scripts\Start-Fleet.ps1` (it takes `-BackendPort`, `-FrontendPort`, `-WebOnLan`,
`-NoBrowser`). The starter unblocks the downloaded files the first time (Windows marks files from the internet).

The very first start of a new download can be slow: the web UI is about 11,000 small files, and antivirus software
may scan each one before it runs. If <http://localhost:3000> does not answer after a minute, give it another minute;
the next starts take a few seconds. `docs/troubleshooting.md` ("Antivirus") explains how to add an exclusion.

## Keep it running

```powershell
.\scripts\Install-Autostart.ps1        # Windows: starts at logon, as you, no administrator rights
```

```sh
bash scripts/install-autostart.sh      # Linux: systemd user services, as you
```

Both run the fleet from this folder, so keep it where it is. `-Uninstall` / `--uninstall` removes the autostart.

## Update

Stop the fleet (Ctrl+C, or `Install-Autostart.ps1 -Uninstall`), unpack the new version over this folder, and start it
again. Your settings, conversations and plans (`backend/fleet.config.json`, `backend/contexts`, `backend/plans`,
`backend/logs`) are not part of the download, so they are kept.

## More

- `scripts/Test-Fleet.ps1` checks every machine and the running fleet.
- `docs/getting-started.md` is the full walk-through; `docs/adding-machines.md` adds more computers;
  `docs/security.md` explains what the fleet can do on your machine. Read it before you open the fleet to the network:
  there is no login.
- The VS Code extension (`agent-fleet-chat-<version>.vsix`) is a separate download on the same release page: in VS Code,
  Extensions, the `...` menu, **Install from VSIX**.

Source, issues and releases: <https://github.com/miladild/AgentFleet>. License: MIT (see `LICENSE`).
