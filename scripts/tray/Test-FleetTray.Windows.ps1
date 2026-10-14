<#
.SYNOPSIS
    Checks the Windows tray using temporary tasks, folders and local fake listeners.
.DESCRIPTION
    Requires free test ports 8033 and 3033. Every task and process made by this check is removed in finally.
#>
[CmdletBinding()]
param([ValidateSet('Tray','Installer')][string]$Phase = 'Tray', [string]$Check)
$ErrorActionPreference = 'Stop'
$script:Checks = 0
$exitCode = 0
function Assert-WindowsTray([string]$Name, [bool]$Condition) {
    if ($Check -and $Check -ne $Name) { return }
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:Checks++
    Write-Host "PASS: $Name"
}
function Wait-TrayCheck([scriptblock]$Condition, [string]$What, [int]$Seconds = 25) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $deadline)
    throw "Timed out: $What"
}
$root = 'C:\fleet-test-tray'
Write-Host $root
Write-Host 'Antivirus exclusion needed for this folder before running'
if (Test-Path -LiteralPath $root) { throw 'Refusing to reuse an existing fixture folder.' }
$createdRoot = $false
$prefix = 'AgentFleetTest' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$backendName = $prefix + 'Backend'
$frontendName = $prefix + 'Frontend'
$trayName = $prefix + 'Tray'
$createdTasks = New-Object Collections.ArrayList
$listenerProcess = $null
$previousRoot = $env:AGENT_FLEET_INSTALL_ROOT
$previousBrowser = $env:FLEET_TRAY_NO_BROWSER
$sourceScripts = Split-Path $PSScriptRoot -Parent
$scriptsRoot = Join-Path $root 'scripts'
$launcher = Join-Path $scriptsRoot 'tray\Fleet-Tray.ps1'
$hostExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Start-CheckTray([string]$Action) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "' + $launcher + '" -InstallRoot "' + $root + '"'
    if ($Action) { $arguments += ' ' + $Action }
    Start-Process -FilePath $hostExe -ArgumentList $arguments -WindowStyle Hidden | Out-Null
}
function Get-ResidentPid {
    $file = Join-Path $root 'tray.pid'
    if (Test-Path -LiteralPath $file) { return [int](Get-Content -LiteralPath $file -Raw).Trim() }
    return 0
}
try {
    $null = New-Item -ItemType Directory -Path $root -ErrorAction Stop
    $createdRoot = $true
    $null = New-Item -ItemType Directory -Path (Join-Path $scriptsRoot 'tray') -Force
    foreach ($file in 'FleetCommon.ps1','Install-FleetTray.ps1') {
        Copy-Item -LiteralPath (Join-Path $sourceScripts $file) -Destination $scriptsRoot
    }
    foreach ($file in 'Fleet-Tray.ps1','FleetTray.Functions.ps1','AgentFleet.ico') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $scriptsRoot 'tray')
    }
    $fixtureScripts = @(Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
    foreach ($port in 8033,3033) {
        $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,$port)
        try { $probe.Start() } finally { $probe.Stop() }
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'backend'),(Join-Path $root 'web'),(Join-Path $root 'menu'),(Join-Path $root 'desktop') -Force
    Set-Content -LiteralPath (Join-Path $root 'backend\AgentFleet.exe') -Value 'fixture, never executed'
    $taskScript = @'
param([string]$Root)
Set-Content -LiteralPath (Join-Path $Root 'health-mode.txt') -Value 'up'
Add-Content -LiteralPath (Join-Path $Root 'backend-starts.txt') -Value 'started'
while ($true) { Start-Sleep -Seconds 1 }
'@
    Set-Content -LiteralPath (Join-Path $root 'backend\fake-task.ps1') -Value $taskScript -Encoding UTF8
    $webScript = @'
