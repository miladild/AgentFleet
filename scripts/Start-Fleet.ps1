<#
.SYNOPSIS
    Starts a downloaded Agent Fleet release: the backend and the web UI, until you press Ctrl+C.

.DESCRIPTION
    For a release download (backend\ and web\ next to this scripts folder). Needs Node.js 20 or newer for the web
    UI, and Ollama on at least one machine. Nothing is built or installed. When the web UI answers, it opens in
    your browser; its Setup tab takes it from there (downloading a model, adding machines). To have the fleet
    start at every logon instead, run .\scripts\Install-Autostart.ps1. From a source checkout, use `npm run dev`
    in agent-fleet instead. Double-clicking "Start Agent Fleet.cmd" in the download runs this.

.PARAMETER WebOnLan
    Let other machines on your network open the web UI. There is no login: read docs\security.md first.

.PARAMETER NoBrowser
    Do not open the web UI in a browser.

.EXAMPLE
    .\scripts\Start-Fleet.ps1
#>
[CmdletBinding()]
param(
    [int]$BackendPort = 8000,
    [int]$FrontendPort = 3000,
    [switch]$WebOnLan,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$backendDir = Join-Path $script:RepoRoot 'backend'
$webDir = Join-Path $script:RepoRoot 'web'
$exe = Join-Path $backendDir $(if ($IsLinux -or $IsMacOS) { 'AgentFleet' } else { 'AgentFleet.exe' })
$webUrl = "http://localhost:$FrontendPort"

function Test-PortFree([int]$Port) {
    try { $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port); $listener.Start(); $listener.Stop(); return $true }
    catch { return $false }
}

function Test-Answers([string]$Url) {
    try { $null = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3; return $true }
    catch { return [bool]$_.Exception.Response }
}

function Open-WebUi {
    if ($NoBrowser) { return }
    try { Start-Process $webUrl } catch { Write-Info "Open $webUrl in your browser." }
}

if (-not (Test-Path (Join-Path $webDir 'server.js')) -or -not (Test-Path $exe)) {
    throw 'This is not a release download (no backend\ and web\ folders). From a source checkout, run: cd agent-fleet; npm run dev'
}

# Windows marks files that came from the internet; a marked program can be stopped from starting.
if (-not ($IsLinux -or $IsMacOS) -and (Get-Item -LiteralPath $exe -Stream Zone.Identifier -ErrorAction SilentlyContinue)) {
    Write-Info 'Unblocking the downloaded files (Windows marks files from the internet). This happens once.'
    Get-ChildItem -LiteralPath $script:RepoRoot -Recurse -File | Unblock-File
}

if (-not (Test-Command node)) {
    throw 'Node.js 20 or newer is needed for the web UI. Install it (winget install OpenJS.NodeJS.LTS, or https://nodejs.org), then open a new window and run this again.'
}
$nodeMajor = [int]((node --version) -replace '^v(\d+)\..*', '$1')
if ($nodeMajor -lt 20) { throw "Node.js $(node --version) is too old for the web UI. Install version 20 or newer from https://nodejs.org and run this again." }
if (-not (Test-Command ollama)) { Write-Warn 'Ollama is not installed on this machine. The web UI will show how to install it (https://ollama.com), unless your models run on other machines.' }

# Already running (at logon, or in another window)? Then there is nothing to start.
if (-not (Test-PortFree $BackendPort) -and (Test-Answers "http://localhost:$BackendPort/api/fleet-mode")) {
    Write-Ok "Agent Fleet is already running on this computer. Web UI: $webUrl"
    Open-WebUi
    return
}
foreach ($port in $BackendPort, $FrontendPort) {
    if (-not (Test-PortFree $port)) {
        $flag = if ($port -eq $BackendPort) { '-BackendPort' } else { '-FrontendPort' }
        throw "Port $port is already used by another program. Close it, or start the fleet on another port: .\scripts\Start-Fleet.ps1 $flag $($port + 10)"
    }
}

# Both programs inherit these. The backend needs to know where the web UI is (it checks plan diagrams there),
# and the web UI where the backend is. HOSTNAME is the address the web UI listens on.
$env:FLEET_FRONTEND_URL = $webUrl
$env:AGENT_URL = "http://localhost:$BackendPort"
$env:PORT = "$FrontendPort"
$env:HOSTNAME = if ($WebOnLan) { '0.0.0.0' } else { '127.0.0.1' }
$env:COPILOTKIT_TELEMETRY_DISABLED = 'true'

$backend = Start-Process -FilePath $exe -ArgumentList '--urls', "http://localhost:$BackendPort" -WorkingDirectory $backendDir -PassThru -NoNewWindow
$web = Start-Process -FilePath (Get-Command node).Source -ArgumentList "`"$(Join-Path $webDir 'server.js')`"" -WorkingDirectory $webDir -PassThru -NoNewWindow
Write-Host "`nAgent Fleet is starting. Web UI: $webUrl   Backend: http://localhost:$BackendPort" -ForegroundColor Green
Write-Host 'Leave this window open while you use the fleet. Press Ctrl+C (or close the window) to stop it.'
try {
    # Open the browser once the page answers. The first start of a new download can take a minute while
    # antivirus software looks at the web UI's files.
    $opened = $false
    $waited = 0
    while (-not $backend.HasExited -and -not $web.HasExited) {
        if (-not $opened) {
            if (Test-Answers $webUrl) {
                $opened = $true
                Write-Ok "Ready: $webUrl"
                Open-WebUi
            }
            elseif ($waited -eq 30) { Write-Info 'Still starting. The first start of a new download can take a minute or two while antivirus software scans it.' }
        }
        Start-Sleep -Seconds 1
        $waited++
    }
    if ($backend.HasExited) { Write-Bad "The backend stopped (exit code $($backend.ExitCode)). Its log is in backend\logs." }
    if ($web.HasExited) { Write-Bad "The web UI stopped (exit code $($web.ExitCode))." }
}
finally {
    foreach ($process in $backend, $web) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
}
