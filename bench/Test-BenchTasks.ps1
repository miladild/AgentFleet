<#
.SYNOPSIS
    Checks that the benchmark tasks are sound, without involving any model.

.DESCRIPTION
    For every task: task.json has what a plan needs; the hidden tests FAIL on the starting project (otherwise they prove
    nothing) and PASS on the known-correct solution (otherwise the task cannot be won). With -BaseUrl the plan is also
    sent to a backend as a dry run, which says whether the fleet would accept it (no plan is saved). Exit code 1 when
    any task is unsound.
#>
[CmdletBinding()]
param(
    [string[]]$Tasks = @('all'),
    [string]$BaseUrl
)

$ErrorActionPreference = 'Stop'
$tasksRoot = Join-Path $PSScriptRoot 'tasks'
$ids = Get-ChildItem -LiteralPath $tasksRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'task.json') } | ForEach-Object Name
if ($Tasks -notcontains 'all') { $ids = $Tasks }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('agent-fleet-bench-check-' + [Guid]::NewGuid().ToString('N'))
$bad = 0

function Invoke-HiddenOn([string]$Project, [string]$HiddenDir) {
    $target = Join-Path $Project '.bench-hidden'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $HiddenDir '*') $target -Recurse -Force
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

try {
    foreach ($id in $ids) {
        $dir = Join-Path $tasksRoot $id
        $problems = @()
        $task = $null
        try { $task = Get-Content -LiteralPath (Join-Path $dir 'task.json') -Raw | ConvertFrom-Json } catch { $problems += "task.json does not parse: $($_.Exception.Message)" }
        if ($task) {
            if ($task.id -ne $id) { $problems += "task.json id '$($task.id)' is not the folder name" }
            if (-not $task.goal) { $problems += 'no goal' }
            if (-not $task.steps -or @($task.steps).Count -lt 1) { $problems += 'no steps' }
            foreach ($step in @($task.steps)) {
                foreach ($field in 'title', 'detail', 'verify', 'tier') { if (-not $step.$field) { $problems += "a step has no $field" } }
            }
            $last = @($task.steps) | Select-Object -Last 1
            if ($last -and $last.verify -ne 'npm test') { $problems += "the last step's check must be 'npm test', it is '$($last.verify)'" }
        }
        foreach ($folder in 'project', 'hidden', 'solution') { if (-not (Test-Path (Join-Path $dir $folder))) { $problems += "no $folder folder" } }
        if (-not (Test-Path (Join-Path $dir 'project\package.json'))) { $problems += 'project has no package.json' }
        # The starting project is what the models get. It must not hold a test, and above all not a copy of the hidden ones.
        if (Test-Path (Join-Path $dir 'project')) {
            $leaks = @(Get-ChildItem (Join-Path $dir 'project') -Recurse -Force | Where-Object { $_.Name -like '*hidden*' -or $_.Name -like '*.test.js' -or $_.Name -eq 'node_modules' })
            foreach ($leak in $leaks) { $problems += "project\ contains $($leak.FullName.Substring($dir.Length + 1)): the models would see it" }
        }

        $start = $null; $solved = $null
        if ($problems.Count -eq 0) {
            $a = Join-Path $scratch "$id-start"; New-Item -ItemType Directory -Path $a -Force | Out-Null
            Copy-Item (Join-Path $dir 'project\*') $a -Recurse -Force
            $start = Invoke-HiddenOn $a (Join-Path $dir 'hidden')
            if ($start.Ok) { $problems += 'the hidden tests PASS on the starting project: they prove nothing' }
            $b = Join-Path $scratch "$id-solved"; New-Item -ItemType Directory -Path $b -Force | Out-Null
            Copy-Item (Join-Path $dir 'project\*') $b -Recurse -Force
            Copy-Item (Join-Path $dir 'solution\*') $b -Recurse -Force
            $solved = Invoke-HiddenOn $b (Join-Path $dir 'hidden')
            if (-not $solved.Ok) { $problems += "the hidden tests FAIL on the solution ($($solved.Passed) pass, $($solved.Failed) fail): the task cannot be won" }

            if ($BaseUrl -and $task) {
                $body = @{ title = "check $id"; goal = $task.goal; workingDirectory = $a; steps = $task.steps; dryRun = $true } | ConvertTo-Json -Depth 8
                try {
                    $answer = Invoke-RestMethod -Uri "$BaseUrl/api/plans" -Method Post -ContentType 'application/json' -Body $body
                    foreach ($p in @($answer.problems)) { if ($p) { $problems += "the fleet would refuse the plan: $p" } }
                }
                catch { $problems += "the fleet refused the plan: $($_.ErrorDetails.Message)" }
            }
        }

        $startText = if ($start) { "start: $($start.Passed) pass / $($start.Failed) fail" } else { 'start: n/a' }
        $solvedText = if ($solved) { "solution: $($solved.Passed) pass / $($solved.Failed) fail" } else { 'solution: n/a' }
        if ($problems.Count -eq 0) { Write-Host ("  [ ok ] {0,-18} {1}; {2}" -f $id, $startText, $solvedText) -ForegroundColor Green }
        else { $bad++; Write-Host ("  [FAIL] {0,-18} {1}; {2}" -f $id, $startText, $solvedText) -ForegroundColor Red; $problems | ForEach-Object { Write-Host "         $_" -ForegroundColor Red } }
    }
}
finally { if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue } }

if ($bad) { Write-Host "`n$bad task(s) unsound." -ForegroundColor Red; exit 1 }
Write-Host "`nAll $(@($ids).Count) task(s) sound." -ForegroundColor Green
