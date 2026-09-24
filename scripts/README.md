# Scripts

Windows scripts are PowerShell and run in Windows PowerShell 5.1 or PowerShell 7. Scripts that change something have a
dry-run option (`-DryRun` in PowerShell, `--dry-run` in shell scripts). Get PowerShell help with
`Get-Help .\scripts\<name>.ps1 -Full`, or use `--help` for shell scripts.

| Script | Run it | What it does |
|---|---|---|
| `Setup-Hub.ps1` | on the hub, once, as yourself | Installs .NET, Node and Ollama if missing, installs packages, builds, creates the config, downloads a model that suits the machine |
| `setup-hub.sh` | on a Linux or macOS hub, as yourself | Checks .NET 9+, Node.js 20+ and Ollama; installs packages, builds, creates local config if missing, optionally downloads a model |
| `Setup-Worker.ps1` | on each Windows worker, elevated | Requires private IPv4 peer addresses, installs Ollama, binds it to the network, scopes all inbound Ollama rules to those peers, optionally stops sleep |
| `setup-worker.sh` | on each Linux worker, with sudo | Requires one private IPv4 peer and an active, restrictive `ufw` or `firewalld` policy; checks existing rules, adds peer-scoped access, installs Ollama, optionally turns off Wi-Fi power saving |
| `Add-FleetNode.ps1` | on the hub | Checks a worker, downloads the model onto it over the network, adds it to the fleet |
| `Test-Fleet.ps1` | on the hub, any time | The health check: tools, config, every machine, the running backend and UI, the VS Code extension |
| `Get-FleetModels.ps1` | on the hub | Every model installed on every machine |
| `Install-VSCodeExtension.ps1` | on any machine with VS Code | Builds and installs `@fleet` into VS Code and Insiders |
| `Install-Autostart.ps1` | on the hub | Builds, publishes and registers the fleet to start at logon (scheduled tasks) or boot (services) |
| `Deploy-Fleet.ps1` | on the hub, after changing code | Rebuilds and restarts what `Install-Autostart.ps1` installed |
| `install-autostart.sh` | on a Linux or macOS hub, as yourself | Builds, publishes and registers the fleet as user services that start at login (systemd user units, launchd agents); run again to update, `--uninstall` to remove |
| `FleetCommon.ps1` | not run directly | Helpers the other scripts share |
| `workstation/` | optional | Hardware inventory and Windows tuning helpers for an AI workstation. Not needed to run the fleet. |

## The usual order

1. Hub: Windows: `Setup-Hub.ps1`; Linux or macOS: `bash scripts/setup-hub.sh`. Then `cd agent-fleet; npm run dev`.
2. For each extra machine: `Setup-Worker.ps1` (or `setup-worker.sh`) there, then `Add-FleetNode.ps1` on the hub.
3. `Test-Fleet.ps1` whenever something looks wrong.
4. When you are happy: `Install-Autostart.ps1`, later `Deploy-Fleet.ps1` after each update. They install to `C:\AgentFleet`;
   for another folder pass `-InstallRoot` once, or set `AGENT_FLEET_INSTALL_ROOT`. The other scripts find the installed copy
   from the registered service or task. On Linux or macOS:
   `bash scripts/install-autostart.sh`, run again after each update.

## Line endings and execution policy

If PowerShell refuses to run a script ("running scripts is disabled"), run it as
`powershell -ExecutionPolicy Bypass -File .\scripts\<name>.ps1`, or allow scripts for your user once with
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`. Files downloaded as a zip may be marked as blocked; `Unblock-File
.\scripts\*.ps1` clears that. `setup-worker.sh`, `setup-hub.sh` and `install-autostart.sh` need LF line endings (git does this for you through `.gitattributes`).

### Linux and macOS hub prerequisites

Install the .NET 9 SDK, Node.js 20 or newer (including npm), and Ollama using their official download pages before running
`setup-hub.sh`. The script does not add package repositories or install system packages. It builds the web UI dependencies
and backend, creates `.env.local` and `fleet.config.json` only when they are absent, and offers to pull a model if Ollama
is running. It never opens a firewall, changes listening addresses, or overwrites existing settings. Run it as your normal
user, not with `sudo`.

```sh
bash scripts/setup-hub.sh
# Or, with a selected model and no model download:
bash scripts/setup-hub.sh --model qwen2.5-coder:7b --skip-model
```

Use `--dry-run` to see the build/configuration actions, and `--yes` to approve the model download in a non-interactive
session. For resource sizing and security details, read [security.md](../docs/security.md).
