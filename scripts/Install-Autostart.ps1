<#
.SYNOPSIS
    Makes the fleet start by itself when the hub machine starts, and installs the built version to run from.

.DESCRIPTION
    Builds the web UI, publishes the backend to <InstallRoot>\backend, and registers the two programs to start
    automatically. Two ways, chosen with -Mode:

      Task     (default) Scheduled tasks that start when YOU log on and run as you. No administrator rights are
               needed. The tools the fleet gives its models (files, commands, git) then act with your own
               permissions and your own PATH, which is what you want on a personal machine. Nothing runs before you
               log in.
      Service  Windows services that start at boot, before anyone logs in. Needs an elevated PowerShell. They run
               as the LocalSystem account unless you change that in services.msc, which gives the models far more
               power than your own account has: read docs\security.md first.

    Update a running install later with Deploy-Fleet.ps1. Remove it with -Uninstall.

.PARAMETER Mode
    Task (default) starts with a limited user token at logon. Run builds from a normal, non-elevated PowerShell.
    Service starts at boot and requires an elevated PowerShell.

.PARAMETER InstallRoot
    Where the backend is published. Its fleet.config.json, conversations, plans and logs live in <InstallRoot>\backend
    and are never overwritten by an update. Default: the AGENT_FLEET_INSTALL_ROOT environment variable, else where an
    earlier install is registered, else the standard application-data folder. Set AGENT_FLEET_INSTALL_ROOT once and the other scripts find it too.

.PARAMETER ListenOnLan
    Let other machines on your network reach the backend (for @fleet in VS Code on another computer). Adds a
    firewall rule for your local network only. Needs an elevated PowerShell. Without it the backend answers only
    on this machine.

.PARAMETER WebOnLan
    Let other machines on your network open the web UI (port 3000), the same way -ListenOnLan does for the backend.
    Needs an elevated PowerShell for the firewall rule. Without it the web UI answers only on this machine.

.PARAMETER DryRun
    Say what would be done and change nothing.

.PARAMETER SkipBuild
    Register or switch autostart using the already-published backend and existing web build. No project code is run.

.EXAMPLE
    .\scripts\Install-Autostart.ps1
.EXAMPLE
    .\scripts\Install-Autostart.ps1 -Mode Service     # elevated PowerShell
