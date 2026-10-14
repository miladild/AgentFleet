<#
.SYNOPSIS
    Adds the Windows tray, Start Menu shortcut and logon task to an existing install.
.DESCRIPTION
    Copies no files. Keep this scripts folder where it is. The install record is fleet-install.json in
    the install root. Run twice to update only changed settings; -Uninstall removes the tray registration,
    shortcuts and install record without removing the backend or web UI.
.PARAMETER InstallRoot
    Uses the shared install resolver: parameter, environment, registered backend, then the standard folder.
.PARAMETER DesktopShortcut
    Also puts an Agent Fleet shortcut on the Desktop.
.PARAMETER StartMenuFolder
    Override the shortcut folder, for example for isolated installation checks.
.PARAMETER DesktopFolder
    Override the Desktop folder when checking -DesktopShortcut.
.PARAMETER DryRun
    Prints the changes without writing files or registering a task.
.EXAMPLE
    .\scripts\Install-FleetTray.ps1
.EXAMPLE
    .\scripts\Install-FleetTray.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$BackendName = 'AgentFleetBackend',
    [string]$FrontendName = 'AgentFleetFrontend',
    [string]$TrayName = 'AgentFleetTray',
    [ValidateSet('Task','Service')][string]$Mode,
    [ValidateRange(0,65535)][int]$BackendPort = 0,
    [ValidateRange(0,65535)][int]$FrontendPort = 0,
    [string]$Version,
    [string]$StartMenuFolder = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs\Agent Fleet'),
    [string]$DesktopFolder = ([Environment]::GetFolderPath('DesktopDirectory')),
    [switch]$DesktopShortcut,
    [switch]$Uninstall,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
