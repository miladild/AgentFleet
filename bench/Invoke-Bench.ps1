<#
.SYNOPSIS
    Runs the benchmark tasks as plans on a fleet backend and scores them against tests the models never see.

.DESCRIPTION
    For every task (and repeat): copies the task's starting project to a scratch folder, makes it a git repository,
    submits the task's steps as a plan to -BaseUrl, approves it with the defaults (or the options given), waits until
    the plan is done or blocked, then runs the task's HIDDEN acceptance tests on what the fleet produced. A run is judged by
    those tests, not by the plan's own status. Results are written to -OutDir as results.jsonl, results.json and summary.md.
    run.json is written first: the batch's options and the machines and models it ran on (no addresses or paths), so the
    results can be grouped by model when they are imported. It also records the settings the backend was started with
    (see Start-BenchBackend.ps1 -Setting) and the -Arm label, so that the arms of a comparison can be told apart.

    It needs a backend with ready machines. Use .\bench\Start-BenchBackend.ps1 for a separate one on port 8010.

    The summary checks the benchmark gate: no false accept (a plan that says done while the hidden tests fail), at least
    80% of runs done with the hidden tests passing, every other run ended with a reason and not stuck, and no step parked
    for an environment problem (which means the fleet, not the work, was at fault).
#>
[CmdletBinding()]
param(
    [string[]]$Tasks = @('all'),
    [int]$Repeat = 1,
    [string]$BaseUrl = 'http://localhost:8010',
    [string]$OutDir,
    [int]$TimeoutMinutes = 120,
    [ValidateRange(0, 10000)][int]$NoProgressMinutes = 0,
    [ValidateRange(0.01, 10000)][double]$MaxHours = 4,
    [ValidateSet('auto', 'off')][string]$Review = 'auto',
    [switch]$WorkerOnly,
    [int]$AutoRetries = -1,
    [ValidateSet('as-written', 'light', 'standard', 'heavy')][string]$Tier = 'as-written',
    [string]$BackendSettingsFile,
    [ValidatePattern('^[A-Za-z0-9_-]{1,40}$')][string]$Arm
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\FleetCommon.ps1"
. "$PSScriptRoot\BenchSettings.ps1"
$tasksRoot = Join-Path $PSScriptRoot 'tasks'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot ('results\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) ('agent-fleet-bench-runs\' + (Split-Path $OutDir -Leaf))

function Get-TaskIds {
    $all = Get-ChildItem -LiteralPath $tasksRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'task.json') } | ForEach-Object Name
    if ($Tasks -contains 'all') { return $all }
    foreach ($id in $Tasks) { if ($all -notcontains $id) { throw "Unknown task '$id'. Known: $($all -join ', ')" } }
    return $Tasks
}

function Get-AsleepMinutes([datetime]$Start, [datetime]$End) {
    if ($End -le $Start) { return 0.0 }
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 506, 507; StartTime = $Start; EndTime = $End } -ErrorAction Stop | Sort-Object TimeCreated)
        $pending = $null
        $minutes = 0.0
        foreach ($event in $events) {
            if ($event.Id -eq 506) { $pending = $event.TimeCreated; continue }
            if ($event.Id -eq 507 -and $null -ne $pending) {
                $from = if ($pending -gt $Start) { $pending } else { $Start }
                $to = if ($event.TimeCreated -lt $End) { $event.TimeCreated } else { $End }
                if ($to -gt $from) { $minutes += ($to - $from).TotalMinutes }
                $pending = $null
            }
        }
        if ($null -ne $pending) {
            $from = if ($pending -gt $Start) { $pending } else { $Start }
            if ($End -gt $from) { $minutes += ($End - $from).TotalMinutes }
        }
        return [Math]::Round($minutes, 1)
    }
    catch { return 0.0 }
}

function Get-BatchAwakeMinutes([datetime]$Start, [datetime]$End) {
    $asleep = Get-AsleepMinutes $Start $End
    return [Math]::Round([Math]::Max(0, ($End - $Start).TotalMinutes - $asleep), 1)
}

function Test-MaxHours([double]$AwakeMinutes, [double]$Hours) {
    return ($AwakeMinutes -gt ($Hours * 60))
}

