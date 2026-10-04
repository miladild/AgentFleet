<#
.SYNOPSIS
    Runs the benchmark tasks as plans on a fleet backend and scores them against tests the models never see.

.DESCRIPTION
    For every task (and repeat): copies the task's starting project to a scratch folder, makes it a git repository,
    submits the task's steps as a plan to -BaseUrl, approves it with the defaults (or the options given), waits until
    the plan is done or blocked, then runs the task's HIDDEN acceptance tests on what the fleet produced. A run is judged by
    those tests, not by the plan's own status. Results are written to -OutDir as results.json and summary.md.

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
    [ValidateSet('auto', 'off')][string]$Review = 'auto',
    [switch]$WorkerOnly,
    [int]$AutoRetries = -1,
    [ValidateSet('as-written', 'light', 'standard', 'heavy')][string]$Tier = 'as-written'
)

$ErrorActionPreference = 'Stop'
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

function Invoke-Hidden([string]$Project, [string]$HiddenDir) {
    # The hidden tests are copied in only now, after the fleet is finished, and never leave this machine.
    $target = Join-Path $Project '.bench-hidden'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item (Join-Path $HiddenDir '*') $target -Recurse
    $passed = 0; $failed = 0; $ok = $true
    Push-Location $Project
    try {
        foreach ($file in Get-ChildItem $target -Filter '*.test.js') {
            $output = (& node --test --test-reporter=tap ".bench-hidden/$($file.Name)" 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { $ok = $false }
            if ($output -match '(?m)^#\s*pass (\d+)') { $passed += [int]$Matches[1] }
            if ($output -match '(?m)^#\s*fail (\d+)') { $failed += [int]$Matches[1] }
        }
    }
    finally { Pop-Location }
    return [pscustomobject]@{ Ok = ($ok -and $passed -gt 0); Passed = $passed; Failed = $failed }
}

function Invoke-Run([string]$Id, [int]$Number) {
    $task = Get-Content -LiteralPath (Join-Path $tasksRoot "$Id\task.json") -Raw | ConvertFrom-Json
    $project = Join-Path $scratchRoot "$Id-$Number\project"
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    Copy-Item (Join-Path $tasksRoot "$Id\project\*") $project -Recurse -Force
    Push-Location $project
    try {
        git init -q 2>$null
        git -c user.name=bench -c user.email=bench@example.invalid add -A 2>$null
        git -c user.name=bench -c user.email=bench@example.invalid commit -q -m 'start' 2>$null
    }
    finally { Pop-Location }

    $result = [ordered]@{ task = $Id; run = $Number; planId = $null; status = 'not-started'; hiddenPass = $false; hiddenSummary = ''
        falseAccept = $false; completed = $false; timedOut = $false; minutes = 0; stepsDone = 0; stepsParked = 0; rounds = 0; maxRung = 1
        autoRetries = 0; reviewFailed = 0; reviewPassed = 0; parkCauses = ''; models = ''; blockedReason = ''; error = '' }
    $started = Get-Date
    try {
        # -Tier sends every step at one tier: the tasks are written as standard, which only ever reaches the standard machine.
        $steps = $task.steps
        if ($Tier -ne 'as-written') {
            $steps = @($task.steps | ForEach-Object { $copy = $_ | ConvertTo-Json -Depth 6 | ConvertFrom-Json; $copy.tier = $Tier; $copy })
        }
        $body = @{ title = "bench $Id #$Number"; goal = $task.goal; workingDirectory = $project; steps = $steps; source = 'bench' } | ConvertTo-Json -Depth 8
        $created = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Post -ContentType 'application/json' -Body $body
        $result.planId = $created.id
        $approval = @{ review = $Review }
        if ($WorkerOnly) { $approval.recoveryScope = 'worker-only' }
        if ($AutoRetries -ge 0) { $approval.autoRetries = $AutoRetries }
        $null = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$($created.id)/approve" -Method Post -ContentType 'application/json' -Body ($approval | ConvertTo-Json)

        $deadline = $started.AddMinutes($TimeoutMinutes)
        $plan = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 15
            $plan = Invoke-RestMethod "$BaseUrl/api/plans/$($created.id)"
            if ($plan.status -in 'done', 'blocked', 'rejected') { break }
        }
        if ($plan.status -notin 'done', 'blocked', 'rejected') {
            $result.timedOut = $true
            try { $null = Invoke-RestMethod -Uri "$BaseUrl/api/plans/$($created.id)/stop" -Method Post } catch { }
            $plan = Invoke-RestMethod "$BaseUrl/api/plans/$($created.id)"
        }

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
        $result.parkCauses = (@($events | Where-Object kind -eq 'step-parked' | ForEach-Object { $_.failureSignature }) | Sort-Object -Unique) -join ','
        $result.models = (@($events | Where-Object { $_.modelNode } | ForEach-Object { $_.modelNode }) | Sort-Object -Unique) -join ','
        $blocked = @($events | Where-Object kind -eq 'plan-blocked') | Select-Object -Last 1
        if ($blocked -and $blocked.detail) {
            $reason = ($blocked.detail -replace '\s+', ' ')
            $result.blockedReason = $reason.Substring(0, [Math]::Min(240, $reason.Length))
        }
    }
    catch { $result.error = $_.Exception.Message }
    $result.minutes = [Math]::Round(((Get-Date) - $started).TotalMinutes, 1)

    $hidden = Invoke-Hidden $project (Join-Path $tasksRoot "$Id\hidden")
    $result.hiddenPass = $hidden.Ok
    $result.hiddenSummary = "pass $($hidden.Passed), fail $($hidden.Failed)"
    $result.falseAccept = ($result.status -eq 'done' -and -not $hidden.Ok)
    $result.completed = ($result.status -eq 'done' -and $hidden.Ok)
    return [pscustomobject]$result
}

