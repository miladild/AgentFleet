<#
.SYNOPSIS
    Starts a downloaded Agent Fleet release: the backend and the web UI, until you press Ctrl+C.

.DESCRIPTION
    For a release download (backend\ and web\ next to this scripts folder). Needs Node.js 20 or newer for the web
    UI, and Ollama with at least one model. Nothing is built or installed. To have the fleet start at every logon
    instead, run .\scripts\Install-Autostart.ps1. From a source checkout, use `npm run dev` in agent-fleet instead.

.PARAMETER WebOnLan
    Let other machines on your network open the web UI. There is no login: read docs\security.md first.

.EXAMPLE
    .\scripts\Start-Fleet.ps1
#>
[CmdletBinding()]
param(
    [int]$BackendPort = 8000,
    [int]$FrontendPort = 3000,
    [switch]$WebOnLan
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$backendDir = Join-Path $script:RepoRoot 'backend'
$webDir = Join-Path $script:RepoRoot 'web'
$exe = Join-Path $backendDir $(if ($IsLinux -or $IsMacOS) { 'AgentFleet' } else { 'AgentFleet.exe' })

if (-not (Test-Path (Join-Path $webDir 'server.js')) -or -not (Test-Path $exe)) {
    throw 'This is not a release download (no backend\ and web\ folders). From a source checkout, run: cd agent-fleet; npm run dev'
}
if (-not (Test-Command node)) {
    throw 'Node.js 20 or newer is needed for the web UI. Install it (winget install OpenJS.NodeJS.LTS, or https://nodejs.org) and run this again.'
}
if (-not (Test-Command ollama)) { Write-Warn 'Ollama is not installed on this machine. Install it from https://ollama.com unless your models run on other machines.' }

# Both programs inherit these. The backend needs to know where the web UI is (it checks plan diagrams there),
# and the web UI where the backend is. HOSTNAME is the address the web UI listens on.
$env:FLEET_FRONTEND_URL = "http://localhost:$FrontendPort"
$env:AGENT_URL = "http://localhost:$BackendPort"
$env:PORT = "$FrontendPort"
$env:HOSTNAME = if ($WebOnLan) { '0.0.0.0' } else { '127.0.0.1' }
$env:COPILOTKIT_TELEMETRY_DISABLED = 'true'

$backend = Start-Process -FilePath $exe -ArgumentList '--urls', "http://localhost:$BackendPort" -WorkingDirectory $backendDir -PassThru -NoNewWindow
$web = Start-Process -FilePath (Get-Command node).Source -ArgumentList "`"$(Join-Path $webDir 'server.js')`"" -WorkingDirectory $webDir -PassThru -NoNewWindow
Write-Host "`nAgent Fleet is starting. Web UI: http://localhost:$FrontendPort   Backend: http://localhost:$BackendPort" -ForegroundColor Green
Write-Host 'The first start writes backend\fleet.config.json with one machine (this one). Add machines in the web UI under Config.'
Write-Host 'Press Ctrl+C to stop both.'
try {
    while (-not $backend.HasExited -and -not $web.HasExited) { Start-Sleep -Seconds 1 }
    if ($backend.HasExited) { Write-Bad "The backend stopped (exit code $($backend.ExitCode)). Its log is in backend\logs." }
    if ($web.HasExited) { Write-Bad "The web UI stopped (exit code $($web.ExitCode))." }
}
finally {
    foreach ($process in $backend, $web) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
}