function Invoke-Hidden([string]$Project, [string]$HiddenDir, [string]$EvidenceDir) {
    # The hidden tests are copied in only now, after the fleet is finished, and never leave this machine.
    $target = Join-Path $Project '.bench-hidden'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item (Join-Path $HiddenDir '*') $target -Recurse
    if ($EvidenceDir) { $EvidenceDir = (New-Item -ItemType Directory -Path $EvidenceDir -Force).FullName }
    $passed = 0; $failed = 0; $ok = $true; $timedOut = $false
    Push-Location $Project
    try {
        foreach ($file in Get-ChildItem $target -Filter '*.test.js') {
            $stdout = [IO.Path]::GetTempFileName()
            $stderr = [IO.Path]::GetTempFileName()
            try {
                $p = Start-Process -FilePath 'node' -ArgumentList @('--test', '--test-reporter=tap', '--test-timeout=60000', ".bench-hidden/$($file.Name)") -WorkingDirectory $Project -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
                $null = $p.Handle
                try { $p.PriorityClass = 'BelowNormal' } catch { }
                if (-not $p.WaitForExit(120000)) {
                    $timedOut = $true
                    $ok = $false
                    # taskkill reports a process that already ended on stderr; under Windows PowerShell 5.1 that would stop the run.
                    $savedPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                    try { & taskkill.exe /F /T /PID $p.Id 2>&1 | Out-Null } finally { $ErrorActionPreference = $savedPreference }
                    $p.WaitForExit()
                }
                if ($p.ExitCode -ne 0) { $ok = $false }
                $output = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
            }
            finally {
                Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
            }
            if ($output -match '(?m)^#\s*pass (\d+)') { $passed += [int]$Matches[1] }
            if ($output -match '(?m)^#\s*fail (\d+)') { $failed += [int]$Matches[1] }
            if ($EvidenceDir) { $output | Set-Content -LiteralPath (Join-Path $EvidenceDir "$($file.Name).tap.txt") -Encoding utf8 }
        }
    }
    finally { Pop-Location }
    $summary = if ($timedOut) { 'timed out after 120 s (an infinite loop?)' } else { "pass $passed, fail $failed" }
    return [pscustomobject]@{ Ok = ($ok -and $passed -gt 0); Passed = $passed; Failed = $failed; TimedOut = $timedOut; Summary = $summary }
}

# The settings the benchmark backend was started with (written by Start-BenchBackend.ps1). Returns an ordered dictionary, empty
# when the backend had none, or $null when the file is missing, unreadable, or describes another port than this batch uses:
# the batch then says so instead of guessing.
function Read-BackendSettings([string]$Path, [int]$Port) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warn 'The settings of the benchmark backend are unknown: its settings file was not found. run.json records them as null.'
        return $null
    }
    try {
        $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not ($document.PSObject.Properties.Name -contains 'port')) {
            Write-Warn 'The settings of the benchmark backend are unknown: its settings file does not say which port the backend ran on. run.json records them as null.'
            return $null
        }
        if ([int]$document.port -ne $Port) {
            Write-Warn "The settings of the benchmark backend are unknown: its settings file describes port $($document.port), and this batch uses port $Port. run.json records them as null."
            return $null
        }
        $pairs = @()
        if ($document.settings) { foreach ($property in $document.settings.PSObject.Properties) { $pairs += "$($property.Name)=$($property.Value)" } }
        return ConvertTo-BenchSettings $pairs
    }
    catch {
        Write-Warn 'The settings of the benchmark backend are unknown: its settings file cannot be read. run.json records them as null.'
        return $null
    }
}

function Get-RunNodes {
    # Names and models only: the backend's address for a machine is never written to run.json. A failed call gives an
    # empty list instead of stopping the batch.
    try {
        $config = Invoke-RestMethod -Uri "$BaseUrl/api/fleet-config" -TimeoutSec 30
        return @($config.nodes | Where-Object { $_.name } | ForEach-Object { [ordered]@{ name = [string]$_.name; model = [string]$_.model } })
    }
    catch { return @() }
}

function Write-RunRecord([datetime]$Started, [string[]]$TaskIds, $BackendSettings) {
    $record = [ordered]@{
        startedAtUtc = $Started.ToUniversalTime().ToString('o')
        arm = $(if ($Arm) { $Arm } else { $null })
        settings = $BackendSettings
        options = [ordered]@{
            tier = $Tier; repeat = $Repeat; workerOnly = [bool]$WorkerOnly; review = $Review
            autoRetries = $(if ($AutoRetries -ge 0) { $AutoRetries } else { $null })
            timeoutMinutes = $TimeoutMinutes; noProgressMinutes = $NoProgressMinutes; tasks = @($TaskIds)
        }
        nodes = @(Get-RunNodes)
    }
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'run.json') -Encoding utf8
}

