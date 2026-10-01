# Scripts

Windows scripts are PowerShell and run in Windows PowerShell 5.1 or PowerShell 7. Scripts that change something have a
dry-run option (`-DryRun` in PowerShell, `--dry-run` in shell scripts). Get PowerShell help with
`Get-Help .\scripts\<name>.ps1 -Full`, or use `--help` for shell scripts.

| Script | Run it | What it does |
|---|---|---|
| `Install-FleetToolchains.ps1` | Windows hub or coding worker, elevated, only when runtimes are missing | Installs fixed, machine-wide .NET 9+, Node.js 20+ and Python 3.13+ packages; does not run project scripts or builds. `-AuditOnly` checks machine PATH without admin rights |
| `Setup-Hub.ps1` | on the hub, once, as yourself | Checks machine-wide runtimes and Ollama, installs UI packages, builds, creates the config, downloads a model that suits the machine |
| `setup-hub.sh` | on a Linux or macOS hub, as yourself | Checks .NET 9+, Node.js 20+, Python 3.13+ (`python3`) and Ollama; installs packages, builds, creates local config if missing, optionally downloads a model |
| `Setup-Worker.ps1` | on each Windows worker, elevated | Requires private IPv4 peer addresses, installs Ollama, binds it to the network, scopes all inbound Ollama rules to those peers, optionally stops sleep; `-InstallToolchains` also installs common coding runtimes |
| `setup-worker.sh` | on each Linux worker, with sudo | Requires one private IPv4 peer and an active, restrictive `ufw` or `firewalld` policy; checks existing rules, adds peer-scoped access, installs Ollama, optionally turns off Wi-Fi power saving |
| `Setup-WorkerWorkspace.ps1` | on each Windows worker, elevated | Creates a standard SSH account with a public key and grants it a dedicated `AgentFleet\Workspaces` folder; refuses broader paths and reparse-point paths, and does not change SSH service or firewall settings. `-DryRun` previews it |
| `setup-worker-workspace.sh` | on each Linux worker, with sudo | Creates a standard, non-sudo account and grants it a workspace folder beneath that account's home; canonicalizes the path and does not change SSH service or firewall settings. Pass `--dry-run` as the fourth argument to preview |
| `Add-FleetNode.ps1` | on the hub | Checks a worker, downloads the model onto it over the network, adds it to the fleet |
| `Test-Fleet.ps1` | on the hub, any time | The health check: tools, config, every machine, the running backend and UI, the VS Code extension |
| `Get-FleetModels.ps1` | on the hub | Every model installed on every machine |
| `Install-VSCodeExtension.ps1` | on any machine with VS Code | Builds and installs `@fleet` into VS Code and Insiders |
| `Install-Autostart.ps1` | on the hub | Default Task mode: run from a normal PowerShell; builds, publishes and registers limited logon tasks. Service registration needs elevation and `-SkipBuild`, so project code is never built elevated. Both modes restrict the install folder to the Fleet user, SYSTEM and local Administrators |
| `Deploy-Fleet.ps1` | on the hub, after changing code | Rebuilds and restarts what `Install-Autostart.ps1` installed |
| `Start-Fleet.ps1`, `start-fleet.sh` | in a release download | Starts the downloaded backend and web UI until Ctrl+C and opens the web UI when it is ready (`-NoBrowser` / `--no-browser` to skip). `Start Agent Fleet.cmd` in a Windows download runs it with a double-click. `Install-Autostart` runs a download in place too |
| `Build-Release.ps1` | when making a release (PowerShell 7) | Builds the downloads: the fleet for one platform, and the VS Code extension. GitHub Actions publishes when `agent-fleet/package.json` gets a new version on `main`; pushing a matching `v*` tag also publishes. Manual workflow runs build without publishing |
| `install-autostart.sh` | on a Linux or macOS hub, as yourself | Builds, publishes and registers the fleet as user services that start at login (systemd user units, launchd agents); run again to update, `--uninstall` to remove |
| `FleetCommon.ps1` | not run directly | Helpers the other scripts share |
| `workstation/` | optional | Hardware inventory and Windows tuning helpers for an AI workstation. Not needed to run the fleet. |

## The usual order

1. Windows hub: if needed, run `Install-FleetToolchains.ps1` from an elevated PowerShell, then close it. Run `Setup-Hub.ps1` and all builds from a normal PowerShell. Linux or macOS: `bash scripts/setup-hub.sh`. Then `cd agent-fleet; npm run dev`.
2. For each extra machine: `Setup-Worker.ps1` (or `setup-worker.sh`) there, then `Add-FleetNode.ps1` on the hub. On Windows coding workers, pass `-InstallToolchains` to install the common .NET, Node.js and Python runtimes while elevated. On Linux, install the runtimes required by the project from that distribution's trusted package sources; the Ollama worker setup script intentionally does not alter package repositories. Verify required project-pinned versions as the workspace SSH account before an unattended run.
   For plan work on that machine, also provision a dedicated workspace account with `Setup-WorkerWorkspace.ps1` or `setup-worker-workspace.sh`. Add only the public key to the worker; keep its private half on the hub. In **Config > Machines**, enter the worker's SSH host, that limited account, private key path, verified SSH host fingerprint, platform and absolute workspace root. Linux SFTP roots look like `/home/agentfleet/workspaces`; Windows examples use `/C:/fleet-test/workspaces`.
3. `Test-Fleet.ps1` whenever something looks wrong.
4. When you are happy: from a normal, non-elevated PowerShell run `Install-Autostart.ps1`, later `Deploy-Fleet.ps1` after each update. They use the configured install root;
   for another folder pass `-InstallRoot` once, or set `AGENT_FLEET_INSTALL_ROOT`. The other scripts find the installed copy
   from the registered service or task. On Linux or macOS:
   `bash scripts/install-autostart.sh`, run again after each update.

### Worker workspace SSH key

On the hub, create a dedicated key with no passphrase (the backend must be able to read it while you are signed in), then copy only its `.pub` contents to the worker setup command:

```powershell
ssh-keygen -t ed25519 -N "" -f "$env:USERPROFILE\.ssh\agentfleet-worker"
Get-Content "$env:USERPROFILE\.ssh\agentfleet-worker.pub" -Raw
```

Run `Setup-WorkerWorkspace.ps1 -PublicKey '<public key>'` in elevated PowerShell on Windows, or `sudo bash scripts/setup-worker-workspace.sh agentfleet '<public key>' /home/agentfleet/workspaces` on Linux. Verify the worker fingerprint locally with `ssh-keygen -lf C:\ProgramData\ssh\ssh_host_ed25519_key.pub` (Windows) or `sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` (Linux); save the SHA256 fingerprint in **Config > Machines** along with the hub-side private-key path.

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