#>
[CmdletBinding()]
param(
    [ValidateSet('Task', 'Service')][string]$Mode = 'Task',
    [string]$InstallRoot,
    [int]$BackendPort = 8000,
    [int]$FrontendPort = 3000,
    [string]$BackendName = 'AgentFleetBackend',
    [string]$FrontendName = 'AgentFleetFrontend',
    [switch]$ListenOnLan,
    [switch]$WebOnLan,
    [switch]$SkipFrontend,
    [switch]$SkipBuild,
    [switch]$Uninstall,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$InstallRoot = Resolve-InstallRoot $InstallRoot $BackendName
$web = Join-Path $script:RepoRoot 'agent-fleet'
$backendSource = Join-Path $web 'agent'
$backendTarget = Join-Path $InstallRoot 'backend'
$exe = Join-Path $backendTarget 'AgentFleet.exe'
$bind = if ($ListenOnLan) { '0.0.0.0' } else { 'localhost' }
$urls = "http://${bind}:$BackendPort"
# A release download (backend\ and web\server.js next to scripts\) is already built: it runs where it was
# unpacked, and its web UI is Next.js's standalone server instead of `npm start` in a source checkout.
$bundle = Test-Path (Join-Path $script:RepoRoot 'web\server.js')
if ($bundle) {
    $InstallRoot = $script:RepoRoot
    $web = Join-Path $script:RepoRoot 'web'
    $backendTarget = Join-Path $script:RepoRoot 'backend'
    $exe = Join-Path $backendTarget 'AgentFleet.exe'
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Do-Step([string]$What, [scriptblock]$Block) {
    if ($DryRun) { Write-Info "would: $What"; return }
    & $Block
}

if (($Mode -eq 'Service' -or $ListenOnLan -or $WebOnLan) -and -not $isAdmin -and -not $DryRun) {
    throw 'This needs an elevated PowerShell (Run as administrator): services and firewall rules cannot be changed otherwise. Or use the default -Mode Task without -ListenOnLan.'
}
if ($Mode -eq 'Task' -and $isAdmin -and -not $Uninstall -and -not $DryRun) {
    throw 'Run Install-Autostart.ps1 from a normal, non-elevated PowerShell. It builds project code and its tasks run with a limited user token.'
}
if ($Mode -eq 'Service' -and -not $SkipBuild -and -not $Uninstall -and -not $DryRun) {
    throw 'Service registration needs elevation, but project builds must not run elevated. Build first in normal Task mode, then register the existing publish with -Mode Service -SkipBuild.'
}

function Protect-InstallRoot {
    $fullPath = [IO.Path]::GetFullPath($InstallRoot)
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.TrimEnd('\').Equals($rootPath.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallRoot must be a dedicated directory, not a drive root: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath)) { $null = New-Item -ItemType Directory -Path $fullPath }

    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $adminsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $icacls = Join-Path $env:WINDIR 'System32\icacls.exe'
    & $icacls $fullPath /inheritance:r | Out-Null
    if ($LASTEXITCODE) { throw "Cannot remove inherited permissions from $fullPath. Have its owner or an administrator fix the folder permissions, then rerun this script." }
    $user = $userSid.Translate([Security.Principal.NTAccount]).Value
    & $icacls $fullPath /grant:r `
        "${user}:(OI)(CI)F" `
        'NT AUTHORITY\SYSTEM:(OI)(CI)F' `
        'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE) { throw "Cannot restrict $fullPath. Have its owner or an administrator fix the folder permissions, then rerun this script." }
    $check = Get-Acl -LiteralPath $fullPath
    if (-not $check.AreAccessRulesProtected -or @($check.Access).Count -ne 3) {
        throw "Could not restrict $fullPath to the Fleet user, SYSTEM and Administrators. Check its permissions before continuing."
    }
    Write-Ok "Restricted $fullPath to the Fleet user, SYSTEM and Administrators"
}

# Stopping a scheduled task (or a service wrapper) ends the wrapper, not always the backend and web UI it started.
# The backend is matched by its exact path; the web UI by its folder and its production command (server.js or
# `next start`), so a `npm run dev` in the same checkout is left alone.
function Stop-FleetProcesses {
    Get-Process -Name AgentFleet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($web) -and ($_.CommandLine -match 'server\.js|next(\.js)?"?\s+start|serve\.mjs"?\s+start') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

$existingService = Get-Service -Name $BackendName -ErrorAction SilentlyContinue
$existingTask = Get-ScheduledTask -TaskName $BackendName -ErrorAction SilentlyContinue

# --- remove -------------------------------------------------------------------------------------
if ($Uninstall) {
    Write-Step 'Removing autostart'
    foreach ($name in $BackendName, $FrontendName) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Do-Step "stop and remove the scheduled task $name" { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $name -Confirm:$false }
            Write-Ok "Task $name removed"
        }
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
            Do-Step "stop and remove the service $name" { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue; sc.exe delete $name | Out-Null }
            Write-Ok "Service $name removed"
        }
    }
    Do-Step 'stop the backend and web UI if they are still running' { Stop-FleetProcesses }
    Write-Info "Files in $InstallRoot were left alone. Delete that folder yourself if you want it gone."
    return
}

if ($Mode -eq 'Task' -and $existingService) { throw "A Windows service called $BackendName already exists. Remove it first (-Uninstall) or use -Mode Service." }
if ($Mode -eq 'Service' -and $existingTask) { throw "A scheduled task called $BackendName already exists. Remove it first (-Uninstall) or use -Mode Task." }

if (-not $DryRun) { Protect-InstallRoot }

# --- prerequisites ------------------------------------------------------------------------------
Write-Step 'Checking prerequisites'
if ($bundle) {
    if (-not (Test-Command node)) { throw 'Node.js 20 or newer is needed for the web UI. Install it (winget install OpenJS.NodeJS.LTS) and run this again.' }
    Write-Ok 'node is installed (a release download needs nothing else)'
} else {
    foreach ($tool in 'dotnet', 'node', 'npm') { if (-not (Test-Command $tool)) { throw "$tool is missing. Run .\scripts\Setup-Hub.ps1 first." } }
    Write-Ok 'dotnet, node and npm are installed'
}

# --- build --------------------------------------------------------------------------------------
if (-not $SkipBuild -and -not $SkipFrontend -and -not $bundle) {
    Write-Step 'Building the web UI'
    Do-Step 'npm install and npm run build in agent-fleet' {
        Push-Location $web
        try {
            npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed.' }
            npm run build; if ($LASTEXITCODE) { throw 'npm run build failed.' }
        } finally { Pop-Location }
    }
}

# Stop whatever is running from a previous install so its files can be replaced.
function Stop-Fleet {
    foreach ($name in $BackendName, $FrontendName) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue }
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $name -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    }
    Stop-FleetProcesses
}

if ($SkipBuild) {
    Write-Step 'Keeping the existing backend and web build'
    Do-Step 'stop the previous fleet processes before registering the new autostart' { Stop-Fleet }
} elseif ($bundle) {
    Write-Step "Running the download in place ($script:RepoRoot)"
    Do-Step 'stop a copy that is already running' { Stop-Fleet }
    Write-Ok 'Keep this folder where it is: the autostart runs the fleet from here'
} else {
    Write-Step "Publishing the backend to $backendTarget"
    Do-Step "stop the running fleet and dotnet publish to $backendTarget" {
        Stop-Fleet
        Push-Location $backendSource
        try { dotnet publish -c Release -o $backendTarget -nologo -v q; if ($LASTEXITCODE) { throw 'dotnet publish failed.' } } finally { Pop-Location }
    }
    Write-Ok 'Published'
}

# The settings made by Setup-Hub.ps1 (or by hand) come along the first time only.
$devConfig = Join-Path $web 'fleet.config.json'
$installedConfig = Join-Path $backendTarget 'fleet.config.json'
if (-not $bundle -and (Test-Path $devConfig) -and -not (Test-Path $installedConfig)) {
    Do-Step "copy your fleet.config.json to $installedConfig" { Copy-Item $devConfig $installedConfig }
    Write-Ok 'Copied your fleet.config.json (from agent-fleet) into the install'
} elseif (Test-Path $installedConfig) { Write-Ok 'The install already has its own fleet.config.json (left as it is)' }

# The web UI finds the backend through AGENT_URL in .env.local.
if ($BackendPort -ne 8000 -and -not $SkipFrontend -and -not $bundle) {
    $envLocal = Join-Path $web '.env.local'
    Do-Step "set AGENT_URL=http://localhost:$BackendPort in agent-fleet\.env.local" {
        $lines = if (Test-Path $envLocal) { @(Get-Content $envLocal) } else { @() }
        $lines = @($lines | Where-Object { $_ -notmatch '^AGENT_URL=' }) + "AGENT_URL=http://localhost:$BackendPort"
        [IO.File]::WriteAllLines($envLocal, $lines, (New-Object Text.UTF8Encoding($false)))
    }
}

# --- register -----------------------------------------------------------------------------------
$npm = if (Test-Command npm.cmd) { (Get-Command npm.cmd).Source } else { 'npm' }
$node = if (Test-Command node) { (Get-Command node).Source } else { 'node' }
$serverJs = Join-Path $web 'server.js'
$frontendLog = Join-Path $InstallRoot 'frontend.log'
if (-not $DryRun -and -not (Test-Path $InstallRoot)) { $null = New-Item -ItemType Directory -Path $InstallRoot }

if ($Mode -eq 'Task') {
    Write-Step 'Registering scheduled tasks (start at your logon and every five minutes if stopped, run as you)'
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    # Two ways to start: at logon, and every five minutes after that. The second is what keeps Fleet up. Task Scheduler
    # restarts a task only when it fails while running, and only a few times, so a task that was stopped from outside, or
    # that gave up after its restarts (the web UI's port still held, its disk not mounted yet), stayed down for days. A
    # task that is already running ignores the repeat (MultipleInstances IgnoreNew). The first repeat is five minutes
    # away so it does not race the start at the end of this script.
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $me
    $repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
    $trigger = @($logon, $repeat)

    # The backend checks plan diagrams in the web UI, so it needs the web UI's address when the port is not 3000.
    $backendCommand = "Set-Location -LiteralPath '$backendTarget'; `$env:FLEET_FRONTEND_URL='http://localhost:$FrontendPort'; & '$exe' --urls $urls"
    $webHost = if ($WebOnLan) { '0.0.0.0' } else { '127.0.0.1' }
    $frontendCommand = if ($bundle) {
        "Set-Location -LiteralPath '$web'; `$env:PORT='$FrontendPort'; `$env:HOSTNAME='$webHost'; `$env:AGENT_URL='http://localhost:$BackendPort'; & '$node' '$serverJs' *> '$frontendLog'"
    } else {
        "Set-Location -LiteralPath '$web'; `$env:PORT='$FrontendPort'; `$env:FLEET_WEB_HOST='$webHost'; & '$npm' start *> '$frontendLog'"
    }
    $jobs = @(, @($BackendName, $backendCommand))
    if (-not $SkipFrontend) { $jobs += , @($FrontendName, $frontendCommand) }
    foreach ($job in $jobs) {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "' + $job[1].Replace('"', '\"') + '"')
        Do-Step "register the scheduled task $($job[0])" {
            Register-ScheduledTask -TaskName $job[0] -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'Agent Fleet' -Force | Out-Null
        }
        Write-Ok "Task $($job[0]) registered"
    }
    Write-Step 'Starting'
    foreach ($job in $jobs) { Do-Step "start $($job[0])" { Start-ScheduledTask -TaskName $job[0] } }
}
else {
    Write-Step 'Registering Windows services (start at boot)'
    Do-Step "create the service $BackendName" {
        if (-not (Get-Service -Name $BackendName -ErrorAction SilentlyContinue)) {
            New-Service -Name $BackendName -BinaryPathName "`"$exe`" --urls $urls" -DisplayName 'Agent Fleet backend' -Description 'Agent Fleet .NET AG-UI backend' -StartupType Automatic | Out-Null
        } else { sc.exe config $BackendName binPath= "`"$exe`" --urls $urls" | Out-Null }
    }
    # Started again if it ever stops unexpectedly, so a plan whose interrupted step is safe to replay carries on (it resumes from disk).
    Do-Step "restart $BackendName if it stops unexpectedly" { sc.exe failure $BackendName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null }
    Write-Ok "Service $BackendName registered"
    if (-not $SkipFrontend) {
        $nssm = if (Test-Command nssm) { (Get-Command nssm).Source } else { $null }
        if (-not $nssm) {
            if (Test-Command winget) {
                Do-Step 'winget install NSSM.NSSM (wraps the web UI as a service)' { winget install --id NSSM.NSSM -e --accept-package-agreements --accept-source-agreements | Out-Null }
                $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
                if (Test-Command nssm) { $nssm = (Get-Command nssm).Source }
            }
            if (-not $nssm -and -not $DryRun) { throw 'NSSM is needed to run the web UI as a service. Install it (winget install NSSM.NSSM), then run this again. Or use the default -Mode Task.' }
        }
        Do-Step "create the service $FrontendName with NSSM" {
            & $nssm remove $FrontendName confirm 2>&1 | Out-Null
            $listen = if ($WebOnLan) { '0.0.0.0' } else { '127.0.0.1' }
            if ($bundle) {
                & $nssm install $FrontendName $node $serverJs | Out-Null
                & $nssm set $FrontendName AppEnvironmentExtra "PORT=$FrontendPort" "HOSTNAME=$listen" "AGENT_URL=http://localhost:$BackendPort" | Out-Null
            } else {
                & $nssm install $FrontendName $npm start | Out-Null
                & $nssm set $FrontendName AppEnvironmentExtra "PORT=$FrontendPort" "FLEET_WEB_HOST=$listen" | Out-Null
            }
            & $nssm set $FrontendName AppDirectory $web | Out-Null
            & $nssm set $FrontendName AppStdout $frontendLog | Out-Null
            & $nssm set $FrontendName AppStderr $frontendLog | Out-Null
            # A little after boot, and again if it stops: the web UI runs from the source folder, which can be on a disk
            # that comes up after the services start (a USB drive).
            & $nssm set $FrontendName Start SERVICE_DELAYED_AUTO_START | Out-Null
            sc.exe failure $FrontendName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
        }
        Write-Ok "Service $FrontendName registered"
    }
    Write-Step 'Starting'
    Do-Step "start $BackendName" { Start-Service -Name $BackendName }
    if (-not $SkipFrontend) { Do-Step "start $FrontendName" { Start-Service -Name $FrontendName } }
    Write-Warn 'The services run as LocalSystem. That account can do more than yours. To run them as your own user, open services.msc, Properties, Log On, and enter your password there (never on a command line). Read docs\security.md.'
}

# --- LAN access ---------------------------------------------------------------------------------
if ($ListenOnLan -or $WebOnLan) {
    Write-Step 'Firewall'
    $ports = @(); if ($ListenOnLan) { $ports += $BackendPort }; if ($WebOnLan) { $ports += $FrontendPort }
    Do-Step "allow your local network to reach port(s) $($ports -join ', ')" {
        Get-NetFirewallRule -DisplayName 'Agent Fleet (LAN)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'Agent Fleet (LAN)' -Direction Inbound -Protocol TCP -LocalPort $ports -Action Allow -RemoteAddress LocalSubnet -Profile Private | Out-Null
    }
    Write-Ok "Port(s) $($ports -join ', ') are open to your local network (Private profile only)"
    Write-Warn 'Anyone on that network can then use the fleet, including its file and command tools. There is no login.'
}

# --- wait ---------------------------------------------------------------------------------------
if (-not $DryRun) {
    Write-Step 'Waiting for the backend'
    $deadline = (Get-Date).AddSeconds(60)
    $health = $null
    while ((Get-Date) -lt $deadline -and -not $health) {
        try { $health = Invoke-RestMethod -Uri "http://localhost:$BackendPort/health" -TimeoutSec 5 }
        catch { if ($_.ErrorDetails.Message) { try { $health = $_.ErrorDetails.Message | ConvertFrom-Json } catch { } }; if (-not $health) { Start-Sleep -Seconds 2 } }
    }
    if ($health) { Write-Ok "Backend answers (overall: $($health.status))"; foreach ($n in @($health.nodes)) { Write-Info ('{0,-14} {1}' -f $n.name, $(if ($n.ready) { 'ready' } else { "not ready ($($n.reason))" })) } }
    else { Write-Warn "The backend did not answer within 60 seconds. Look in $backendTarget\logs." }
}

Write-Host "`nDone. Web UI: http://localhost:$FrontendPort   Backend: http://localhost:$BackendPort" -ForegroundColor Green
if ($bundle) { Write-Host 'Update: stop it (-Uninstall), unpack a newer download over this folder, run this again. Remove with .\scripts\Install-Autostart.ps1 -Uninstall.' }
else { Write-Host 'Update later with .\scripts\Deploy-Fleet.ps1. Remove with .\scripts\Install-Autostart.ps1 -Uninstall.' }