. "$PSScriptRoot\tray\FleetTray.Functions.ps1"
$InstallRoot = [IO.Path]::GetFullPath((Resolve-InstallRoot $InstallRoot $BackendName))
$found = Get-FleetInstall -InstallRoot $InstallRoot -ScriptRoot $PSScriptRoot -BackendName $BackendName -FrontendName $FrontendName
$launcher = Join-Path $PSScriptRoot 'tray\Fleet-Tray.ps1'
$icon = Join-Path $PSScriptRoot 'tray\AgentFleet.ico'
$hostExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$recordPath = Join-Path $InstallRoot 'fleet-install.json'
$menuLink = Join-Path $StartMenuFolder 'Agent Fleet.lnk'
$desktopLink = Join-Path $DesktopFolder 'Agent Fleet.lnk'
$taskArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "' + $launcher + '"'
$arguments = $taskArguments + ' --open'
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
function Do-Step([string]$What, [scriptblock]$Block) {
    if ($DryRun) { Write-Info "would: $What"; return }
    & $Block
}
function Get-Link([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    try { return $shell.CreateShortcut($Path) } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
}
if ($Uninstall) {
    $task = Get-ScheduledTask -TaskName $TrayName -ErrorAction SilentlyContinue
    if ($task) {
        if (@($task.Actions | Where-Object { $_.Arguments -eq $taskArguments }).Count -ne 1) { throw 'This tray task belongs to another scripts folder. Use that folder to remove it.' }
        Do-Step "stop and remove the tray task $TrayName" { Stop-ScheduledTask -TaskName $TrayName -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $TrayName -Confirm:$false }
    }
    foreach ($path in $menuLink,$desktopLink) {
        $link = Get-Link $path
        if ($link) {
            try {
                if ($link.Arguments -ne $arguments) { throw 'The shortcut belongs to another scripts folder.' }
                Do-Step "remove $path" { Remove-Item -LiteralPath $path }
            } finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($link) | Out-Null }
        }
    }
    if (Test-Path -LiteralPath $recordPath) { Do-Step 'remove the install record' { Remove-Item -LiteralPath $recordPath } }
    if ((Test-Path -LiteralPath $StartMenuFolder) -and @(Get-ChildItem -LiteralPath $StartMenuFolder -Force).Count -eq 0) {
        Do-Step 'remove the empty Start Menu folder' { Remove-Item -LiteralPath $StartMenuFolder }
    }
    Write-Ok 'Tray removal complete; the fleet registration and files are kept'
    return
}
if (-not (Test-Path -LiteralPath $launcher) -or -not (Test-Path -LiteralPath $icon)) { throw 'The tray files are missing from this scripts folder.' }
if (-not $found -and -not $DryRun) { throw 'Agent Fleet is not installed here. Run Install-Autostart.ps1 first.' }
if ([IO.Path]::GetFullPath($PSScriptRoot).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { Write-Warn 'The tray runs from a temporary folder. Move this download to a lasting folder before installing it.' }
$backendUrl = 'http://localhost:8000'
$webUrl = 'http://localhost:3000'
if ($found) { $backendUrl=$found.backendUrl; $webUrl=$found.webUrl; if (-not $Mode) { $Mode=$found.mode }; if (-not $Version) { $Version=$found.version } }
if (-not $Mode) { $Mode='Task' }
if ($BackendPort) { $backendUrl='http://localhost:' + $BackendPort }
if ($FrontendPort) { $webUrl='http://localhost:' + $FrontendPort }
if (-not $Version -or $Version -eq 'unknown') {
    $package = Join-Path (Split-Path $PSScriptRoot -Parent) 'agent-fleet\package.json'
    if (Test-Path -LiteralPath $package) { $Version=(Get-Content -LiteralPath $package -Raw | ConvertFrom-Json).version }
    else { $Version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $InstallRoot 'backend\AgentFleet.exe')).ProductVersion }
    if (-not $Version) { $Version='unknown' }
}
$record = [ordered]@{ installRoot=$InstallRoot; backendUrl=$backendUrl; webUrl=$webUrl; backendTask=$BackendName; frontendTask=$FrontendName; mode=$Mode; version=$Version }
$json = ($record | ConvertTo-Json) + [Environment]::NewLine
if (-not (Test-Path -LiteralPath $recordPath) -or [IO.File]::ReadAllText($recordPath) -ne $json) {
    Do-Step 'write fleet-install.json' { [IO.File]::WriteAllText($recordPath,$json,(New-Object Text.UTF8Encoding($false))) }
} else { Write-Info 'Install record unchanged' }
$task = Get-ScheduledTask -TaskName $TrayName -ErrorAction SilentlyContinue
$sameUser = $false
if ($task) {
    # Task Scheduler can return the current user's name without its account prefix.
    $sameUser = $task.Principal.UserId -in @($user, $env:USERNAME, [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    try {
        $registeredUser = $task.Principal.UserId
        if (-not $sameUser) {
            if ($registeredUser -notmatch '^S-\d-') { $registeredUser = (New-Object Security.Principal.NTAccount($registeredUser)).Translate([Security.Principal.SecurityIdentifier]).Value }
            $sameUser = $registeredUser -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        }
    } catch { }
}
$sameTask = $task -and @($task.Actions).Count -eq 1 -and $task.Actions[0].Execute -eq $hostExe -and $task.Actions[0].Arguments -eq $taskArguments -and
    @($task.Triggers).Count -eq 1 -and $task.Triggers[0].CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and -not $task.Triggers[0].Repetition.Interval -and $task.Triggers[0].UserId -in @($user,$env:USERNAME,[Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -and
    $task.Principal.RunLevel -eq 'Limited' -and $task.Principal.LogonType -eq 'Interactive' -and $sameUser
if (-not $sameTask) {
    Do-Step "register the logon-only tray task $TrayName" {
        $action = New-ScheduledTaskAction -Execute $hostExe -Argument $taskArguments
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TrayName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Agent Fleet tray' -Force | Out-Null
    }
} else { Write-Info 'Tray task unchanged' }
$paths = @($menuLink)
if ($DesktopShortcut) { $paths += $desktopLink }
foreach ($path in $paths) {
    $existing = Get-Link $path
    $sameLink = $false
    if ($existing) {
        try { $sameLink = $existing.TargetPath -eq $hostExe -and $existing.Arguments -eq $arguments -and $existing.IconLocation -eq ($icon + ',0') -and $existing.Description -eq 'Open Agent Fleet' -and $existing.WorkingDirectory -eq $PSScriptRoot -and $existing.WindowStyle -eq 7 }
        finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($existing) | Out-Null }
    }
    if (-not $sameLink) {
        Do-Step "create the shortcut $path" {
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent)
            $shell = New-Object -ComObject WScript.Shell
            $link = $null
            try {
                $link = $shell.CreateShortcut($path)
                $link.TargetPath=$hostExe; $link.Arguments=$arguments; $link.IconLocation=$icon + ',0'
                $link.WorkingDirectory=$PSScriptRoot; $link.Description='Open Agent Fleet'; $link.WindowStyle=7; $link.Save()
            } finally { if ($link) { [Runtime.InteropServices.Marshal]::ReleaseComObject($link) | Out-Null }; [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
        }
    } else { Write-Info 'Shortcut unchanged' }
}
Write-Ok 'Tray and shortcut are ready. Use the Agent Fleet Start Menu shortcut to open it.'
