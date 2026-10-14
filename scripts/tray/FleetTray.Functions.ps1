# Shared tray logic. Task, health and action callbacks can be replaced by tests.
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'FleetCommon.ps1')

function Get-FleetActionFolder($Action) {
    if ($Action.WorkingDirectory) { return $Action.WorkingDirectory }
    if ($Action.Arguments -match "-LiteralPath '([^']+)'") { return $Matches[1] }
    if ($Action.Execute -match 'AgentFleet\.exe$') { return (Split-Path $Action.Execute -Parent) }
    return $null
}

function Get-FleetInstall {
    param(
        [string]$InstallRoot,
        [string]$ScriptRoot = (Split-Path $PSScriptRoot -Parent),
        [string]$BackendName = 'AgentFleetBackend',
        [string]$FrontendName = 'AgentFleetFrontend',
        [string]$EnvironmentRoot = $env:AGENT_FLEET_INSTALL_ROOT,
        [string]$DefaultRoot = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentFleet'),
        [scriptblock]$TaskLookup = { param($name) Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue },
        [scriptblock]$ServiceLookup = { param($name) Get-CimInstance Win32_Service -Filter "Name='$($name.Replace("'", "''"))'" -ErrorAction SilentlyContinue }
    )
    $root = $null
    $source = $null
    $backendTask = $null
    $backendService = $null
    if ($InstallRoot) { $root = Resolve-InstallRoot $InstallRoot $BackendName; $source = 'parameter' }
    elseif ($EnvironmentRoot) { $root = Resolve-InstallRoot $EnvironmentRoot $BackendName; $source = 'environment' }
    else {
        $backendTask = & $TaskLookup $BackendName
        if ($backendTask) {
            foreach ($action in $backendTask.Actions) {
                $folder = Get-FleetActionFolder $action
                if ($folder -and (Split-Path $folder -Leaf) -eq 'backend') { $root = Resolve-InstallRoot (Split-Path $folder -Parent); $source = 'task'; break }
            }
        }
        if (-not $root) {
            $backendService = & $ServiceLookup $BackendName
            if ($backendService -and $backendService.PathName -match '^\s*"?(.+?AgentFleet\.exe)"?(?:\s|$)') {
                $root = Resolve-InstallRoot (Split-Path (Split-Path $Matches[1] -Parent) -Parent)
                $source = 'service'
            }
        }
        if (-not $root) {
            $portable = Split-Path $ScriptRoot -Parent
            if (Test-Path -LiteralPath (Join-Path $portable 'backend\AgentFleet.exe')) { $root = Resolve-InstallRoot $portable; $source = 'portable' }
            else { $root = Resolve-InstallRoot $DefaultRoot; $source = 'default' }
        }
    }
    $root = [IO.Path]::GetFullPath($root)
    $metadata = $null
    $metadataPath = Join-Path $root 'fleet-install.json'
    if (Test-Path -LiteralPath $metadataPath) { $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json }
    # Read stored names before looking up tasks, including when a caller uses a separate install.
    if ($metadata.backendTask) { $BackendName = $metadata.backendTask }
    if ($metadata.frontendTask) { $FrontendName = $metadata.frontendTask }
    $backendTask = & $TaskLookup $BackendName
    $frontendTask = & $TaskLookup $FrontendName
    $backendService = & $ServiceLookup $BackendName
    if (-not $metadata -and -not (Test-Path -LiteralPath (Join-Path $root 'backend\AgentFleet.exe'))) { return $null }
    $values = [ordered]@{
        installRoot = $root; backendUrl = 'http://localhost:8000'; webUrl = 'http://localhost:3000'
        backendTask = $BackendName; frontendTask = $FrontendName; mode = 'Task'; version = 'unknown'
    }
    $sources = [ordered]@{
        installRoot = $source; backendUrl = 'default'; webUrl = 'default'; backendTask = 'default'
        frontendTask = 'default'; mode = 'default'; version = 'default'
    }
    if ($PSBoundParameters.ContainsKey('BackendName')) { $sources.backendTask = 'parameter' }
    if ($PSBoundParameters.ContainsKey('FrontendName')) { $sources.frontendTask = 'parameter' }
    if ($backendTask) { $sources.mode = 'task' }
    $backendArguments = ''
    if ($backendTask) { $backendArguments = ($backendTask.Actions | ForEach-Object { $_.Arguments }) -join ' ' }
    elseif ($backendService) { $backendArguments = $backendService.PathName; $values.mode = 'Service'; $sources.mode = 'service' }
    if ($backendArguments -match '--urls\s+["'']?(http://[^\s"'';]+)') {
        $values.backendUrl = $Matches[1].Replace('0.0.0.0', 'localhost').TrimEnd('/')
        $sources.backendUrl = 'registration'
    }
    if ($backendArguments -match "FLEET_FRONTEND_URL='([^']+)'") { $values.webUrl = $Matches[1]; $sources.webUrl = 'registration' }
    $webFolder = Join-Path $root 'web'
    if ($frontendTask) {
        foreach ($action in $frontendTask.Actions) {
            $folder = Get-FleetActionFolder $action
            if ($folder) { $webFolder = $folder }
            if ($action.Arguments -match "(?:env:)?PORT='?(\d+)'?") { $values.webUrl = 'http://localhost:' + $Matches[1]; $sources.webUrl = 'registration' }
        }
    }
    foreach ($key in @($values.Keys)) {
        if ($metadata -and $metadata.PSObject.Properties[$key] -and $null -ne $metadata.$key) { $values[$key] = $metadata.$key; $sources[$key] = 'json' }
    }
    if ($values.mode -notin @('Task', 'Service')) { throw 'The install record has an unknown mode.' }
    foreach ($key in 'backendUrl', 'webUrl') {
        $uri = $null
        if (-not [Uri]::TryCreate($values[$key], [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https')) { throw 'The install record has an invalid web address.' }
    }
    $values.installRoot = [IO.Path]::GetFullPath($values.installRoot)
    if (-not $frontendTask) { $webFolder = Join-Path $values.installRoot 'web' }
    $values['Sources'] = [pscustomobject]$sources
    $values['ScriptRoot'] = $ScriptRoot
    $values['WebDirectory'] = $webFolder
    $values['Portable'] = (-not $backendTask -and -not $backendService -and (Test-Path -LiteralPath (Join-Path $values.installRoot 'web\server.js')))
    return [pscustomobject]$values
}

function Get-FleetRunningPlan($Plans) {
    return @($Plans | Where-Object { $_.status -in @('running', 'approved') } | Select-Object -First 1)[0]
}

function Get-FleetState {
    param($Install, [scriptblock]$Health, [scriptblock]$Tasks)
    if (-not $Install) { return [pscustomobject]@{ State = 'notinstalled'; PlanTitle = $null } }
    $healthResult = & $Health $Install
    $taskResult = & $Tasks $Install
    $state = 'notanswering'
    if ($healthResult.Answers) { $state = 'running' }
    elseif (-not $taskResult.BackendEnabled -and -not $taskResult.FrontendEnabled) { $state = 'stopped' }
    elseif ($taskResult.BackendRunning -or $taskResult.FrontendRunning) { $state = 'starting' }
    $plan = Get-FleetRunningPlan $healthResult.Plans
    return [pscustomobject]@{ State = $state; PlanTitle = $plan.title }
}

function Get-FleetTooltip($State) {
    $word = $State.State
    if ($word -eq 'notanswering') { $word = 'not answering' }
    $text = 'Agent Fleet: ' + $word
    if ($State.PlanTitle) { $text += ' - ' + $State.PlanTitle }
    if ($text.Length -gt 63) { $text = $text.Substring(0, 63) }
    return $text
}

function Invoke-FleetRunner($Runner, [string]$Name, $Argument, $Extra) {
    $block = $Runner[$Name]
    if (-not $block) { throw "The tray action '$Name' is unavailable." }
    return & $block $Argument $Extra
}

function Invoke-FleetOpen {
    param($Install, $Runner)
    if (-not $Install) { return [pscustomobject]@{ Success = $false; Reason = 'not installed' } }
    $health = Invoke-FleetRunner $Runner 'Health' $Install
    if (-not $health.Answers) {
        if ($Install.Portable) { Invoke-FleetRunner $Runner 'StartPortable' $Install | Out-Null }
        elseif ($Install.mode -eq 'Service') {
            foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'StartService' $name | Out-Null }
        } else {
            foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'EnableTask' $name | Out-Null }
            foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'StartTask' $name | Out-Null }
        }
        $ready = Invoke-FleetRunner $Runner 'Wait' $Install 90
        if (-not $ready) { return [pscustomobject]@{ Success = $false; Reason = 'did not start' } }
    }
    Invoke-FleetRunner $Runner 'OpenUrl' $Install.webUrl | Out-Null
    return [pscustomobject]@{ Success = $true; Reason = $null }
}

function Invoke-FleetClose {
    param($Install, $Runner)
    if (-not $Install) { return [pscustomobject]@{ Success = $false; Reason = 'not installed' } }
    $plans = Invoke-FleetRunner $Runner 'GetPlans' $Install
    $plan = Get-FleetRunningPlan $plans
    if ($plan) {
        $confirmed = Invoke-FleetRunner $Runner 'Confirm' $plan.title
        if (-not $confirmed) { return [pscustomobject]@{ Success = $false; Reason = 'cancelled' } }
    }
    if ($Install.mode -eq 'Service') {
        foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'StopService' $name | Out-Null }
    } elseif (-not $Install.Portable) {
        # Disable repeats before stopping.
        foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'DisableTask' $name | Out-Null }
        foreach ($name in $Install.backendTask, $Install.frontendTask) { Invoke-FleetRunner $Runner 'StopTask' $name | Out-Null }
    }
    Invoke-FleetRunner $Runner 'StopProcessesOfInstall' $Install | Out-Null
    return [pscustomobject]@{ Success = $true; Reason = $null }
}