function Wait-ForPlan([string]$PlanId, [datetime]$Started, [datetime]$Deadline, [int]$NoProgressMinutes, [System.Collections.IDictionary]$Result) {
    $plan = $null
    $lastProgressAt = $Started
    $lastProgressSignature = $null
    while ((Get-Date) -lt $Deadline) {
        Start-Sleep -Seconds 15
        $plan = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$PlanId" -TimeoutSec 30
        if ($plan.status -in 'done', 'blocked', 'rejected') { break }

        # The backend keeps only the latest events of a long plan, so a count can stay flat while rounds finish.
        # Progress is judged by the newest progress event instead; the date is compared as text, not parsed.
        $progressEvents = @($plan.events | Where-Object { $_.kind -in 'round-classified', 'check-passed', 'check-failed' })
        $progressSignature = $null
        if ($progressEvents.Count) {
            $newest = $progressEvents[-1]
            $progressSignature = "$($newest.atUtc)|$($newest.kind)|$($newest.stepId)|$($newest.attempt)"
        }
        if ($progressSignature -and $progressSignature -ne $lastProgressSignature) { $lastProgressAt = Get-Date }
        $lastProgressSignature = $progressSignature
        if ($NoProgressMinutes -gt 0 -and (Get-Date) -ge $lastProgressAt.AddMinutes($NoProgressMinutes)) {
            $Result.timedOut = $true
            $Result.stalled = $true
            $Result.stalledAfterMinutes = [Math]::Round(((Get-Date) - $lastProgressAt).TotalMinutes, 1)
            break
        }
    }

    if ($plan.status -notin 'done', 'blocked', 'rejected') {
        $Result.timedOut = $true
        try { $null = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$PlanId/stop" -Method Post -TimeoutSec 30 } catch { }
        $plan = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$PlanId" -TimeoutSec 30
    }
    return $plan
}

function Get-MinutesPerRound([double]$Minutes, [int]$Rounds) {
    if ($Rounds -gt 0) { return [Math]::Round($Minutes / $Rounds, 1) }
    return $null
}

function Get-MedianMinutesPerRound([object[]]$Results) {
    $values = @($Results | Where-Object { $null -ne $_.minutesPerRound } | ForEach-Object { [double]$_.minutesPerRound } | Sort-Object)
    if (-not $values.Count) { return $null }
    $middle = [int][Math]::Floor($values.Count / 2)
    if ($values.Count % 2) { return $values[$middle] }
    return [Math]::Round(($values[$middle - 1] + $values[$middle]) / 2, 1)
}

function Invoke-Run([string]$Id, [int]$Number) {
    $task = Get-Content -LiteralPath (Join-Path $tasksRoot "$Id\task.json") -Raw | ConvertFrom-Json
    $project = Join-Path $scratchRoot "$Id-$Number\project"
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    Copy-Item (Join-Path $tasksRoot "$Id\project\*") $project -Recurse -Force
    Push-Location $project
    # git prints notices such as the line-ending warning on stderr. Windows PowerShell 5.1 turns native stderr into a
    # terminating error under -ErrorAction Stop even when it is redirected, so these calls run with errors set to continue.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        git -c core.autocrlf=false init -q 2>$null
        git -c core.autocrlf=false -c core.safecrlf=false -c user.name=bench -c user.email=bench@example.invalid add -A 2>$null
        git -c core.autocrlf=false -c core.safecrlf=false -c user.name=bench -c user.email=bench@example.invalid commit -q -m 'start' 2>$null
    }
    finally { $ErrorActionPreference = $savedPreference; Pop-Location }

    $result = [ordered]@{ task = $Id; run = $Number; planId = $null; status = 'not-started'; hiddenPass = $false; hiddenSummary = ''
        falseAccept = $false; completed = $false; timedOut = $false; stalled = $false; stalledAfterMinutes = $null; hiddenTimedOut = $false; asleepMinutes = 0; minutes = 0; minutesPerRound = $null; stepsDone = 0; stepsParked = 0; rounds = 0; maxRung = 1
        autoRetries = 0; reviewFailed = 0; reviewPassed = 0; parkCauses = ''; models = ''; blockedReason = ''; error = '' }
    $started = Get-Date
    try {
        # -Tier overrides the tiers written by the task, so the same workload can be measured on another worker tier.
        $steps = $task.steps
        if ($Tier -ne 'as-written') {
            $steps = @($task.steps | ForEach-Object { $copy = $_ | ConvertTo-Json -Depth 6 | ConvertFrom-Json; $copy.tier = $Tier; $copy })
        }
        $body = @{ title = "bench $Id #$Number"; goal = $task.goal; workingDirectory = $project; steps = $steps; source = 'bench' } | ConvertTo-Json -Depth 8
        $created = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 30
        $result.planId = $created.id
        $approval = @{ review = $Review }
        if ($WorkerOnly) { $approval.recoveryScope = 'worker-only' }
        if ($AutoRetries -ge 0) { $approval.autoRetries = $AutoRetries }
        $null = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$($created.id)/approve" -Method Post -ContentType 'application/json' -Body ($approval | ConvertTo-Json) -TimeoutSec 30

        $plan = Wait-ForPlan $created.id $started $started.AddMinutes($TimeoutMinutes) $NoProgressMinutes $result

        $events = @($plan.events)
        $result.status = $plan.status
        $result.stepsDone = @($plan.steps | Where-Object status -eq 'done').Count
        $result.stepsParked = @($plan.steps | Where-Object status -eq 'parked').Count
        $result.rounds = @($events | Where-Object kind -eq 'round-classified').Count
        $rungs = @($events | Where-Object { $_.kind -eq 'rung-changed' -and $null -ne $_.rung } | ForEach-Object { [int]$_.rung })
        if ($rungs.Count) { $result.maxRung = ($rungs | Measure-Object -Maximum).Maximum }
        $result.autoRetries = @($events | Where-Object { $_.kind -eq 'step-retried' -and $_.failureClass -eq 'AutoRetry' }).Count
        $result.reviewFailed = @($events | Where-Object { $_.kind -eq 'step-review' -and $_.failureClass -eq 'Failed' }).Count
        $result.reviewPassed = @($events | Where-Object { $_.kind -eq 'step-review' -and $_.failureClass -eq 'Passed' }).Count
        $result.parkCauses = Hide-LocalDetail ((@($events | Where-Object kind -eq 'step-parked' | ForEach-Object { $_.failureSignature }) | Sort-Object -Unique) -join ',')
        $result.models = (@($events | Where-Object { $_.modelNode } | ForEach-Object { $_.modelNode }) | Sort-Object -Unique) -join ','
        $blocked = @($events | Where-Object kind -eq 'plan-blocked') | Select-Object -Last 1
        if ($blocked -and $blocked.detail) {
            $reason = Hide-LocalDetail ($blocked.detail -replace '\s+', ' ')
            $result.blockedReason = $reason.Substring(0, [Math]::Min(240, $reason.Length))
        }
    }
    catch { $result.error = Hide-LocalDetail $_.Exception.Message }
    $result.minutes = [Math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    $result.minutesPerRound = Get-MinutesPerRound $result.minutes $result.rounds

    $hidden = Invoke-Hidden $project (Join-Path $tasksRoot "$Id\hidden") (Join-Path $OutDir "$Id-$Number\hidden")
    $result.hiddenPass = $hidden.Ok
    $result.hiddenTimedOut = $hidden.TimedOut
    $result.hiddenSummary = $hidden.Summary
    $result.falseAccept = ($result.status -eq 'done' -and -not $hidden.Ok)
    $result.completed = ($result.status -eq 'done' -and $hidden.Ok)
    $result.asleepMinutes = Get-AsleepMinutes $started (Get-Date)
    return [pscustomobject]$result
}

$batchStarted = Get-Date
$stopReason = ''
$results = @()
$taskIds = @(Get-TaskIds)
if (-not $BackendSettingsFile) { $BackendSettingsFile = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench') 'backend.settings.json' }
$backendSettings = Read-BackendSettings $BackendSettingsFile ([Uri]$BaseUrl).Port
Write-RunRecord $batchStarted $taskIds $backendSettings
Write-Host ("Run caps: task timeout {0} min; no-progress stop {1}." -f $TimeoutMinutes, $(if ($NoProgressMinutes -gt 0) { "$NoProgressMinutes min" } else { 'off' }))
foreach ($id in $taskIds) {
    for ($run = 1; $run -le $Repeat; $run++) {
        $now = Get-Date
        $awakeMinutes = Get-BatchAwakeMinutes $batchStarted $now
        if (Test-MaxHours $awakeMinutes $MaxHours) { $stopReason = 'stopped: MaxHours'; break }
        Write-Host ("[{0}] {1} run {2} ..." -f (Get-Date -Format 'HH:mm:ss'), $id, $run)
        $r = Invoke-Run $id $run
        $results += $r
        $r | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $OutDir 'results.jsonl') -Encoding UTF8
        Write-Host ("        {0}: plan {1}, hidden tests {2} ({3}), {4} min, {5} rounds, {6} auto retries, {7} reviews failed" -f
            $id, $r.status, $(if ($r.hiddenPass) { 'PASS' } else { 'FAIL' }), $r.hiddenSummary, $r.minutes, $r.rounds, $r.autoRetries, $r.reviewFailed)
    }
    if ($stopReason) { break }
}

$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'results.json') -Encoding utf8