$results = @()
foreach ($id in Get-TaskIds) {
    for ($run = 1; $run -le $Repeat; $run++) {
        Write-Host ("[{0}] {1} run {2} ..." -f (Get-Date -Format 'HH:mm:ss'), $id, $run)
        $r = Invoke-Run $id $run
        $results += $r
        $r | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $OutDir 'results.jsonl')
        Write-Host ("        {0}: plan {1}, hidden tests {2} ({3}), {4} min, {5} rounds, {6} auto retries, {7} reviews failed" -f
            $id, $r.status, $(if ($r.hiddenPass) { 'PASS' } else { 'FAIL' }), $r.hiddenSummary, $r.minutes, $r.rounds, $r.autoRetries, $r.reviewFailed)
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'results.json') -Encoding utf8

$n = $results.Count
$completed = @($results | Where-Object completed).Count
$falseAccepts = @($results | Where-Object falseAccept)
$stuck = @($results | Where-Object { $_.timedOut -or ($_.status -eq 'blocked' -and -not $_.blockedReason) -or $_.error })
$environment = @($results | Where-Object { $_.parkCauses -match 'environment' })
$rate = if ($n) { [Math]::Round(100.0 * $completed / $n, 0) } else { 0 }

$lines = @()
$lines += "# Benchmark results ($n runs, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
$lines += ''
$lines += "Backend $BaseUrl; steps $(if ($Tier -eq 'as-written') { 'at the tier the task says (standard)' } else { "all at the $Tier tier" }); second opinion $Review; $(if ($WorkerOnly) { 'worker only' } else { 'hub rescue allowed' }); automatic retries $(if ($AutoRetries -ge 0) { $AutoRetries } else { 'default' })."
$lines += ''
$lines += '| task | run | plan | hidden tests | minutes | rounds | rung | retries | reviews failed | parked causes | models |'
$lines += '|---|---|---|---|---|---|---|---|---|---|---|'
foreach ($r in $results) {
    $lines += "| $($r.task) | $($r.run) | $($r.status)$(if ($r.timedOut) { ' (timed out)' }) | $(if ($r.hiddenPass) { 'PASS' } else { 'FAIL' }) ($($r.hiddenSummary)) | $($r.minutes) | $($r.rounds) | $($r.maxRung) | $($r.autoRetries) | $($r.reviewFailed) | $($r.parkCauses) | $($r.models) |"
}
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
