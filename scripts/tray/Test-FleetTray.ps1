<#
.SYNOPSIS
    Checks tray decisions with fakes; never starts or changes registered tasks.
#>
[CmdletBinding()]
param([string]$Check)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetTray.Functions.ps1"
$script:Checks = 0
function Assert-Tray([string]$Name, [bool]$Condition) {
    if ($Check -and $Check -ne $Name) { return }
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:Checks++
    Write-Host "PASS: $Name"
}
function New-FakeRunner([bool]$Healthy, [bool]$Ready = $true, $Plans = @(), [bool]$Confirm = $true) {
    $calls = New-Object Collections.ArrayList
    $runner = @{ Calls = $calls }
    foreach ($verb in 'EnableTask','DisableTask','StartTask','StopTask','StartService','StopService','StartPortable','StopProcessesOfInstall','OpenUrl') {
        $action = $verb
        $runner[$verb] = { param($value) $label = $value; if ($action -in @('StopProcessesOfInstall','StartPortable')) { $label = 'install' }; $null = $calls.Add($action + ':' + $label) }.GetNewClosure()
    }
    $runner.Health = { param($install) [pscustomobject]@{ Answers = $Healthy; Plans = $Plans } }.GetNewClosure()
    $runner.GetPlans = { param($install) $Plans }.GetNewClosure()
    $runner.Confirm = { param($title) $Confirm }.GetNewClosure()
    $runner.Wait = { param($install,$seconds) $null = $calls.Add('Wait:' + $seconds); $Ready }.GetNewClosure()
    return $runner
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('fleet-tray-check-' + [Guid]::NewGuid().ToString('N'))
try {
    $null = New-Item -ItemType Directory -Path $tempRoot
    $absent = { param($name) $null }
    $roots = @{}
    foreach ($key in 'parameter','environment','registered','portable') {
        $folder = Join-Path $tempRoot $key
        $null = New-Item -ItemType Directory -Path (Join-Path $folder 'backend') -Force
        Set-Content -LiteralPath (Join-Path $folder 'backend\AgentFleet.exe') -Value 'fixture'
        $roots[$key] = $folder
    }
    $taskFolder = Join-Path $roots.registered 'backend'
    $registered = { param($name) if ($name -eq 'fixture-backend') { [pscustomobject]@{ Actions = @([pscustomobject]@{ Arguments = "Set-Location -LiteralPath '$taskFolder'; run --urls http://localhost:8033"; Execute='powershell.exe'; WorkingDirectory='' }) } } }.GetNewClosure()
    $base = @{ BackendName='fixture-backend'; FrontendName='fixture-frontend'; TaskLookup=$registered; ServiceLookup=$absent; DefaultRoot=(Join-Path $tempRoot 'missing'); ScriptRoot=(Join-Path $roots.portable 'scripts') }
    $found = Get-FleetInstall @base -InstallRoot $roots.parameter -EnvironmentRoot $roots.environment
    Assert-Tray 'Explicit install wins' ($found.installRoot -eq $roots.parameter -and $found.Sources.installRoot -eq 'parameter')
    $found = Get-FleetInstall @base -EnvironmentRoot $roots.environment
    Assert-Tray 'Environment install wins over registration' ($found.installRoot -eq $roots.environment)
    $found = Get-FleetInstall @base -EnvironmentRoot ''
    Assert-Tray 'Registered task precedes portable folder' ($found.installRoot -eq $roots.registered -and $found.backendUrl -eq 'http://localhost:8033')
    Assert-Tray 'A missing explicit install does not borrow another registration' ($null -eq (Get-FleetInstall @base -InstallRoot (Join-Path $tempRoot 'missing-explicit') -EnvironmentRoot ''))
    $base.TaskLookup = $absent
    $found = Get-FleetInstall @base -EnvironmentRoot ''
    Assert-Tray 'Portable folder is found' ($found.installRoot -eq $roots.portable)
    $base.ScriptRoot = Join-Path $tempRoot 'not-a-release\scripts'
    Assert-Tray 'Missing install returns nothing' ($null -eq (Get-FleetInstall @base -EnvironmentRoot ''))
    $record = @{ installRoot=$roots.parameter; backendUrl='http://localhost:8033'; webUrl='http://localhost:3033'; backendTask='fixture-backend'; frontendTask='fixture-frontend'; mode='Task'; version='fixture' }
    $record | ConvertTo-Json | Set-Content (Join-Path $roots.parameter 'fleet-install.json')
    $found = Get-FleetInstall @base -InstallRoot $roots.parameter -EnvironmentRoot ''
    Assert-Tray 'Stored install values win' ($found.webUrl -eq $record.webUrl -and $found.Sources.webUrl -eq 'json' -and $found.version -eq 'fixture')
    $derived = { param($name) [pscustomobject]@{ Actions=@([pscustomobject]@{ Arguments="run --urls http://localhost:8034; `$env:PORT='3034'"; Execute='powershell.exe'; WorkingDirectory='' }) } }
    $base.TaskLookup = $derived
    $found = Get-FleetInstall @base -InstallRoot $roots.parameter -EnvironmentRoot ''
    Assert-Tray 'Stored addresses beat registered addresses' ($found.backendUrl -eq 'http://localhost:8033' -and $found.webUrl -eq 'http://localhost:3033')
    $base.TaskLookup = $absent
    $record.installRoot = $roots.environment
    $record | ConvertTo-Json | Set-Content (Join-Path $roots.parameter 'fleet-install.json')
    $found = Get-FleetInstall @base -InstallRoot $roots.parameter -EnvironmentRoot ''
    Assert-Tray 'Stored root also supplies the default web folder' ($found.installRoot -eq $roots.environment -and $found.WebDirectory -eq (Join-Path $roots.environment 'web') -and $found.Sources.installRoot -eq 'json')
    $install = [pscustomobject]@{ backendTask='backend'; frontendTask='frontend'; mode='Task'; Portable=$false; webUrl='http://localhost:3033' }
    foreach ($case in @(
        @('running',$true,$true,$true,$false,$false),
        @('stopped',$false,$false,$false,$false,$false),
        @('starting',$false,$true,$true,$true,$false),
        @('starting',$false,$false,$true,$false,$true),
        @('notanswering',$false,$true,$true,$false,$false)
    )) {
        $c = $case
        $health = { param($i) [pscustomobject]@{ Answers=$c[1]; Plans=@([pscustomobject]@{ status='approved'; title='Example work' }) } }.GetNewClosure()
        $tasks = { param($i) [pscustomobject]@{ BackendEnabled=$c[2]; FrontendEnabled=$c[3]; BackendRunning=$c[4]; FrontendRunning=$c[5] } }.GetNewClosure()
        $state = Get-FleetState $install $health $tasks
        Assert-Tray ('State ' + $c[0]) ($state.State -eq $c[0] -and $state.PlanTitle -eq 'Example work')
    }
    Assert-Tray 'No install state' ((Get-FleetState $null $null $null).State -eq 'notinstalled')
    $combination = 0
    foreach ($answers in $false,$true) {
        foreach ($backendEnabled in $false,$true) {
            foreach ($frontendEnabled in $false,$true) {
                foreach ($backendRunning in $false,$true) {
                    foreach ($frontendRunning in $false,$true) {
                        $combination++
                        $health = { param($i) [pscustomobject]@{ Answers=$answers; Plans=@() } }.GetNewClosure()
                        $tasks = { param($i) [pscustomobject]@{ BackendEnabled=$backendEnabled; FrontendEnabled=$frontendEnabled; BackendRunning=$backendRunning; FrontendRunning=$frontendRunning } }.GetNewClosure()
                        $expected = 'notanswering'
                        if ($answers) { $expected = 'running' }
                        elseif (-not $backendEnabled -and -not $frontendEnabled) { $expected = 'stopped' }
                        elseif ($backendRunning -or $frontendRunning) { $expected = 'starting' }
                        $state = Get-FleetState $install $health $tasks
                        Assert-Tray ('State combination ' + $combination) ($state.State -eq $expected -and $null -eq $state.PlanTitle)
                    }
                }
            }
        }
    }
    $plan = @([pscustomobject]@{ status='running'; title='Example work' })
    $runner = New-FakeRunner $true $true $plan $false
    $result = Invoke-FleetClose $install $runner
    Assert-Tray 'Declining a running plan changes nothing' (-not $result.Success -and $runner.Calls.Count -eq 0)
    $approved = @([pscustomobject]@{ status='approved'; title='Example work' })
    $runner = New-FakeRunner $true $true $approved $false
    $result = Invoke-FleetClose $install $runner
    Assert-Tray 'Declining an approved plan changes nothing' (-not $result.Success -and $runner.Calls.Count -eq 0)
    $runner = New-FakeRunner $true $true $plan $true
    $null = Invoke-FleetClose $install $runner
    Assert-Tray 'Close disables before stopping' (($runner.Calls -join ',') -eq 'DisableTask:backend,DisableTask:frontend,StopTask:backend,StopTask:frontend,StopProcessesOfInstall:install')
    $runner = New-FakeRunner $true
    $runner.Confirm = { throw 'Unexpected confirmation' }
    Assert-Tray 'Close without a plan asks nothing' ((Invoke-FleetClose $install $runner).Success)
    $runner = New-FakeRunner $true
    $null = Invoke-FleetOpen $install $runner
    Assert-Tray 'Healthy Open only opens the URL' (($runner.Calls -join ',') -eq 'OpenUrl:http://localhost:3033')
    $runner = New-FakeRunner $false
    $null = Invoke-FleetOpen $install $runner
    Assert-Tray 'Open starts and waits before opening' (($runner.Calls -join ',') -eq 'EnableTask:backend,EnableTask:frontend,StartTask:backend,StartTask:frontend,Wait:90,OpenUrl:http://localhost:3033')
    $runner = New-FakeRunner $false $false
    $result = Invoke-FleetOpen $install $runner
    Assert-Tray 'Failed wait opens nothing' (-not $result.Success -and $result.Reason -eq 'did not start' -and @($runner.Calls | Where-Object { $_ -like 'OpenUrl:*' }).Count -eq 0)
    $install.mode = 'Service'
    $runner = New-FakeRunner $false
    $null = Invoke-FleetOpen $install $runner
    Assert-Tray 'Service Open uses service verbs' (($runner.Calls -join ',') -eq 'StartService:backend,StartService:frontend,Wait:90,OpenUrl:http://localhost:3033')
    $runner = New-FakeRunner $true
    $null = Invoke-FleetClose $install $runner
    Assert-Tray 'Service Close uses service verbs' (($runner.Calls -join ',') -eq 'StopService:backend,StopService:frontend,StopProcessesOfInstall:install')
    $tip = Get-FleetTooltip ([pscustomobject]@{ State='notanswering'; PlanTitle=('Long title ' * 20) })
    Assert-Tray 'Tooltip has words and at most 63 characters' ($tip.StartsWith('Agent Fleet: not answering') -and $tip.Length -eq 63)
    if ($script:Checks -eq 0) { throw 'No matching check was run.' }
    Write-Host "All $script:Checks tray logic checks passed."
} catch { Write-Error $_; exit 1 }
finally {
    $full = [IO.Path]::GetFullPath($tempRoot)
    if ($full.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $full)) { Remove-Item -LiteralPath $full -Recurse -Force }
}