const http = require('http');
http.createServer((request, response) => {
  response.writeHead(200, {'Content-Type': 'text/plain'});
  response.end('fixture web page');
}).listen(3033, '127.0.0.1');
'@
    Set-Content -LiteralPath (Join-Path $root 'web\server.js') -Value $webScript -Encoding UTF8
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddHours(1)
    $backendAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $root 'backend\fake-task.ps1') + '" -Root "' + $root + '"') -WorkingDirectory (Join-Path $root 'backend')
    $frontendAction = New-ScheduledTaskAction -Execute (Get-Command node.exe).Source -Argument ('"' + (Join-Path $root 'web\server.js') + '"') -WorkingDirectory (Join-Path $root 'web')
    foreach ($entry in @(@($backendName,$backendAction),@($frontendName,$frontendAction))) {
        Register-ScheduledTask -TaskName $entry[0] -Action $entry[1] -Principal $principal -Settings $settings -Trigger $trigger | Out-Null
        $null = $createdTasks.Add($entry[0])
        Write-Host ('CREATED TASK: ' + $entry[0])
    }
    if ($Phase -eq 'Tray') {
        @{ installRoot=$root; backendUrl='http://localhost:8033'; webUrl='http://localhost:3033'; backendTask=$backendName; frontendTask=$frontendName; mode='Task'; version='fixture' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'fleet-install.json') -Encoding UTF8
        $listenerScript = @'
param([string]$Root)
$ErrorActionPreference='Stop'
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add('http://localhost:8033/')
try {
 $listener.Start()
 Set-Content -LiteralPath (Join-Path $Root 'listener-ready.txt') -Value 'ready'
 while($listener.IsListening) {
  $context=$listener.GetContext()
  $path=$context.Request.Url.AbsolutePath
  Add-Content -LiteralPath (Join-Path $Root 'health-requests.txt') -Value $path
  $context.Response.ContentType='application/json'
  $body='[]'
  if($path -eq '/health') {
   $mode=Get-Content -LiteralPath (Join-Path $Root 'health-mode.txt') -ErrorAction SilentlyContinue
   if($mode -eq 'up') {$body='{"status":"ok"}'} else {$context.Response.StatusCode=404; $body='not started'}
  }
  $bytes=[Text.Encoding]::UTF8.GetBytes($body)
  $context.Response.ContentLength64=$bytes.Length
  $context.Response.OutputStream.Write($bytes,0,$bytes.Length)
  $context.Response.Close()
 }
} catch { Set-Content -LiteralPath (Join-Path $Root 'listener-error.txt') -Value $_.Exception.Message; throw }
finally {$listener.Close()}
'@
        $listenerPath = Join-Path $root 'listener.ps1'
        Set-Content -LiteralPath $listenerPath -Value $listenerScript -Encoding UTF8
        $listenerProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $listenerPath + '" -Root "' + $root + '"') -WindowStyle Hidden -PassThru
        Wait-TrayCheck { Test-Path -LiteralPath (Join-Path $root 'listener-ready.txt') } 'fake health listener'
        Start-ScheduledTask -TaskName $backendName
        Start-ScheduledTask -TaskName $frontendName
        Wait-TrayCheck { (Test-Path (Join-Path $root 'health-mode.txt')) -and (Get-ScheduledTask -TaskName $frontendName).State -eq 'Running' } 'fixture tasks started'
        $env:AGENT_FLEET_INSTALL_ROOT = $root
        $env:FLEET_TRAY_NO_BROWSER = '1'
        Start-CheckTray ''
        Wait-TrayCheck { (Get-ResidentPid) -gt 0 } 'resident tray'
        $residentPid = Get-ResidentPid
        Assert-WindowsTray 'Hidden launcher creates the resident tray' ([bool](Get-Process -Id $residentPid -ErrorAction SilentlyContinue))
        Start-Sleep -Seconds 17
        $polls = @(Get-Content -LiteralPath (Join-Path $root 'health-requests.txt') | Where-Object { $_ -eq '/health' }).Count
        Assert-WindowsTray 'Tray polls fake health every 15 seconds' ($polls -ge 2)
        Start-CheckTray '--open'
        Wait-TrayCheck { Test-Path -LiteralPath (Join-Path $root 'tray-open-url.txt') } 'second launch open'
        Assert-WindowsTray 'Second Open retains the same resident instance' ((Get-ResidentPid) -eq $residentPid)
        Start-CheckTray '--close'
        Wait-TrayCheck { -not (Get-Process -Id $residentPid -ErrorAction SilentlyContinue) } 'resident close'
        $backend = Get-ScheduledTask -TaskName $backendName
        $frontend = Get-ScheduledTask -TaskName $frontendName
        Assert-WindowsTray 'Close leaves tasks disabled and stopped' (-not $backend.Settings.Enabled -and -not $frontend.Settings.Enabled -and $backend.State -ne 'Running' -and $frontend.State -ne 'Running')
        Assert-WindowsTray 'Close removes the resident tray process' (-not (Get-Process -Id $residentPid -ErrorAction SilentlyContinue))
        Start-CheckTray ''
        Start-Sleep -Seconds 3
        Assert-WindowsTray 'Closed fleet stays closed at a resident launch' ((Get-ResidentPid) -eq 0 -and -not (Get-ScheduledTask -TaskName $backendName).Settings.Enabled)
        Set-Content -LiteralPath (Join-Path $root 'health-mode.txt') -Value 'down'
        Remove-Item -LiteralPath (Join-Path $root 'tray-open-url.txt') -ErrorAction SilentlyContinue
        Start-CheckTray '--open'
        Wait-TrayCheck { (Get-ResidentPid) -gt 0 -and (Test-Path -LiteralPath (Join-Path $root 'tray-open-url.txt')) } 'Open restarts fixture'
        $residentPid = Get-ResidentPid
        $backend = Get-ScheduledTask -TaskName $backendName
        $frontend = Get-ScheduledTask -TaskName $frontendName
        Assert-WindowsTray 'Open enables and starts both tasks' ($backend.Settings.Enabled -and $frontend.Settings.Enabled -and $backend.State -eq 'Running' -and $frontend.State -eq 'Running')
        $url = (Get-Content -LiteralPath (Join-Path $root 'tray-open-url.txt') -Raw).Trim()
        Assert-WindowsTray 'Open records only the fixture web URL' ($url -eq 'http://localhost:3033')
        $page = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
        Assert-WindowsTray 'Recorded URL reaches the fake web listener' ($page.Content -eq 'fixture web page')
        Start-CheckTray '--close'
        Wait-TrayCheck { -not (Get-Process -Id $residentPid -ErrorAction SilentlyContinue) } 'final close'
        Assert-WindowsTray 'Tray records no handler errors' (-not (Test-Path -LiteralPath (Join-Path $root 'tray-error.txt')))
    } else {
        $installer = Join-Path $scriptsRoot 'Install-FleetTray.ps1'
        $options = @{ InstallRoot=$root; BackendName=$backendName; FrontendName=$frontendName; TrayName=$trayName; BackendPort=8033; FrontendPort=3033; Version='fixture'; StartMenuFolder=(Join-Path $root 'menu'); DesktopFolder=(Join-Path $root 'desktop'); DesktopShortcut=$true }
        Set-Content -LiteralPath (Join-Path $root 'menu\keep.txt') -Value 'unrelated file'
        $beforeFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
        $beforeTasks = @(Get-ScheduledTask -TaskName ($prefix + '*') | ForEach-Object { $_.TaskName }) -join '|'
        & $installer @options -DryRun
        $afterFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
        $afterTasks = @(Get-ScheduledTask -TaskName ($prefix + '*') | ForEach-Object { $_.TaskName }) -join '|'
        Assert-WindowsTray 'Installer DryRun changes no files or tasks' ($beforeFiles -eq $afterFiles -and $beforeTasks -eq $afterTasks)
        $backendXml = Export-ScheduledTask -TaskName $backendName
        $frontendXml = Export-ScheduledTask -TaskName $frontendName
        $null = $createdTasks.Add($trayName)
        & $installer @options
        Write-Host ('CREATED TASK: ' + $trayName)
        $task = Get-ScheduledTask -TaskName $trayName
        Assert-WindowsTray 'Tray task uses the current user' ($task.Principal.UserId -in @($user, $env:USERNAME, [Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
        $expectedArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "' + $launcher + '"'
        Assert-WindowsTray 'Tray task points at the hidden launcher' (@($task.Actions).Count -eq 1 -and $task.Actions[0].Execute -eq $hostExe -and $task.Actions[0].Arguments -eq $expectedArguments)
        Assert-WindowsTray 'Tray task has no repetition' (@($task.Triggers).Count -eq 1 -and $task.Triggers[0].CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and -not $task.Triggers[0].Repetition.Interval)
        Assert-WindowsTray 'Tray logon trigger belongs to the current user' ($task.Triggers[0].UserId -in @($user,$env:USERNAME,[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
        Assert-WindowsTray 'Tray task uses a limited interactive principal' ($task.Principal.RunLevel -eq 'Limited' -and $task.Principal.LogonType -eq 'Interactive')
        $shell = New-Object -ComObject WScript.Shell
        try {
            foreach ($folder in 'menu','desktop') {
                $shortcutPath = Join-Path $root ($folder + '\Agent Fleet.lnk')
                $link = $shell.CreateShortcut($shortcutPath)
                try { Assert-WindowsTray ('Shortcut fields in ' + $folder) ((Test-Path -LiteralPath $shortcutPath) -and $link.TargetPath -eq $hostExe -and $link.Arguments -eq ($expectedArguments + ' --open') -and $link.IconLocation -eq ((Join-Path $scriptsRoot 'tray\AgentFleet.ico') + ',0') -and $link.Description -eq 'Open Agent Fleet' -and $link.WindowStyle -eq 7) }
                finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($link) | Out-Null }
            }
        } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
        $record = Get-Content -LiteralPath (Join-Path $root 'fleet-install.json') -Raw | ConvertFrom-Json
        Assert-WindowsTray 'Install record has the supplied root names ports and version' ($record.installRoot -eq $root -and $record.backendTask -eq $backendName -and $record.frontendTask -eq $frontendName -and $record.backendUrl -eq 'http://localhost:8033' -and $record.webUrl -eq 'http://localhost:3033' -and $record.mode -eq 'Task' -and $record.version -eq 'fixture')
        Assert-WindowsTray 'Installer copies no scripts into the install' ($fixtureScripts -eq (@(Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'))
        $savedFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash + ':' + $_.LastWriteTimeUtc.Ticks }) -join '|'
        $savedTask = Export-ScheduledTask -TaskName $trayName
        $secondOutput = & $installer @options 6>&1 | Out-String
        $currentFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash + ':' + $_.LastWriteTimeUtc.Ticks }) -join '|'
        Assert-WindowsTray 'Second install is a no-op' ($currentFiles -eq $savedFiles -and (Export-ScheduledTask -TaskName $trayName) -eq $savedTask -and $secondOutput.Contains('Tray task unchanged'))
        & $installer @options -Uninstall -DryRun
        $currentFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash + ':' + $_.LastWriteTimeUtc.Ticks }) -join '|'
        Assert-WindowsTray 'Uninstall DryRun changes nothing' ($currentFiles -eq $savedFiles -and (Export-ScheduledTask -TaskName $trayName) -eq $savedTask)
        & $installer @options -Uninstall
        Assert-WindowsTray 'Uninstall removes only tray task shortcuts and record' (-not (Get-ScheduledTask -TaskName $trayName -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath (Join-Path $root 'menu\Agent Fleet.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $root 'desktop\Agent Fleet.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $root 'fleet-install.json')) -and (Test-Path -LiteralPath (Join-Path $root 'menu\keep.txt')) -and (Export-ScheduledTask -TaskName $backendName) -eq $backendXml -and (Export-ScheduledTask -TaskName $frontendName) -eq $frontendXml)
        & $installer @options -Uninstall
        Assert-WindowsTray 'Repeated uninstall keeps fleet files' (Test-Path -LiteralPath (Join-Path $root 'backend\AgentFleet.exe'))
    }
    if ($script:Checks -eq 0) { throw 'No matching check was run.' }
    Write-Host "All $script:Checks Windows tray checks passed."
} catch { Write-Host $_.Exception.Message; $exitCode=1 }
finally {
    foreach ($name in $createdTasks) {
        if ($name.StartsWith($prefix) -and (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            Write-Host ('REMOVED TASK: ' + $name)
        }
    }
    if ($listenerProcess -and -not $listenerProcess.HasExited) { Stop-Process -Id $listenerProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($createdRoot) {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -in @('powershell.exe','node.exe') -and $_.CommandLine -and $_.CommandLine.Contains($root)
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    $env:AGENT_FLEET_INSTALL_ROOT = $previousRoot
    $env:FLEET_TRAY_NO_BROWSER = $previousBrowser
    $full = [IO.Path]::GetFullPath($root)
    if ($full -ne 'C:\fleet-test-tray') { throw 'Refusing cleanup outside the fixed fixture folder.' }
    if ($createdRoot -and (Test-Path -LiteralPath $full)) {
        $reparse = @((Get-Item -LiteralPath $full), (Get-ChildItem -LiteralPath $full -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($reparse.Count) { throw 'Refusing recursive cleanup through a reparse point.' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    Write-Host ('REMOVED TEMP INSTALL: ' + (Split-Path $full -Leaf))
    if (@(Get-ScheduledTask -TaskName ($prefix + '*') -ErrorAction SilentlyContinue).Count) { throw 'A throwaway task remained after cleanup.' }
}
exit $exitCode
