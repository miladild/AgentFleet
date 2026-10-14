<#
.SYNOPSIS
    Opens and closes Agent Fleet from the Windows notification area.
.DESCRIPTION
    Launch with powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File. --open starts the fleet and opens the web UI;
    --close closes it; no arguments leave a running fleet's icon resident.
    For scripted checks, FLEET_TRAY_NO_BROWSER=1 records the URL in <install-root>\tray-open-url.txt
    instead of opening a browser. It also records the resident PID in tray.pid for cleanup.
#>
param([string]$InstallRoot)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetTray.Functions.ps1"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$opening = $args -contains '--open'
$closing = $args -contains '--close'
if (($opening -and $closing) -or @($args | Where-Object { $_ -notin @('--open','--close') }).Count) { throw 'Use --open, --close, or no action.' }
try { $install = Get-FleetInstall -InstallRoot $InstallRoot -ScriptRoot (Split-Path $PSScriptRoot -Parent) }
catch { [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Agent Fleet', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null; exit 1 }
if (-not $install) {
    [Windows.Forms.MessageBox]::Show('Agent Fleet is not installed on this computer. Run scripts\Install-Autostart.ps1.', 'Agent Fleet') | Out-Null
    exit
}

function Get-TrayHealth($value) {
    $answers = $false
    $plans = @()
    try { $health = Invoke-RestMethod -Uri ($value.backendUrl.TrimEnd('/') + '/health') -TimeoutSec 3; $answers = [bool]$health }
    catch {
        if ($_.ErrorDetails.Message) { try { $health = $_.ErrorDetails.Message | ConvertFrom-Json; $answers = [bool]$health.status } catch { } }
    }
    if ($answers) { try { $plans = @(Invoke-RestMethod -Uri ($value.backendUrl.TrimEnd('/') + '/api/plans') -TimeoutSec 3) } catch { } }
    return [pscustomobject]@{ Answers=$answers; Plans=$plans }
}
function Get-TrayTasks($value) {
    if ($value.Portable) { return [pscustomobject]@{ BackendEnabled=$true; FrontendEnabled=$true; BackendRunning=$false; FrontendRunning=$false } }
    if ($value.mode -eq 'Service') {
        $backend = Get-Service -Name $value.backendTask -ErrorAction SilentlyContinue
        $frontend = Get-Service -Name $value.frontendTask -ErrorAction SilentlyContinue
        return [pscustomobject]@{ BackendEnabled=($backend.Status -eq 'Running'); FrontendEnabled=($frontend.Status -eq 'Running'); BackendRunning=($backend.Status -eq 'Running'); FrontendRunning=($frontend.Status -eq 'Running') }
    }
    $backend = Get-ScheduledTask -TaskName $value.backendTask -ErrorAction SilentlyContinue
    $frontend = Get-ScheduledTask -TaskName $value.frontendTask -ErrorAction SilentlyContinue
    return [pscustomobject]@{ BackendEnabled=([bool]$backend -and $backend.Settings.Enabled); FrontendEnabled=([bool]$frontend -and $frontend.Settings.Enabled); BackendRunning=($backend.State -eq 'Running'); FrontendRunning=($frontend.State -eq 'Running') }
}
function Stop-TrayProcesses($value) {
    $exe = Join-Path $value.installRoot 'backend\AgentFleet.exe'
    Get-Process -Name AgentFleet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    $web = $value.WebDirectory.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($web, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.CommandLine -match 'server\.js|next(\.js)?"?\s+start|serve\.mjs"?\s+start' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
$runner = @{
    Health = { param($value) Get-TrayHealth $value }
    EnableTask = { param($name) Enable-ScheduledTask -TaskName $name | Out-Null }
    DisableTask = { param($name) Disable-ScheduledTask -TaskName $name | Out-Null }
    StartTask = { param($name) Start-ScheduledTask -TaskName $name }
    StopTask = { param($name) Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue }
    StartService = { param($name) Start-Service -Name $name }
    StopService = { param($name) $service = Get-Service -Name $name -ErrorAction SilentlyContinue; if ($service) { Stop-Service -Name $name -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } }
    StopProcessesOfInstall = { param($value) Stop-TrayProcesses $value }
    GetPlans = {
        param($value)
        try { return @(Invoke-RestMethod -Uri ($value.backendUrl.TrimEnd('/') + '/api/plans') -TimeoutSec 3) }
        catch {
            $folder = Join-Path $value.installRoot 'backend\plans'
            if (Test-Path -LiteralPath $folder) { return @(Get-ChildItem -LiteralPath $folder -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json }) }
            return @()
        }
    }
    Confirm = {
        param($title)
        $message = 'A plan is running (' + $title + '). Closing stops it for now; it picks up again, or waits for your review, the next time the fleet starts. Close anyway?'
        return [Windows.Forms.MessageBox]::Show($message, 'Agent Fleet', [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question, [Windows.Forms.MessageBoxDefaultButton]::Button2) -eq [Windows.Forms.DialogResult]::Yes
    }
    Wait = {
        param($value,$seconds)
        if ($script:trayIcon) { $script:trayIcon.Text = 'Agent Fleet: starting' }
        $deadline = (Get-Date).AddSeconds($seconds)
        do { if ((Get-TrayHealth $value).Answers) { return $true }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
        return $false
    }
    OpenUrl = {
        param($url)
        if ($env:FLEET_TRAY_NO_BROWSER -eq '1') { Add-Content -LiteralPath (Join-Path $install.installRoot 'tray-open-url.txt') -Value $url }
        else { Start-Process -FilePath $url }
    }
    StartPortable = {
        param($value)
        $starter = Join-Path $value.installRoot 'scripts\Start-Fleet.ps1'
        if (-not (Test-Path -LiteralPath $starter)) { throw 'The download is missing its start script.' }
        $command = '-NoProfile -ExecutionPolicy Bypass -File "' + $starter + '" -NoBrowser -BackendPort ' + ([Uri]$value.backendUrl).Port + ' -FrontendPort ' + ([Uri]$value.webUrl).Port
        Start-Process -FilePath 'powershell.exe' -ArgumentList $command -WindowStyle Hidden | Out-Null
    }
}
function Show-TrayError($errorRecord) {
    if ($env:FLEET_TRAY_NO_BROWSER -eq '1') { Add-Content -LiteralPath (Join-Path $install.installRoot 'tray-error.txt') -Value $errorRecord.Exception.Message }
    [Windows.Forms.MessageBox]::Show($errorRecord.Exception.Message, 'Agent Fleet', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}

$hash = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($install.installRoot.ToLowerInvariant()))).Replace('-','').Substring(0,24) }
finally { $hash.Dispose() }
$created = $false
$mutex = New-Object Threading.Mutex($true, ('Local\AgentFleetTray-' + $identity), [ref]$created)
$closeSignal = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::AutoReset, ('Local\AgentFleetTrayClose-' + $identity))
$script:trayIcon = $null
$timer = $null
$menu = $null
$icon = $null
$context = $null
try {
    if ($closing) {
        try { $result = Invoke-FleetClose $install $runner; if ($result.Success) { $closeSignal.Set() | Out-Null } } catch { Show-TrayError $_ }
        return
    }
    if (-not $created) {
        if ($opening) { try { $result = Invoke-FleetOpen $install $runner; if (-not $result.Success) { throw 'Agent Fleet did not start within 90 seconds.' } } catch { Show-TrayError $_ } }
        return
    }
    if (-not $opening) {
        $tasks = Get-TrayTasks $install
        if (-not $tasks.BackendEnabled -and -not $tasks.FrontendEnabled) { return }
        if ($install.Portable -and -not (Get-TrayHealth $install).Answers) { return }
    }
    [Windows.Forms.Application]::EnableVisualStyles()
    $context = New-Object Windows.Forms.ApplicationContext
    $icon = New-Object Drawing.Icon((Join-Path $PSScriptRoot 'AgentFleet.ico'))
    $script:trayIcon = New-Object Windows.Forms.NotifyIcon
    $script:trayIcon.Icon = $icon
    $menu = New-Object Windows.Forms.ContextMenuStrip
    $openItem = $menu.Items.Add('Open Agent Fleet')
    $closeItem = $menu.Items.Add('Close Agent Fleet')
    $script:trayIcon.ContextMenuStrip = $menu
    $openHandler = {
        try { $result = Invoke-FleetOpen $install $runner; if (-not $result.Success) { throw 'Agent Fleet did not start within 90 seconds.' } }
        catch { Show-TrayError $_ }
    }
    $openItem.add_Click($openHandler)
    $script:trayIcon.add_DoubleClick($openHandler)
    $closeItem.add_Click({ try { $result = Invoke-FleetClose $install $runner; if ($result.Success) { $context.ExitThread() } } catch { Show-TrayError $_ } })
    $refresh = { $state = Get-FleetState $install ${function:Get-TrayHealth} ${function:Get-TrayTasks}; $script:trayIcon.Text = Get-FleetTooltip $state }
    $menu.add_Opening({ try { & $refresh } catch { Show-TrayError $_ } })
    & $refresh
    $script:trayIcon.Visible = $true
    if ($env:FLEET_TRAY_NO_BROWSER -eq '1') { Set-Content -LiteralPath (Join-Path $install.installRoot 'tray.pid') -Value $PID }
    if ($opening) { & $openHandler }
    $script:lastRefresh = Get-Date
    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 250
    $timer.add_Tick({
        try {
            if ($closeSignal.WaitOne(0)) { $context.ExitThread(); return }
            if (((Get-Date) - $script:lastRefresh).TotalSeconds -ge 15) { & $refresh; $script:lastRefresh = Get-Date }
        } catch { Show-TrayError $_ }
    })
    $timer.Start()
    [Windows.Forms.Application]::Run($context)
} catch { Show-TrayError $_ }
finally {
    if ($timer) { $timer.Stop(); $timer.Dispose() }
    if ($script:trayIcon) { $script:trayIcon.Visible = $false; $script:trayIcon.Dispose() }
    if ($menu) { $menu.Dispose() }
    if ($icon) { $icon.Dispose() }
    if ($context) { $context.Dispose() }
    if ($created) {
        if ($env:FLEET_TRAY_NO_BROWSER -eq '1') { Remove-Item -LiteralPath (Join-Path $install.installRoot 'tray.pid') -ErrorAction SilentlyContinue }
        $mutex.ReleaseMutex()
    }
    $closeSignal.Dispose()
    $mutex.Dispose()
}
