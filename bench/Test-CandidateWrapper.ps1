<#
.SYNOPSIS
    Offline checks for scripts\Test-CandidateModel.ps1 and the bench scripts it calls.

.DESCRIPTION
    Nothing is started and no model or live fleet is contacted. The candidate script only runs with -WhatIf or refuses,
    its functions are read from the file and called with synthetic configurations, and the temporary folder this check
    creates is deleted at the end. Exit code 1 on any failure.
#>
$ErrorActionPreference = 'Stop'
$wrapper = Join-Path $PSScriptRoot '..\scripts\Test-CandidateModel.ps1'
$startScript = Join-Path $PSScriptRoot 'Start-BenchBackend.ps1'
$benchScript = Join-Path $PSScriptRoot 'Invoke-Bench.ps1'

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAILED: $Name" }
    Write-Host "PASS: $Name" -ForegroundColor Green
}

function Get-ScriptAst([string]$Path) {
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
    if ($errors.Count) { throw "$Path has $($errors.Count) syntax error(s): $($errors[0].Message)" }
    return $ast
}

function Get-ParameterNames($Ast) {
    return @($Ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

function Get-Parameter($Ast, [string]$Name) {
    return @($Ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $Name })[0]
}

# Runs the candidate script in a child PowerShell with the engine that runs this check. Only -WhatIf runs and
# refusals are passed to it, so it never starts a backend.
$engine = (Get-Process -Id $PID).Path
function Invoke-CandidateScript([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & $engine -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper @Arguments 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
    }
    finally { $ErrorActionPreference = $previous }
}

function Get-FreeTestPort {
    foreach ($port in 18810..18899) {
        if (-not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) { return $port }
    }
    throw 'No free port was found between 18810 and 18899 for the dry-run checks.'
}

# Read the candidate script's functions into this scope without running its body.
$wrapperAst = Get-ScriptAst $wrapper
$startAst = Get-ScriptAst $startScript
$benchAst = Get-ScriptAst $benchScript
foreach ($definition in $wrapperAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    Invoke-Expression $definition.Extent.Text
}
. (Join-Path $PSScriptRoot '..\scripts\FleetCommon.ps1')

$configText = @'
{
  "triageModel": "triage-model:1b",
  "mode": "conservative",
  "planModeEnabled": false,
  "nodes": [
    { "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "hub-model:30b", "purpose": "Synthetic hub.", "tier": "heavy", "fallback": true },
    { "name": "worker1", "url": "http://192.0.2.21:11434/v1", "model": "worker-model-a:7b", "purpose": "Synthetic worker.", "tier": "standard" },
    { "name": "worker2", "url": "http://192.0.2.22:11434/v1", "model": "worker-model-b:3b", "purpose": "Synthetic light worker.", "tier": "light" },
    { "name": "eyes", "url": "http://192.0.2.21:11434/v1", "model": "vision-model:7b", "purpose": "Synthetic vision node.", "vision": true },
    { "name": "remote", "url": "http://192.0.2.23:1234/v1", "model": "openai-model:1b", "purpose": "Synthetic OpenAI-style node.", "api": "openai", "tier": "light" }
  ],
  "tools": { "read_file": { "enabled": true } }
}
'@
$noFallbackText = $configText -replace '"fallback": true', '"fallbackOff": true'
$config = ConvertFrom-Json -InputObject $configText
$noFallback = ConvertFrom-Json -InputObject $noFallbackText
$live = @(8000)
$reserved = @(3000, 11434)

$prefix = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'candidate-wrapper-check-'))
$scratch = $prefix + [Guid]::NewGuid().ToString('N')
# The candidate script finds a live install through this variable: pointing it at an empty folder keeps the check from
# reading the install record of a real installation.
$previousInstallRoot = $env:AGENT_FLEET_INSTALL_ROOT
try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $env:AGENT_FLEET_INSTALL_ROOT = Join-Path $scratch 'no-live-install'
    $configPath = Join-Path $scratch 'fleet.config.json'
    $noFallbackPath = Join-Path $scratch 'no-fallback.config.json'
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($configPath, $configText, $utf8)
    [IO.File]::WriteAllText($noFallbackPath, $noFallbackText, $utf8)
    $configHashBefore = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash

    # Script surface: parameters, mandatory inputs and defaults, and the bench parameters the candidate relies on.
    $wrapperParams = Get-ParameterNames $wrapperAst
    $documented = 'Node', 'Model', 'Config', 'Tasks', 'Repeat', 'Port', 'OutDir', 'AllowHub', 'Force', 'WhatIf'
    Assert-Check (@($documented | Where-Object { $wrapperParams -notcontains $_ }).Count -eq 0) 'the candidate script has every documented parameter'
    $mandatory = @($wrapperAst.ParamBlock.Parameters | Where-Object { $_.Attributes.Extent.Text -match 'Mandatory' } | ForEach-Object { $_.Name.VariablePath.UserPath })
    Assert-Check (($mandatory -join ',') -eq 'Node,Model') '-Node and -Model are mandatory'
    Assert-Check ((Get-Parameter $wrapperAst 'Port').DefaultValue.Extent.Text -eq '8010') 'the default -Port is 8010'
    Assert-Check ((Get-Parameter $wrapperAst 'Repeat').DefaultValue.Extent.Text -eq '1') 'the default -Repeat is 1'

    $startParams = Get-ParameterNames $startAst
    Assert-Check ((@('Port', 'Config', 'WorkRoot', 'NoHubModel', 'HealthTimeoutSeconds', 'Stop') | Where-Object { $startParams -notcontains $_ }).Count -eq 0) `
        'Start-BenchBackend.ps1 keeps the parameters the candidate script passes'
    Assert-Check ((Get-Parameter $startAst 'HealthTimeoutSeconds').DefaultValue.Extent.Text -eq '90') 'Start-BenchBackend.ps1 still waits 90 s by default'
    $benchParams = Get-ParameterNames $benchAst
    Assert-Check ((@('BaseUrl', 'OutDir', 'Repeat', 'Review', 'AutoRetries', 'WorkerOnly', 'Tasks') | Where-Object { $benchParams -notcontains $_ }).Count -eq 0) `
        'Invoke-Bench.ps1 has every parameter the candidate script passes'
    Assert-Check (((Get-Parameter $benchAst 'Review').Attributes.Extent.Text -join ' ') -match "'off'") 'Invoke-Bench.ps1 accepts -Review off'
    $startText = [IO.File]::ReadAllText($startScript)
    $skipSwitchUses = @($startAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandParameterAst] -and $node.ParameterName -eq 'SkipHttpErrorCheck' }, $true))
    Assert-Check ($skipSwitchUses.Count -eq 0) 'Start-BenchBackend.ps1 no longer uses a switch that Windows PowerShell 5.1 lacks'
    $wrapperText = [IO.File]::ReadAllText($wrapper)
    Assert-Check ($wrapperText -match "Review = 'off'" -and $wrapperText -match 'AutoRetries = 0' -and $wrapperText -match 'HealthTimeoutSeconds = 180') `
        'the candidate run uses no second opinion, no automatic retries and a three-minute health wait'

    # Folder names: a model tag or a path must never add folders or characters Windows refuses.
    Assert-Check ((Get-CandidateFolderPart 'model-a:7b') -eq 'model-a-7b') 'folder name: a tag separator becomes a dash'
    Assert-Check ((Get-CandidateFolderPart 'org/name:Q4_K_M') -eq 'org-name-Q4_K_M') 'folder name: slashes and colons are replaced'
    Assert-Check ((Get-CandidateFolderPart '..\..\evil') -eq 'evil') 'folder name: parent-folder steps cannot climb out'
    Assert-Check ((Get-CandidateFolderPart '///') -eq 'model') 'folder name: a name with nothing usable gets a neutral word'
    Assert-Check ((Get-CandidateFolderPart ('x' * 200)).Length -le 60) 'folder name: long names are shortened'
    Assert-Check ((Get-CandidateOutDirName 'worker1' 'model-a:7b' '20261014-120000') -eq 'candidate-worker1-model-a-7b-20261014-120000') `
        'output folder name: node, model and time'

    # The hub: the marked fallback node, else the first heavy text node, else the first text node; vision nodes never.
    Assert-Check (((Get-FleetHubNames $config.nodes) -join ',') -eq 'hub') 'hub: the node marked fallback'
    $heavyLater = ConvertFrom-Json -InputObject '[{"name":"mid","tier":"standard"},{"name":"big","tier":"heavy"}]'
    Assert-Check (((Get-FleetHubNames $heavyLater) -join ',') -eq 'big') 'hub: without a mark, the first heavy text node'
    $noHeavy = ConvertFrom-Json -InputObject '[{"name":"mid","tier":"standard"},{"name":"small","tier":"light"}]'
    Assert-Check (((Get-FleetHubNames $noHeavy) -join ',') -eq 'mid') 'hub: without a mark or a heavy node, the first text node'
    $visionFallback = ConvertFrom-Json -InputObject '[{"name":"eyes","vision":true,"fallback":true},{"name":"mid","tier":"standard"}]'
    Assert-Check (((Get-FleetHubNames $visionFallback) -join ',') -eq 'mid') 'hub: a vision node is never the hub'
    Assert-Check (Test-CandidateIsHub $config.nodes 'hub') 'hub: the hub node is the hub'
    Assert-Check (-not (Test-CandidateIsHub $config.nodes 'worker1')) 'hub: a worker on the network is not the hub'
    $localWorker = ConvertFrom-Json -InputObject '[{"name":"hub","fallback":true,"url":"http://192.0.2.10:11434/v1"},{"name":"local","url":"http://localhost:11435/v1","tier":"light"}]'
    Assert-Check (Test-CandidateIsHub $localWorker 'local') 'hub: a node on this machine is treated as the hub'

    # The live backends: the default address, and the one the installer recorded when there is one.
    Assert-Check (((Get-LiveBackendUrls $null) -join ' ') -eq 'http://localhost:8000') 'live backend: the default address without an install record'
    $installRoot = Join-Path $scratch 'install'
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installRoot 'fleet-install.json'), '{ "backendUrl": "http://192.0.2.10:8100/" }', $utf8)
    $recorded = Get-LiveBackendUrls $installRoot
    Assert-Check (($recorded -join ' ') -eq 'http://localhost:8000 http://192.0.2.10:8100') 'live backend: the recorded address is checked as well'
    Assert-Check (((Get-LivePorts $recorded) -join ',') -eq '8000,8100') 'live backend: the ports of those addresses are reserved'
    [IO.File]::WriteAllText((Join-Path $installRoot 'fleet-install.json'), '{ not json', $utf8)
    Assert-Check (((Get-LiveBackendUrls $installRoot) -join ' ') -eq 'http://localhost:8000') 'live backend: an unreadable install record is ignored'

    # The live plan check: only a JSON list of plans with statuses can show that the fleet is idle.
    $idle = Get-PlanActivity '[{"status":"done"},{"status":"awaiting-approval"},{"status":"blocked"}]'
    Assert-Check ($idle.Readable -and $idle.Active -eq 0) 'live plans: finished, waiting and blocked plans are not active'
    $busy = Get-PlanActivity '[{"status":"done"},{"status":"running"},{"status":"approved"}]'
    Assert-Check ($busy.Readable -and $busy.Active -eq 2) 'live plans: running and approved plans are active'
    $empty = Get-PlanActivity '[]'
    Assert-Check ($empty.Readable -and $empty.Active -eq 0) 'live plans: an empty list is idle'
    $spacedEmpty = Get-PlanActivity "[ ]`n"
    Assert-Check ($spacedEmpty.Readable -and $spacedEmpty.Active -eq 0) 'live plans: an empty list with spaces is idle'
    $single = Get-PlanActivity '[{"status":"running"}]'
    Assert-Check ($single.Readable -and $single.Active -eq 1) 'live plans: a single running plan is active'
    Assert-Check (-not (Get-PlanActivity '<html><body>Not the fleet</body></html>').Readable) 'live plans: an HTML page is not a plan list'
    Assert-Check (-not (Get-PlanActivity '{"error":"not a list"}').Readable) 'live plans: an object is not a plan list'
    Assert-Check (-not (Get-PlanActivity '[{"name":"no status"}]').Readable) 'live plans: a plan without a status is not readable'
    Assert-Check (-not (Get-PlanActivity '[ not json').Readable) 'live plans: broken JSON is not readable'
    Assert-Check (-not (Get-PlanActivity '').Readable) 'live plans: an empty answer is not readable'

    # Live backends: no answer, an unreadable answer and running or approved plans all block the candidate.
    $closedPort = Get-FreeTestPort
    $unanswered = Get-LiveActivity "http://127.0.0.1:$closedPort"
    Assert-Check (-not $unanswered.Answered -and -not $unanswered.Readable) 'live backend: a port that nothing answers on gives no answer'
    Assert-Check ($null -ne (Get-LiveProblem "http://127.0.0.1:$closedPort" $unanswered)) 'live backend: no answer counts as busy, not idle'
    Assert-Check ($null -ne (Get-LiveProblem 'http://localhost:8000' ([pscustomobject]@{ Answered = $true; Readable = $false; Active = 0; Detail = 'not a list' }))) 'live backend: an answer that is not a plan list counts as busy'
    Assert-Check ($null -ne (Get-LiveProblem 'http://localhost:8000' ([pscustomobject]@{ Answered = $true; Readable = $true; Active = 2; Detail = '' }))) 'live backend: running or approved plans count as busy'
    Assert-Check ($null -eq (Get-LiveProblem 'http://localhost:8000' ([pscustomobject]@{ Answered = $true; Readable = $true; Active = 0; Detail = '' }))) 'live backend: a readable answer with no active plan lets the candidate run'

    # Ports: the node addresses and the recorded web UI address are reserved, not only the defaults.
    $portNodes = ConvertFrom-Json -InputObject '[{"name":"local","url":"http://localhost:11500/v1"},{"name":"far","url":"http://192.0.2.31:11434/v1"}]'
    $reservedWithWeb = Get-CandidateReservedPorts $portNodes 'http://localhost:3033' @(3000, 11434)
    Assert-Check ($reservedWithWeb -contains 11500) 'ports: the port of a node address is reserved while nothing listens on it'
    Assert-Check ($reservedWithWeb -contains 3033 -and $reservedWithWeb -contains 3000 -and $reservedWithWeb -contains 11434) 'ports: the recorded web UI port and the default ports are reserved'
    Assert-Check (-not ((Get-CandidateReservedPorts $portNodes '' @(3000, 11434)) -contains 3033)) 'ports: without a recorded web UI address no other port is reserved'
    $webRoot = Join-Path $scratch 'web-install'
    New-Item -ItemType Directory -Path $webRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $webRoot 'fleet-install.json'), '{ "backendUrl": "http://localhost:8000", "webUrl": "http://localhost:3033" }', $utf8)
    Assert-Check ((Get-RecordedWebUrl $webRoot) -eq 'http://localhost:3033') 'install record: the recorded web UI address is read'
    [IO.File]::WriteAllText((Join-Path $webRoot 'fleet-install.json'), '{ not json', $utf8)
    Assert-Check ((Get-RecordedWebUrl $webRoot) -eq '') 'install record: an unreadable record gives no web UI address'
    Assert-Check ((Get-RecordedWebUrl (Join-Path $scratch 'no-such-install')) -eq '') 'install record: a missing record gives no web UI address'

    # A node that answers on another node's Ollama address is named, because loading the candidate there can unload its model.
    Assert-Check (((Get-SharedOllamaNodes $config.nodes 'worker1') -join ',') -eq 'eyes') 'shared address: the node that answers on the same Ollama address is named'
    Assert-Check (@(Get-SharedOllamaNodes $config.nodes 'worker2').Count -eq 0) 'shared address: a node with its own address shares with nobody'

    # Starting the backend: the DLL path must reach the process as one argument, also in a folder with a space in its path.
    $argcScript = Join-Path $scratch 'argc.ps1'
    [IO.File]::WriteAllText($argcScript, 'Write-Output ("count=" + $args.Count)', $utf8)
    $startProcess = @($startAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Start-Process' }, $true))[0]
    $startElements = @($startProcess.CommandElements)
    $argumentIndex = 0
    for ($i = 0; $i -lt $startElements.Count; $i++) {
        if ($startElements[$i] -is [Management.Automation.Language.CommandParameterAst] -and $startElements[$i].ParameterName -eq 'ArgumentList') { $argumentIndex = $i + 1 }
    }
    Assert-Check ($argumentIndex -gt 0) 'backend start: the backend is started with an argument list'
    $bin = Join-Path (Join-Path $scratch 'with space') 'bin'
    $dllArgument = Invoke-Expression $startElements[$argumentIndex].Extent.Text
    $argcOut = Join-Path $scratch 'argc-out.txt'
    Start-Process -FilePath $engine -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $argcScript + '" ' + $dllArgument) -RedirectStandardOutput $argcOut -Wait -WindowStyle Hidden
    Assert-Check ((Get-Content -LiteralPath $argcOut -Raw).Trim() -eq 'count=1') 'backend start: the DLL path reaches the process as one argument, also when its folder has a space'

    # Config files are UTF-8 without a byte-order mark: their characters must survive a read and a write under both engines.
    $purposeText = 'caf' + [char]0x00E9 + ' ' + [char]0x2014 + ' hub'
    $utf8Config = Join-Path $scratch 'utf8-purpose.json'
    [IO.File]::WriteAllText($utf8Config, ('{ "purpose": "' + $purposeText + '" }'), $utf8)
    Assert-Check ((Read-FleetConfig $utf8Config).purpose -ceq $purposeText) 'config read: UTF-8 text without a byte-order mark keeps its characters'
    $utf8Copy = Join-Path $scratch 'utf8-copy.json'
    Write-FleetConfig $utf8Copy (Read-FleetConfig $utf8Config)
    Assert-Check (([IO.File]::ReadAllText($utf8Copy, [Text.Encoding]::UTF8) | ConvertFrom-Json).purpose -ceq $purposeText) 'config copy: the characters survive a write'

    # Results and copied logs: addresses and folder paths of this machine are taken out.
    $leak = Hide-LocalDetail 'No connection (192.0.2.21:11434) at http://192.0.2.22:11434/v1 under Z:\example-profile\AppData and \\share-host\folder'
    Assert-Check ($leak -notmatch '192\.0\.2\.2[12]' -and $leak -notmatch 'example-profile' -and $leak -notmatch 'share-host' -and $leak -notmatch 'http://') 'hidden detail: addresses, web addresses and folder paths are taken out'
    Assert-Check ((Hide-LocalDetail 'Log in Z:\example-temp\candidate-1\work' @('Z:\example-temp\candidate-1')) -eq 'Log in <hidden>\work') 'hidden detail: an extra folder given by the caller is taken out'
    Assert-Check ((Hide-LocalDetail 'Step 3 needs a retry.') -eq 'Step 3 needs a retry.') 'hidden detail: ordinary text is kept as it is'
    $benchText = [IO.File]::ReadAllText($benchScript)
    Assert-Check ($benchText -match [regex]::Escape('FleetCommon.ps1')) 'results file: the bench script loads the shared helpers'
    $addContent = @($benchAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Content' }, $true))
    $unencoded = @($addContent | Where-Object { @($_.CommandElements | Where-Object { $_ -is [Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'Encoding' }).Count -eq 0 })
    Assert-Check ($addContent.Count -gt 0 -and $unencoded.Count -eq 0) 'results file: every line is written with an explicit encoding, not the system code page'
    $hiddenInBench = @($benchAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Hide-LocalDetail' }, $true))
    Assert-Check ($hiddenInBench.Count -ge 3) 'results file: the blocked reason, the error and the park causes pass through the hiding function'
    $hiddenInCandidate = @($wrapperAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Hide-LocalDetail' }, $true))
    Assert-Check ($hiddenInCandidate.Count -ge 1) 'backend logs: the copy in the results folder passes through the hiding function'

    # Refusals: pure checks on synthetic nodes; nothing is contacted.
    function Get-Refusal($Nodes, [string]$NodeName, [int]$Port = 8010, [bool]$InUse = $false, [bool]$AllowHub = $false) {
        return Get-CandidateRefusal -Nodes $Nodes -NodeName $NodeName -Port $Port -LivePorts $live -ReservedPorts $reserved -PortInUse $InUse -AllowHub $AllowHub
    }
    Assert-Check ($null -eq (Get-Refusal $config.nodes 'worker1')) 'a text worker on the network is accepted'
    Assert-Check ((Get-Refusal $config.nodes 'nobody') -match "no node named 'nobody'") 'refused: a node that is not in the configuration'
    Assert-Check ((Get-Refusal $config.nodes 'Worker1') -match "no node named 'Worker1'") 'refused: node names must match exactly'
    Assert-Check ((Get-Refusal $config.nodes 'hub') -match "the hub's machine") 'refused: the hub without -AllowHub'
    Assert-Check ($null -eq (Get-Refusal $config.nodes 'hub' -AllowHub $true)) 'accepted: the hub with -AllowHub'
    Assert-Check ((Get-Refusal $config.nodes 'remote') -match 'openai API') 'refused: a node that uses the openai API'
    Assert-Check ((Get-Refusal $config.nodes 'eyes') -match 'vision node') 'refused: a vision node'
    Assert-Check ((Get-Refusal $noFallback.nodes 'worker1') -match 'marks no node as fallback') 'refused: no node marked fallback'
    $twice = ConvertFrom-Json -InputObject '[{"name":"worker1","tier":"standard"},{"name":"worker1","tier":"light"}]'
    Assert-Check ((Get-Refusal $twice 'worker1') -match 'More than one node') 'refused: two nodes with the same name'
    Assert-Check ((Get-Refusal $config.nodes 'worker1' -Port 8000) -match 'live backend') 'refused: the port of a live backend'
    Assert-Check ((Get-Refusal $config.nodes 'worker1' -Port 3000) -match 'web UI or by Ollama') 'refused: the web UI port'
    Assert-Check ((Get-Refusal $config.nodes 'worker1' -Port 11434) -match 'web UI or by Ollama') 'refused: the Ollama port'
    Assert-Check ((Get-Refusal $config.nodes 'worker1' -InUse $true) -match 'already listens') 'refused: a port something already listens on'

    # The copy: the named node's model changes and nothing else.
    $candidate = New-CandidateConfig $config 'worker1' 'model-c:1b'
    Assert-Check ((@($candidate.nodes | Where-Object { $_.name -eq 'worker1' })[0].model) -eq 'model-c:1b') 'copy: the named node takes the new model'
    Assert-Check ((@($config.nodes | Where-Object { $_.name -eq 'worker1' })[0].model) -eq 'worker-model-a:7b') 'copy: the original object keeps its model'
    $othersBefore = @($config.nodes | Where-Object { $_.name -ne 'worker1' }) | ConvertTo-Json -Depth 12
    $othersAfter = @($candidate.nodes | Where-Object { $_.name -ne 'worker1' }) | ConvertTo-Json -Depth 12
    Assert-Check ($othersBefore -ceq $othersAfter) 'copy: the other nodes are unchanged'
    Assert-Check (Test-CandidateOnlyChangesModel $config $candidate 'worker1' 'worker-model-a:7b') 'copy: only that node model differs'
    $touchedUrl = New-CandidateConfig $config 'worker1' 'model-c:1b'
    @($touchedUrl.nodes | Where-Object { $_.name -eq 'worker2' })[0].url = 'http://192.0.2.99:11434/v1'
    Assert-Check (-not (Test-CandidateOnlyChangesModel $config $touchedUrl 'worker1' 'worker-model-a:7b')) 'copy: a change to another node is caught'
    $touchedTop = New-CandidateConfig $config 'worker1' 'model-c:1b'
    $touchedTop.triageModel = 'other-triage:1b'
    Assert-Check (-not (Test-CandidateOnlyChangesModel $config $touchedTop 'worker1' 'worker-model-a:7b')) 'copy: a change to a top-level setting is caught'

    # The candidate script itself, dry run and refusals only, as child processes.
    $outDir = Join-Path $scratch 'out-not-created'
    $freePort = Get-FreeTestPort
    $tempBefore = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'agent-fleet-candidate-*' -ErrorAction SilentlyContinue).Count
    $dry = Invoke-CandidateScript @('-Node', 'worker1', '-Model', 'candidate-model:2b', '-Config', $configPath, '-OutDir', $outDir, '-Port', "$freePort", '-WhatIf')
    Assert-Check ($dry.ExitCode -eq 0) 'dry run exits with 0'
    Assert-Check ($dry.Text -match "model 'worker-model-a:7b' -> 'candidate-model:2b'") 'dry run prints the one configuration change'
    Assert-Check ($dry.Text -match 'Dry run complete') 'dry run says it started nothing'
    Assert-Check ($dry.Text -match 'same Ollama address as eyes') 'dry run warns that the worker shares its Ollama address with another node'
    Assert-Check ($dry.Text -notmatch '== Starting' -and $dry.Text -notmatch '== Running') 'dry run has no start or run step'
    Assert-Check (-not (Test-Path -LiteralPath $outDir)) 'dry run creates no output folder'
    $tempAfter = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'agent-fleet-candidate-*' -ErrorAction SilentlyContinue).Count
    Assert-Check ($tempAfter -eq $tempBefore) 'dry run creates no temporary copy'
    Assert-Check ((Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -eq $configHashBefore) 'dry run leaves the configuration byte-identical'

    $dryAlias = Invoke-CandidateScript @('-Node', 'worker1', '-Model', 'candidate-model:2b', '-Config', $configPath, '-OutDir', $outDir, '-Port', "$freePort", '-DryRun')
    Assert-Check ($dryAlias.ExitCode -eq 0) '-DryRun is accepted as -WhatIf'

    $hubDry = Invoke-CandidateScript @('-Node', 'hub', '-Model', 'candidate-model:2b', '-Config', $configPath, '-OutDir', $outDir, '-Port', "$freePort", '-WhatIf', '-AllowHub')
    Assert-Check ($hubDry.ExitCode -eq 0 -and $hubDry.Text -match "is the hub's machine \(-AllowHub\)") 'with -AllowHub the hub is accepted and the run is said to use it'
    Assert-Check ($hubDry.Text -notmatch 'WorkerOnly' -and $hubDry.Text -notmatch 'kept out') 'with -AllowHub the run is not limited to workers'

    $cases = @(
        @{ Name = 'an unknown node'; Arguments = @('-Node', 'nobody'); Expect = "There is no node named 'nobody'" },
        @{ Name = 'the hub without -AllowHub'; Arguments = @('-Node', 'hub'); Expect = "the hub's machine" },
        @{ Name = 'an openai node'; Arguments = @('-Node', 'remote'); Expect = 'openai API' },
        @{ Name = 'a vision node'; Arguments = @('-Node', 'eyes'); Expect = 'vision node' },
        @{ Name = 'the live backend port'; Arguments = @('-Node', 'worker1', '-Port', '8000'); Expect = 'port of a live backend' },
        @{ Name = 'the web UI port'; Arguments = @('-Node', 'worker1', '-Port', '3000'); Expect = 'web UI or by Ollama' },
        @{ Name = 'the Ollama port'; Arguments = @('-Node', 'worker1', '-Port', '11434'); Expect = 'web UI or by Ollama' },
        @{ Name = 'a configuration without a fallback mark'; Arguments = @('-Node', 'worker1', '-Config', $noFallbackPath); Expect = 'marks no node as fallback' },
        @{ Name = 'a configuration file that does not exist'; Arguments = @('-Node', 'worker1', '-Config', (Join-Path $scratch 'missing.json')); Expect = 'was not found' }
    )
    foreach ($case in $cases) {
        $caseArgs = @($case.Arguments)
        if ($caseArgs -notcontains '-Config') { $caseArgs += @('-Config', $configPath) }
        if ($caseArgs -notcontains '-Port') { $caseArgs += @('-Port', "$freePort") }
        $caseArgs += @('-Model', 'candidate-model:2b', '-OutDir', $outDir, '-WhatIf')
        $result = Invoke-CandidateScript $caseArgs
        Assert-Check ($result.ExitCode -eq 2 -and $result.Text -match 'Refused:' -and $result.Text -match [regex]::Escape($case.Expect)) "refused with exit 2: $($case.Name)"
    }

    $resultsFolder = Join-Path $scratch 'already-has-results'
    New-Item -ItemType Directory -Path $resultsFolder -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $resultsFolder 'results.jsonl'), '{}' + "`n", $utf8)
    $held = Invoke-CandidateScript @('-Node', 'worker1', '-Model', 'candidate-model:2b', '-Config', $configPath, '-OutDir', $resultsFolder, '-Port', "$freePort", '-WhatIf')
    Assert-Check ($held.ExitCode -eq 2 -and $held.Text -match 'already holds results') 'refused with exit 2: an output folder that already holds results'

    $missingNode = Invoke-CandidateScript @('-Model', 'candidate-model:2b', '-Config', $configPath, '-WhatIf')
    Assert-Check ($missingNode.ExitCode -ne 0) 'without -Node the script does not run'

    $finalHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    Assert-Check ($finalHash -eq $configHashBefore) 'the configuration is byte-identical after every check'
}
finally {
    $env:AGENT_FLEET_INSTALL_ROOT = $previousInstallRoot
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        if ($resolved -ne $scratch -or -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the check folder.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Host 'PASS: every candidate-wrapper check passed.' -ForegroundColor Green