$n = $results.Count
$completed = @($results | Where-Object completed).Count
$stalledCount = @($results | Where-Object stalled).Count
$medianMinutesPerRound = Get-MedianMinutesPerRound $results
$falseAccepts = @($results | Where-Object falseAccept)
$stuck = @($results | Where-Object { $_.timedOut -or $_.hiddenTimedOut -or ($_.status -eq 'blocked' -and -not $_.blockedReason) -or $_.error })
$environment = @($results | Where-Object { $_.parkCauses -match 'environment' })
$rate = if ($n) { [Math]::Round(100.0 * $completed / $n, 0) } else { 0 }

$lines = @()
$lines += "# Benchmark results ($n runs, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
$lines += ''
$lines += "Backend $BaseUrl; steps $(if ($Tier -eq 'as-written') { 'at the tiers specified by each task' } else { "all at the $Tier tier" }); second opinion $Review; $(if ($WorkerOnly) { 'worker only' } else { 'hub rescue allowed' }); automatic retries $(if ($AutoRetries -ge 0) { $AutoRetries } else { 'default' }); task timeout $TimeoutMinutes min; no-progress stop $(if ($NoProgressMinutes -gt 0) { "$NoProgressMinutes min" } else { 'off' })."
$settingsText = if ($null -eq $backendSettings) { 'unknown' } elseif ($backendSettings.Count) { (@($backendSettings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ') } else { 'none set' }
$lines += "Settings: arm $(if ($Arm) { $Arm } else { 'not given' }); backend $settingsText."
$lines += ''
$lines += '| task | run | plan | hidden tests | minutes | minutes per round | asleep minutes | rounds | stalled | rung | retries | reviews failed | parked causes | models |'
$lines += '|---|---|---|---|---:|---:|---:|---:|---|---:|---:|---:|---|---|'
foreach ($r in $results) {
    $planLabel = if ($r.stalled) { "$($r.status) (stalled after $($r.stalledAfterMinutes) min)" } elseif ($r.timedOut) { "$($r.status) (timed out)" } else { $r.status }
    $lines += "| $($r.task) | $($r.run) | $planLabel | $(if ($r.hiddenPass) { 'PASS' } else { 'FAIL' }) ($($r.hiddenSummary)) | $($r.minutes) | $(if ($null -ne $r.minutesPerRound) { $r.minutesPerRound } else { 'n/a' }) | $($r.asleepMinutes) | $($r.rounds) | $($r.stalled) | $($r.maxRung) | $($r.autoRetries) | $($r.reviewFailed) | $($r.parkCauses) | $($r.models) |"
}
$medianLabel = if ($null -ne $medianMinutesPerRound) { $medianMinutesPerRound } else { 'n/a' }
$lines += ''
$lines += '| measure | value |'
$lines += '|---|---:|'
$lines += "| Stalled | $stalledCount of $n |"
$lines += "| Median minutes per round | $medianLabel |"
$batchEnded = Get-Date
$batchAsleepMinutes = Get-AsleepMinutes $batchStarted $batchEnded
$batchAwakeMinutes = Get-BatchAwakeMinutes $batchStarted $batchEnded
$lines += ''
$lines += "Batch awake time: $batchAwakeMinutes min (wall time $( [Math]::Round(($batchEnded - $batchStarted).TotalMinutes, 1) ) min; asleep $batchAsleepMinutes min)."
if ($stopReason) { $lines += "Batch stopped: $stopReason" }
$gate = @(
    @{ Name = 'No false accept (done while the hidden tests fail)'; Ok = ($falseAccepts.Count -eq 0); Detail = "$($falseAccepts.Count) of $n" },
    @{ Name = 'At least 80% done with the hidden tests passing'; Ok = ($rate -ge 80); Detail = "$completed of $n ($rate%)" },
    @{ Name = 'Every other run ended with a reason, none stuck or in error'; Ok = ($stuck.Count -eq 0); Detail = "$($stuck.Count) of $n" },
    @{ Name = 'No step parked for an environment problem'; Ok = ($environment.Count -eq 0); Detail = "$($environment.Count) of $n" }
)
$lines += ''
$lines += '## Gate'
foreach ($g in $gate) { $lines += "- $(if ($g.Ok) { 'PASS' } else { 'FAIL' }): $($g.Name): $($g.Detail)" }
$lines += ''
$lines += "Overall: $(if (@($gate | Where-Object { -not $_.Ok }).Count -eq 0) { 'PASS' } else { 'FAIL' })"
$lines | Set-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Encoding utf8
$lines | ForEach-Object { Write-Host $_ }
Write-Host "`nResults in $OutDir"
