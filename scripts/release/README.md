# Agent Fleet

A coding assistant that runs on your own machines. This download is ready to run: nothing to build.

## What you need

- **Ollama** (<https://ollama.com>) on this machine, with a coding model, for example `ollama pull qwen2.5-coder:7b`.
- **Node.js 20 or newer** (<https://nodejs.org>) for the web UI.

The backend is included with its own runtime; you do not need .NET.

## Start it

Windows (PowerShell, in this folder):

```powershell
.\scripts\Start-Fleet.ps1
```

Linux:

```sh
bash scripts/start-fleet.sh
```

Open <http://localhost:3000>. The first start writes `backend/fleet.config.json` with one machine, this one, using
`qwen2.5-coder:7b`. Change the model, and add more machines, in the web UI under **Config**.

If PowerShell says running scripts is disabled: `powershell -ExecutionPolicy Bypass -File .\scripts\Start-Fleet.ps1`.
A downloaded zip may be marked as blocked; `Get-ChildItem -Recurse | Unblock-File` in this folder clears that.

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
