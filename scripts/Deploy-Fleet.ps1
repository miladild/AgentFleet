<#
.SYNOPSIS
    Promotes the current source to the running fleet on this machine.

.DESCRIPTION
    Builds the web UI, publishes the backend to <InstallRoot>\backend, restarts both programs, and waits for the
    backend's /health endpoint to answer. It works with whichever way Install-Autostart.ps1 registered them:
    scheduled tasks (no administrator rights needed) or Windows services (needs an elevated PowerShell).

    Run it from anywhere:  .\scripts\Deploy-Fleet.ps1

.PARAMETER InstallRoot
    Where the backend is published. fleet.config.json, sessions and logs live in <InstallRoot>\backend and are
    never overwritten by a publish. Default: AGENT_FLEET_INSTALL_ROOT, else the folder the registered service or task
    runs from, else C:\AgentFleet.

.PARAMETER SkipFrontend
    Skip the web UI build and restart (backend-only change).
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$BackendName = 'AgentFleetBackend',
    [string]$FrontendName = 'AgentFleetFrontend',
    [int]$BackendPort = 8000,
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$InstallRoot = Resolve-InstallRoot $InstallRoot $BackendName
$web = Join-Path $script:RepoRoot 'agent-fleet'
$backendSource = Join-Path $web 'agent'
$backendTarget = Join-Path $InstallRoot 'backend'
$exe = Join-Path $backendTarget 'AgentFleet.exe'

$service = Get-Service -Name $BackendName -ErrorAction SilentlyContinue
$task = Get-ScheduledTask -TaskName $BackendName -ErrorAction SilentlyContinue
if (-not $service -and -not $task) {
    throw "Nothing called '$BackendName' is registered yet (neither a Windows service nor a scheduled task). Run .\scripts\Install-Autostart.ps1 first."
}
$mode = if ($service) { 'service' } else { 'task' }

if ($mode -eq 'service') {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw 'The fleet runs as Windows services, so this needs an elevated PowerShell (Run as administrator).' }
}

function Stop-One([string]$Name) {
    if ($mode -eq 'service') {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $Name -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    } elseif (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    }
}

function Start-One([string]$Name) {
    if (-not (($mode -eq 'service' -and (Get-Service -Name $Name -ErrorAction SilentlyContinue)) -or ($mode -eq 'task' -and (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue)))) { return }
    if ($mode -ne 'service') { Start-ScheduledTask -TaskName $Name; return }
    # An antivirus can refuse the first start of a freshly published program ("Access is denied"); the second start works.
    try { Start-Service -Name $Name }
    catch {
        Write-Warn "The first start of $Name was refused ($($_.Exception.Message)); trying again."
        Start-Sleep -Seconds 3
        Start-Service -Name $Name
    }
}

if (-not $SkipFrontend) {
    Write-Step 'Building the web UI'
    Push-Location $web
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed' } }
        npm run build
        if ($LASTEXITCODE) { throw 'npm run build failed' }
    }
    finally { Pop-Location }
}

Write-Step "Stopping $BackendName"
Stop-One $BackendName
# A task stops its shell but can leave the backend process itself; make sure the files are free.
Get-Process -Name AgentFleet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Step "Publishing the backend to $backendTarget"
Push-Location $backendSource
try {
    dotnet publish -c Release -o $backendTarget -nologo -v q
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
}
finally { Pop-Location }

if ($mode -eq 'service') {
    # Installs made before this setting existed get it here: a backend that stops unexpectedly is started again, so a
    # plan running overnight carries on (it resumes from disk).
    sc.exe failure $BackendName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    if (-not $SkipFrontend -and (Get-Service -Name $FrontendName -ErrorAction SilentlyContinue)) {
        # The web UI runs from the source folder, which can be on a disk that comes up after the services start (a USB
        # drive): after a reboot it failed with "The system cannot open the file" and stayed down. It starts a little
        # later now, and is started again if it stops.
        sc.exe failure $FrontendName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
        sc.exe config $FrontendName start= delayed-auto | Out-Null
    }
}

Write-Step "Starting $BackendName"
Start-One $BackendName

if (-not $SkipFrontend) {
    Write-Step "Restarting $FrontendName"
    Stop-One $FrontendName
    Start-Sleep -Seconds 1
    Start-One $FrontendName
}

Write-Step 'Waiting for the backend to answer /health'
$healthUrl = "http://localhost:$BackendPort/health"
$deadline = (Get-Date).AddSeconds(60)
$health = $null
while ((Get-Date) -lt $deadline -and -not $health) {
    try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5 }
    catch {
        # A 503 (fallback node down) still carries the node list in the body.
        if ($_.ErrorDetails.Message) { try { $health = $_.ErrorDetails.Message | ConvertFrom-Json } catch { } }
        if (-not $health) { Start-Sleep -Seconds 2 }
    }
}

if (-not $health) { throw "The backend did not answer $healthUrl within 60 seconds. Check $backendTarget\logs." }

Write-Host "Overall: $($health.status)"
foreach ($node in $health.nodes) {
    $state = if ($node.ready) { 'ready' } else { "NOT READY ($($node.reason))" }
    Write-Host ('  {0,-14} {1,-32} {2}' -f $node.name, $node.model, $state)
}
# The durable record is part of the backend now: prove it opened, and that old chats were imported.
try {
    $sessions = @(Invoke-RestMethod -Uri "http://localhost:$BackendPort/api/sessions" -TimeoutSec 10 | ForEach-Object { $_ })
    $contexts = @(Invoke-RestMethod -Uri "http://localhost:$BackendPort/api/contexts" -TimeoutSec 10 | ForEach-Object { $_ })
    Write-Host ("Durable record: {0} conversation(s) listed, {1} context(s) in all" -f $sessions.Count, $contexts.Count)
}
catch { Write-Host "Durable record: not answering yet ($($_.Exception.Message))" -ForegroundColor Yellow }

Write-Host "`nDone. Web UI: http://localhost:3000   Backend: http://localhost:$BackendPort" -ForegroundColor Green
