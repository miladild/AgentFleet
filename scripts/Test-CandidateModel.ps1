<#
.SYNOPSIS
    Tries a candidate model on one worker: runs the benchmark tasks there on a throwaway backend and writes the results.

.DESCRIPTION
    Copies the fleet configuration to a temporary folder with the model of one worker replaced, starts a second backend
    on -Port with that copy (bench\Start-BenchBackend.ps1), runs the benchmark tasks on that worker only (bench\Invoke-Bench.ps1
    with -WorkerOnly, the hub's model kept out, no second opinion and no automatic retries), and writes the results to
    -OutDir. The backend is stopped and the copy deleted at the end, also when the run fails or is interrupted.

    The worker is a machine of the live fleet, so the command refuses while a plan on the live backend is running or
    approved (-Force overrides that). It never writes the live configuration and never uses the live data folder or port.
    The fit probe in the Models tab does not choose models: only the task results say whether a candidate works.

    Import the results afterwards: results.jsonl from the output folder, in the Models tab or through
    POST /api/lab/results/import.

.PARAMETER Node
    The worker's name as written in the fleet configuration.

.PARAMETER Model
    The Ollama model to try on that worker. It must already be pulled on that machine; this command does not pull it.

.PARAMETER Config
    The fleet.config.json to copy. Default: the one bench\Start-BenchBackend.ps1 finds.

.PARAMETER Tasks
    The benchmark tasks to run. Default: the tasks bench\Invoke-Bench.ps1 runs by default.

.PARAMETER Repeat
    How many times each task runs. Default 1.

.PARAMETER Port
    The port of the throwaway backend. Default 8010. It must not be a live backend's port, the web UI's or Ollama's, and
    nothing may already listen on it, because the benchmark backend stops whatever listens on its port.

.PARAMETER OutDir
    Where the results go. Default: bench\results\candidate-<node>-<model>-<time>. The folder must not already hold results.

.PARAMETER AllowHub
    Allows the candidate on the hub's own machine. The run then uses the hub's model, which loads the hub's graphics
    memory, and the tasks are no longer limited to workers.

.PARAMETER Force
    Runs even while the live backend has a plan running or approved. The candidate then shares that machine with the plan.

.PARAMETER WhatIf
    Prints every step and the configuration change, and starts nothing. Also accepted as -DryRun.

.EXAMPLE
    .\scripts\Test-CandidateModel.ps1 -Node worker1 -Model llama3.2:3b -WhatIf
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Node,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Model,
    [string]$Config,
    [string[]]$Tasks,
    [ValidateRange(1, 100)][int]$Repeat = 1,
    [ValidateRange(1, 65535)][int]$Port = 8010,
    [string]$OutDir,
    [switch]$AllowHub,
    [switch]$Force,
    [Alias('DryRun')][switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"

# The web UI and Ollama answer on these ports on every machine. The benchmark backend stops whatever listens on its
# port, so a candidate must never be given one of them.
$reservedPorts = @(3000, 11434)
$startScript = Join-Path $script:RepoRoot 'bench\Start-BenchBackend.ps1'
$benchScript = Join-Path $script:RepoRoot 'bench\Invoke-Bench.ps1'

function Stop-Refused([string]$Message) {
    Write-Host "Refused: $Message" -ForegroundColor Red
    exit 2
}

function Test-IsTrue($Value) { return ($Value -eq $true) }

# Keeps letters, digits, dots, underscores and dashes, so a model name such as "name:tag" or "org/name" cannot add a
# folder level or a character that Windows refuses.
function Get-CandidateFolderPart([string]$Text) {
    $part = ($Text -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.')
    if ($part.Length -gt 60) { $part = $part.Substring(0, 60).TrimEnd('-', '.') }
    if (-not $part) { return 'model' }
    return $part
}

function Get-CandidateOutDirName([string]$NodeName, [string]$ModelName, [string]$Stamp) {
    return 'candidate-{0}-{1}-{2}' -f (Get-CandidateFolderPart $NodeName), (Get-CandidateFolderPart $ModelName), $Stamp
}

# The hub is the node the backend routes its fallback to: the one marked fallback, else the first heavy text node, else
# the first text node. A vision node never is.
function Get-FleetHubNames($Nodes) {
    $text = @($Nodes | Where-Object { -not (Test-IsTrue $_.vision) })
    $marked = @($text | Where-Object { Test-IsTrue $_.fallback })
    if ($marked.Count) { return @($marked | ForEach-Object { $_.name }) }
    $heavy = @($text | Where-Object { "$($_.tier)".Trim() -ieq 'heavy' })
    if ($heavy.Count) { return @($heavy[0].name) }
    if ($text.Count) { return @($text[0].name) }
    return @()
}

# The hub node itself, or any node whose address is this machine: its Ollama is the hub's own.
function Test-CandidateIsHub($Nodes, [string]$NodeName) {
    $named = @($Nodes | Where-Object { $_.name -ceq $NodeName })
    if ($named.Count -eq 0) { return $false }
    if ((Get-FleetHubNames $Nodes) -contains $NodeName) { return $true }
    $uri = $null
    return ([Uri]::TryCreate("$($named[0].url)", [UriKind]::Absolute, [ref]$uri) -and $uri.IsLoopback)
}

# The default address of a live backend, plus the address the installer recorded for it when there is one.
function Get-LiveBackendUrls([string]$InstallRoot) {
    $urls = @('http://localhost:8000')
    if ($InstallRoot) {
        $record = Join-Path $InstallRoot 'fleet-install.json'
        if (Test-Path -LiteralPath $record) {
            try { $recorded = "$((Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).backendUrl)" } catch { $recorded = '' }
            if ($recorded) { $urls += $recorded.TrimEnd('/') }
        }
    }
    return @($urls | Select-Object -Unique)
}

function Get-LivePorts([string[]]$Urls) {
    $ports = @()
    foreach ($url in $Urls) {
        $uri = $null
        if ([Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)) { $ports += $uri.Port }
    }
    return @($ports)
}

function Test-PortListening([int]$Port) {
    return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

# The reason this candidate must not run, or nothing. The checks come in the order a user can act on them.
function Get-CandidateRefusal {
    param($Nodes, [string]$NodeName, [int]$Port, [int[]]$LivePorts, [int[]]$ReservedPorts, [bool]$PortInUse, [bool]$AllowHub)
    $named = @($Nodes | Where-Object { $_.name -ceq $NodeName })
    if ($named.Count -eq 0) { return "There is no node named '$NodeName' in the configuration." }
    if ($named.Count -gt 1) { return "More than one node is named '$NodeName' in the configuration." }
    $node = $named[0]
    if (Test-IsTrue $node.vision) { return "'$NodeName' is a vision node. The candidate test needs a text model." }
    if ("$($node.api)".Trim() -ieq 'openai') { return "'$NodeName' uses the openai API. The candidate test covers Ollama machines only." }
    $isHub = Test-CandidateIsHub $Nodes $NodeName
    if ($isHub -and -not $AllowHub) { return "'$NodeName' is the hub's machine. Pass -AllowHub to try a model there anyway; the hub's graphics memory is then loaded." }
    $markedFallback = @($Nodes | Where-Object { -not (Test-IsTrue $_.vision) -and (Test-IsTrue $_.fallback) })
    if (-not $markedFallback.Count -and -not ($isHub -and $AllowHub)) {
        return 'The configuration marks no node as fallback, so the hub''s model cannot be kept out of the run. Mark the hub node with "fallback": true.'
    }
    if ($LivePorts -contains $Port) { return "Port $Port is the port of a live backend." }
    if ($ReservedPorts -contains $Port) { return "Port $Port is used by the web UI or by Ollama." }
    if ($PortInUse) { return "Something already listens on port $Port. The benchmark backend stops whatever listens on its port, so choose another -Port." }
    return $null
}

# The copy of the configuration with one node's model replaced; the rest is the same JSON.
function New-CandidateConfig($Config, [string]$NodeName, [string]$Model) {
    $copy = $Config | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    foreach ($node in @($copy.nodes | Where-Object { $_.name -ceq $NodeName })) { $node.model = $Model }
    return $copy
}

# True when the candidate differs from the original in that node's model and nowhere else.
function Test-CandidateOnlyChangesModel($Original, $Candidate, [string]$NodeName, [string]$OldModel) {
    $restored = $Candidate | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    foreach ($node in @($restored.nodes | Where-Object { $_.name -ceq $NodeName })) { $node.model = $OldModel }
    return (($Original | ConvertTo-Json -Depth 12) -ceq ($restored | ConvertTo-Json -Depth 12))
}

# Counts the plans that are running or approved in the text /api/plans answers with. Anything that is not a JSON list of
# plans cannot show that the fleet is idle, so it is reported as unreadable.
function Get-PlanActivity([string]$Json) {
    $unreadable = [pscustomobject]@{ Readable = $false; Active = 0 }
    $text = "$Json".Trim()
    if (-not $text.StartsWith('[')) { return $unreadable }
    $items = @()
    if ($text -ne '[]') {
        # Windows PowerShell 5.1 emits the whole parsed array as one object, so it is assigned first and then enumerated.
        try { $parsed = ConvertFrom-Json -InputObject $text } catch { return $unreadable }
        $items = @($parsed | Where-Object { $null -ne $_ })
    }
    if (@($items | Where-Object { $_.PSObject.Properties.Name -notcontains 'status' }).Count) { return $unreadable }
    $active = @($items | Where-Object { "$($_.status)" -in 'running', 'approved' })
    return [pscustomobject]@{ Readable = $true; Active = $active.Count }
}

# What the live backend at $Url is doing. Answered is false when nothing answers there at all; an HTTP error answers
# but cannot show that the fleet is idle.
function Get-LiveActivity([string]$Url) {
    try { $response = Invoke-WebRequest -Uri "$Url/api/plans" -UseBasicParsing -TimeoutSec 10 }
    catch {
        return [pscustomobject]@{ Answered = [bool]$_.Exception.Response; Readable = $false; Active = 0; Detail = $_.Exception.Message }
    }
    $activity = Get-PlanActivity "$($response.Content)"
    $detail = if ($activity.Readable) { '' } else { 'the answer is not a list of plans' }
    return [pscustomobject]@{ Answered = $true; Readable = $activity.Readable; Active = $activity.Active; Detail = $detail }
}

# The ports the candidate must never take: the base list (the web UI and Ollama on every machine), the web UI's address as
# the installer recorded it, and the port of every node's address. A node's Ollama can be stopped right now and still be
# this machine's, so the node ports are reserved whether or not something listens on them at the moment.
function Get-CandidateReservedPorts($Nodes, [string]$WebUrl, [int[]]$Base) {
    $nodeUrls = @($Nodes | ForEach-Object { "$($_.url)" })
    return @(@($Base) + @(Get-LivePorts (@($nodeUrls) + @($WebUrl))))
}

# The web UI's address as the installer recorded it, when there is one: its port is reserved as well.
function Get-RecordedWebUrl([string]$InstallRoot) {
    if (-not $InstallRoot) { return '' }
    $record = Join-Path $InstallRoot 'fleet-install.json'
    if (-not (Test-Path -LiteralPath $record)) { return '' }
    try { return "$((Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).webUrl)" } catch { return '' }
}

# The live backend's answer decides whether the candidate may run. Anything but a readable list with no plan running or
# approved counts as busy, including no answer at all: a backend that does not answer may still be running plans.
function Get-LiveProblem([string]$Url, $Activity) {
    if (-not $Activity.Readable) { return "The live backend at $Url did not answer the plan check with a list of plans ($($Activity.Detail)), so it cannot be shown to be idle." }
    if ($Activity.Active -gt 0) { return "The live backend at $Url has $($Activity.Active) plan(s) running or approved, and the candidate would share its machines." }
    return $null
}

# The other nodes that answer on this node's Ollama address. Loading the candidate there can unload their models.
function Get-SharedOllamaNodes($Nodes, [string]$NodeName) {
    $named = @($Nodes | Where-Object { $_.name -ceq $NodeName })
    if ($named.Count -eq 0 -or -not "$($named[0].url)") { return @() }
    $base = Get-OllamaBase "$($named[0].url)"
    return @($Nodes | Where-Object { $_.name -cne $NodeName -and "$($_.url)" -and (Get-OllamaBase "$($_.url)") -ieq $base } | ForEach-Object { $_.name })
}

if ($Config -and -not (Test-Path -LiteralPath $Config)) { Stop-Refused "The configuration file was not found: $Config" }
$source = Find-FleetConfig $Config $null
if (-not $source) { Stop-Refused 'No fleet.config.json found. Pass -Config <path>.' }
try { $original = Read-FleetConfig $source } catch { Stop-Refused "The configuration is not valid JSON: $source" }

if (-not $OutDir) { $OutDir = Join-Path $script:RepoRoot ('bench\results\' + (Get-CandidateOutDirName $Node $Model (Get-Date -Format 'yyyyMMdd-HHmmss'))) }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)

$installRoot = Resolve-InstallRoot $null
$liveUrls = Get-LiveBackendUrls $installRoot
$refusal = Get-CandidateRefusal -Nodes $original.nodes -NodeName $Node -Port $Port -LivePorts (Get-LivePorts $liveUrls) `
    -ReservedPorts (Get-CandidateReservedPorts $original.nodes (Get-RecordedWebUrl $installRoot) $reservedPorts) `
    -PortInUse (Test-PortListening $Port) -AllowHub:$AllowHub
if ($refusal) { Stop-Refused $refusal }
if (Test-Path -LiteralPath (Join-Path $OutDir 'results.jsonl')) {
    Stop-Refused "$OutDir already holds results. Choose another -OutDir, so two candidates never share a results file."
}

$namedNode = @($original.nodes | Where-Object { $_.name -ceq $Node })[0]
$oldModel = "$($namedNode.model)"
$isHubRun = Test-CandidateIsHub $original.nodes $Node
$workerOnly = -not ($AllowHub -and $isHubRun)
$candidate = New-CandidateConfig $original $Node $Model
if (-not (Test-CandidateOnlyChangesModel $original $candidate $Node $oldModel)) {
    Stop-Refused 'The copy of the configuration would change more than the model. Nothing was started.'
}

$mode = if ($WhatIf) { 'dry run: nothing is started, contacted or written' } else { 'live run' }
Write-Step "Candidate model on $Node ($mode)"
Write-Ok "Configuration read from $source"
if ($isHubRun) { Write-Warn "$Node is the hub's machine (-AllowHub): the run uses the hub's model and is not limited to workers." }
else { Write-Ok "$Node is a text worker on the Ollama API, not the hub's machine." }
$sharedNodes = @(Get-SharedOllamaNodes $original.nodes $Node)
if ($sharedNodes.Count) { Write-Warn "$Node answers on the same Ollama address as $($sharedNodes -join ', '). Loading the candidate there can unload their model while the run goes on." }
Write-Ok "Port $Port is free and is not a live backend's, the web UI's or Ollama's port."
Write-Info ("Configuration change (node and model only): {0}: model '{1}' -> '{2}'" -f $Node, $oldModel, $Model)
Write-Info "The model must already be pulled on $Node. This command does not pull it."
Write-Info "Results go to $OutDir"

$hubNote = if ($workerOnly) { ', with the hub''s model kept out' } else { '' }
$workerNote = if ($workerOnly) { ', WorkerOnly' } else { '' }
$taskNote = if ($PSBoundParameters.ContainsKey('Tasks')) { "the tasks $($Tasks -join ', ')" } else { 'the default tasks' }
if ($WhatIf) {
    Write-Info "Would ask the live backend at $($liveUrls -join ', ') for its plans, and refuse while any is running or approved."
    Write-Info 'Would copy the configuration to a temporary folder with that one change; the original is not written.'
    Write-Info "Would start the throwaway backend on port $Port with that copy$hubNote."
    Write-Info "Would wait up to 180 s for http://localhost:$Port/health."
    Write-Info "Would run $taskNote against http://localhost:$Port, with Repeat $Repeat, Review off, AutoRetries 0$workerNote."
    Write-Info 'Would stop the throwaway backend and delete the temporary copy.'
    Write-Ok 'Dry run complete. Nothing was started, contacted or written.'
    exit 0
}

foreach ($url in $liveUrls) {
    $activity = Get-LiveActivity $url
    $problem = Get-LiveProblem $url $activity
    if (-not $problem) { Write-Ok "The live backend at $url has no plan running or approved."; continue }
    $advice = if ($activity.Readable) { 'Wait for them to finish, or pass -Force to run anyway.' } else { 'Pass -Force to run anyway.' }
    if (-not $Force) { Stop-Refused "$problem $advice" }
    Write-Warn "$problem Continuing because of -Force."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('agent-fleet-candidate-' + [Guid]::NewGuid().ToString('N'))
$candidatePath = Join-Path $tempRoot 'candidate-fleet.config.json'
$workRoot = Join-Path $tempRoot 'work'
$pidFile = Join-Path $workRoot 'backend.pid'
$hashBefore = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$exitCode = 0

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Write-FleetConfig $candidatePath $candidate
    Write-Ok 'Candidate configuration written to a temporary folder.'

    $startArgs = @{ Port = $Port; Config = $candidatePath; WorkRoot = $workRoot; HealthTimeoutSeconds = 180 }
    if ($workerOnly) { $startArgs['NoHubModel'] = $true }
    Write-Step "Starting the throwaway backend on port $Port"
    & $startScript @startArgs

    $benchArgs = @{ BaseUrl = "http://localhost:$Port"; OutDir = $OutDir; Repeat = $Repeat; Review = 'off'; AutoRetries = 0 }
    if ($workerOnly) { $benchArgs['WorkerOnly'] = $true }
    if ($PSBoundParameters.ContainsKey('Tasks')) { $benchArgs['Tasks'] = $Tasks }
    Write-Step 'Running the benchmark tasks'
    & $benchScript @benchArgs
}
catch {
    Write-Bad "The run did not finish: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    $backendPid = 0
    if (Test-Path -LiteralPath $pidFile) {
        $null = [int]::TryParse((Get-Content -LiteralPath $pidFile -TotalCount 1), [ref]$backendPid)
        try { & $startScript -Stop -Port $Port -WorkRoot $workRoot }
        catch {
            Write-Bad "Could not stop the throwaway backend: $($_.Exception.Message). Stop it with bench\Start-BenchBackend.ps1 -Stop -Port $Port."
            $exitCode = 1
        }
        for ($i = 0; $i -lt 30 -and $backendPid -gt 0 -and (Get-Process -Id $backendPid -ErrorAction SilentlyContinue); $i++) {
            Start-Sleep -Milliseconds 500
        }
    }

    $logs = @(Get-ChildItem -LiteralPath $workRoot -Filter 'backend.*.log' -ErrorAction SilentlyContinue)
    if ($exitCode -ne 0 -and $logs.Count) {
        # The logs can name node addresses and this machine's folders. A copy that fails must not stop the cleanup below.
        $logFolder = Join-Path $OutDir 'backend-logs'
        try {
            $secrets = @($tempRoot, $env:USERPROFILE) + @($original.nodes | ForEach-Object { "$($_.url)"; try { ([Uri]"$($_.url)").Host } catch { } })
            New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
            foreach ($log in $logs) {
                [IO.File]::WriteAllText((Join-Path $logFolder $log.Name), (Hide-LocalDetail ([IO.File]::ReadAllText($log.FullName)) $secrets), (New-Object Text.UTF8Encoding($false)))
            }
            Write-Info "The backend's logs were copied to $logFolder, without its addresses or this machine's folder paths."
        }
        catch { Write-Warn "The backend's logs could not be copied: $($_.Exception.Message)" }
    }

    if ((Split-Path -Leaf $tempRoot) -like 'agent-fleet-candidate-*' -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempRoot) { Write-Warn "Could not delete $tempRoot. Delete it by hand: it holds a copy of the configuration." }

    try { $hashAfter = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash } catch { $hashAfter = '' }
    if ($hashAfter -ne $hashBefore) {
        Write-Bad "The configuration at $source changed or disappeared during the run. This command never writes it: check it before you use the fleet."
        $exitCode = 1
    }
    else { Write-Ok 'The configuration is unchanged.' }
}

if (Test-Path -LiteralPath (Join-Path $OutDir 'results.jsonl')) {
    Write-Info "Results are in $OutDir (results.jsonl; results.json and summary.md once the run has finished)."
    Write-Info 'To import them, import results.jsonl from this folder in the Models tab or through POST /api/lab/results/import.'
}
else { Write-Info "No results were written to $OutDir." }
exit $exitCode
