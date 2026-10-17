<# Verifies plan progress handling, run timing and hidden-test process cleanup. #>
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-Bench.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Invoke-Bench.ps1 has syntax errors.' }
$hiddenFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Hidden' }, $true)
if (-not $hiddenFunction) { throw 'Invoke-Hidden was not found.' }
Invoke-Expression $hiddenFunction.Extent.Text
$sleepFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-AsleepMinutes' }, $true)
$awakeFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-BatchAwakeMinutes' }, $true)
$limitFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-MaxHours' }, $true)
if (-not $sleepFunction -or -not $awakeFunction -or -not $limitFunction) { throw 'A batch timing function was not found.' }
Invoke-Expression $sleepFunction.Extent.Text
Invoke-Expression $awakeFunction.Extent.Text
Invoke-Expression $limitFunction.Extent.Text
$waitFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-ForPlan' }, $true)
$minutesFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-MinutesPerRound' }, $true)
$medianFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-MedianMinutesPerRound' }, $true)
if (-not $waitFunction -or -not $minutesFunction -or -not $medianFunction) { throw 'A progress or round timing function was not found.' }
Invoke-Expression $waitFunction.Extent.Text
Invoke-Expression $minutesFunction.Extent.Text
Invoke-Expression $medianFunction.Extent.Text
$source = $ast.Extent.Text
if ($ast.ParamBlock.Extent.Text -notmatch '\[double\]\$MaxHours = 4') { throw 'MaxHours must default to four hours.' }
foreach ($pattern in '\$result\.asleepMinutes\s*=\s*Get-AsleepMinutes', '\| task \| run \| plan \| hidden tests \| minutes \| minutes per round \|', '\| stalled \|', 'Median minutes per round', "stopped: MaxHours", 'Batch stopped: \$stopReason', 'Write-RunRecord\s+\$batchStarted', "'run\.json'") {
    if ($source -notmatch $pattern) { throw "Benchmark timing output is missing: $pattern" }
}
if ($ast.ParamBlock.Extent.Text -notmatch '\[int\]\$NoProgressMinutes = 0') { throw 'NoProgressMinutes must default to off.' }
if ($source -notmatch '\$result\.minutesPerRound\s*=\s*Get-MinutesPerRound\s+\$result\.minutes\s+\$result\.rounds' -or
    $source -notmatch '\$stalledCount\s*=\s*@\(\$results\s*\|\s*Where-Object stalled\)\.Count' -or
    $source -notmatch '\$medianMinutesPerRound\s*=\s*Get-MedianMinutesPerRound\s+\$results') { throw 'Per-round or summary statistics are not written from the run results.' }
$httpCalls = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -in 'Invoke-RestMethod', 'Invoke-WebRequest' }, $true))
if (-not $httpCalls.Count) { throw 'No benchmark HTTP calls were found.' }
foreach ($call in $httpCalls) { if ($call.Extent.Text -notmatch '(?i)-TimeoutSec\s+30\b') { throw "HTTP timeout missing: $($call.Extent.Text)" } }

$taskScript = Join-Path $PSScriptRoot 'Test-BenchTasks.ps1'
$taskErrors = $null
$taskAst = [Management.Automation.Language.Parser]::ParseFile($taskScript, [ref]$null, [ref]$taskErrors)
if ($taskErrors.Count) { throw 'Test-BenchTasks.ps1 has syntax errors.' }
$taskHiddenFunction = $taskAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-HiddenOn' }, $true)
if (-not $taskHiddenFunction) { throw 'Invoke-HiddenOn was not found.' }
foreach ($pattern in '--test-timeout=60000', 'WaitForExit\(120000\)', 'taskkill\.exe /F /T /PID', "PriorityClass = 'BelowNormal'", '\$null = \$p\.Handle') {
    if ($taskHiddenFunction.Extent.Text -notmatch $pattern) { throw "Test-BenchTasks is missing bounded process behavior: $pattern" }
}

$script:fakeClockEnabled = $false
$script:fakeNow = [datetime]'2026-10-14T11:00:00'
function Get-Date {
    if ($script:fakeClockEnabled) { return $script:fakeNow }
    Microsoft.PowerShell.Utility\Get-Date @PSBoundParameters
}
function Start-Sleep {
    param([int]$Seconds = 0)
    if ($script:fakeClockEnabled) { $script:fakeNow = $script:fakeNow.AddSeconds($Seconds); return }
    Microsoft.PowerShell.Utility\Start-Sleep @PSBoundParameters
}

$script:fakePowerEvents = @(
    [pscustomobject]@{ Id = 506; TimeCreated = [datetime]'2026-10-07T10:10:00' },
    [pscustomobject]@{ Id = 507; TimeCreated = [datetime]'2026-10-07T10:40:00' },
    [pscustomobject]@{ Id = 506; TimeCreated = [datetime]'2026-10-07T11:00:00' },
    [pscustomobject]@{ Id = 507; TimeCreated = [datetime]'2026-10-07T11:15:00' }
)
function Get-WinEvent {
    param([hashtable]$FilterHashtable)
    $script:observedPowerFilter = $FilterHashtable
    $script:fakePowerEvents
}
$batchStart = [datetime]'2026-10-07T10:00:00'
$batchEnd = [datetime]'2026-10-07T12:00:00'
if ((Get-AsleepMinutes $batchStart $batchEnd) -ne 45) { throw 'Kernel-Power 506/507 pairing did not sum 45 asleep minutes.' }
if ($script:observedPowerFilter.LogName -ne 'System' -or $script:observedPowerFilter.ProviderName -ne 'Microsoft-Windows-Kernel-Power' -or ($script:observedPowerFilter.Id -join ',') -ne '506,507' -or $script:observedPowerFilter.StartTime -ne $batchStart -or $script:observedPowerFilter.EndTime -ne $batchEnd) { throw 'Kernel-Power query did not use the row time window and 506/507 IDs.' }
if ((Get-BatchAwakeMinutes $batchStart $batchEnd) -ne 75) { throw 'Batch awake time did not subtract sleep.' }
if ((Test-MaxHours 239.9 4) -or (Test-MaxHours 240 4) -or -not (Test-MaxHours 240.1 4)) { throw 'The MaxHours boundary is wrong.' }

# run.json holds the batch's machines and models: names and models only, never an address or a path. A failed
# configuration call must still leave the file, with no machines in it. The configuration call is stubbed here.
$runNodesFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-RunNodes' }, $true)
$runRecordFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-RunRecord' }, $true)
if (-not $runNodesFunction -or -not $runRecordFunction) { throw 'Get-RunNodes or Write-RunRecord was not found.' }
Invoke-Expression $runNodesFunction.Extent.Text
Invoke-Expression $runRecordFunction.Extent.Text
$script:fleetConfig = $null
function Invoke-RestMethod {
    param([string]$Uri, [int]$TimeoutSec, [string]$Method)
    if ($TimeoutSec -ne 30) { throw "Unexpected timeout: $TimeoutSec" }
    if ($Uri -like '*/api/fleet-config') {
        if ($null -eq $script:fleetConfig) { throw 'The fleet configuration is not reachable.' }
        return $script:fleetConfig
    }
    if (-not $script:fakePlanEnabled) { throw "Unexpected request: $Uri" }
    if ($Uri -like '*/stop') {
        if ($Method -ne 'Post') { throw 'The stalled plan was not stopped with POST.' }
        $script:fakePlanStopCount++
        $script:fakePlanStopped = $true
        return [pscustomobject]@{ stopped = $true }
    }
    if ($Uri -notlike '*/api/plans/fake-plan') { throw "Unexpected request: $Uri" }
    $script:fakePlanPolls++
    $status = if ($script:fakePlanStopped) { 'blocked' } else { 'running' }
    $events = @()
    if ($script:fakePlanMode -eq 'progress') {
        $rounds = [int][Math]::Floor($script:fakePlanPolls / 6)
        $progressKinds = @('round-classified', 'check-passed', 'check-failed')
        $events = @(for ($round = 1; $round -le $rounds; $round++) { [pscustomobject]@{ kind = $progressKinds[($round - 1) % $progressKinds.Count] } })
        if ($script:fakePlanPolls -ge 25) { $status = 'done' }
    }
    elseif ($script:fakePlanMode -eq 'capped') {
        # Like the backend, the event list keeps the first event and the latest 29 once it passes 30 events.
        $rounds = [int][Math]::Floor($script:fakePlanPolls / 6)
        $all = @([pscustomobject]@{ atUtc = '2026-10-14T11:00:00.0000000Z'; kind = 'plan-started'; stepId = $null; attempt = $null })
        for ($n = 1; $n -le 10 * $rounds; $n++) {
            $kind = if ($n % 10 -eq 0) { 'round-classified' } else { 'files-changed' }
            $all += [pscustomobject]@{ atUtc = ([datetime]'2026-10-14T11:00:00').AddSeconds($n).ToString('o'); kind = $kind; stepId = 'step-1'; attempt = 1 }
        }
        $events = if ($all.Count -gt 30) { @($all[0]) + @($all[($all.Count - 29)..($all.Count - 1)]) } else { $all }
        if ($script:fakePlanPolls -ge 40) { $status = 'done' }
    }
    elseif ($script:fakePlanMode -eq 'old-behavior' -and $script:fakePlanPolls -ge 12) { $status = 'done' }
    return [pscustomobject]@{ status = $status; events = $events; steps = @() }
}
$script:fakePlanEnabled = $false
$script:fakePlanMode = ''
$script:fakePlanPolls = 0
$script:fakePlanStopCount = 0
$script:fakePlanStopped = $false
$runPrefix = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench-run-'))
$runScratch = $runPrefix + [Guid]::NewGuid().ToString('N')
$BaseUrl = 'http://192.0.2.10:8010'
$OutDir = Join-Path $runScratch 'out'
$Tier = 'as-written'; $Repeat = 2; $WorkerOnly = $true; $Review = 'auto'; $AutoRetries = -1; $TimeoutMinutes = 120; $NoProgressMinutes = 0
function Reset-FakePlan([string]$Mode) {
    $script:fakePlanEnabled = $true
    $script:fakePlanMode = $Mode
    $script:fakePlanPolls = 0
    $script:fakePlanStopCount = 0
    $script:fakePlanStopped = $false
    $script:fakeNow = [datetime]'2026-10-14T11:00:00'
    $script:fakeClockEnabled = $true
}
Reset-FakePlan 'no-progress'
$stalledResult = [ordered]@{ timedOut = $false; stalled = $false; stalledAfterMinutes = $null }
$fakeStart = $script:fakeNow
$stalledPlan = Wait-ForPlan 'fake-plan' $fakeStart ($fakeStart.AddMinutes(10)) 1 $stalledResult
if ($stalledResult.timedOut -ne $true -or $stalledResult.stalled -ne $true -or $stalledResult.stalledAfterMinutes -ne 1) { throw 'A plan with no progress was not recorded as stalled after its window.' }
if ($script:fakePlanStopCount -ne 1 -or $stalledPlan.status -ne 'blocked') { throw 'A plan with no progress was not stopped and read again.' }
Write-Host 'PASS: a no-progress plan was stopped and recorded as stalled.' -ForegroundColor Green

Reset-FakePlan 'progress'
$progressResult = [ordered]@{ timedOut = $false; stalled = $false; stalledAfterMinutes = $null }
$fakeStart = $script:fakeNow
$progressPlan = Wait-ForPlan 'fake-plan' $fakeStart ($fakeStart.AddMinutes(10)) 2 $progressResult
if ($progressPlan.status -ne 'done' -or $progressResult.timedOut -or $progressResult.stalled -or $script:fakePlanStopCount -ne 0 -or @($progressPlan.events).Count -ne 4 -or @($progressPlan.events | Select-Object -ExpandProperty kind -Unique).Count -ne 3) { throw 'Round and check events did not reset the no-progress window.' }
Write-Host 'PASS: round and check events reset the no-progress window.' -ForegroundColor Green

Reset-FakePlan 'capped'
$cappedResult = [ordered]@{ timedOut = $false; stalled = $false; stalledAfterMinutes = $null }
$fakeStart = $script:fakeNow
$cappedPlan = Wait-ForPlan 'fake-plan' $fakeStart ($fakeStart.AddMinutes(12)) 3 $cappedResult
if ($cappedPlan.status -ne 'done' -or $cappedResult.timedOut -or $cappedResult.stalled -or $script:fakePlanStopCount -ne 0 -or @($cappedPlan.events).Count -ne 30) { throw 'A long plan with a capped event list was stopped as stalled. The window must follow the newest progress event, not the count of progress events.' }
Write-Host 'PASS: a long plan with a capped event list is not stopped as stalled.' -ForegroundColor Green

Reset-FakePlan 'old-behavior'
$offResult = [ordered]@{ timedOut = $false; stalled = $false; stalledAfterMinutes = $null }
$fakeStart = $script:fakeNow
$offPlan = Wait-ForPlan 'fake-plan' $fakeStart ($fakeStart.AddMinutes(4)) 0 $offResult
if ($offPlan.status -ne 'done' -or $offResult.timedOut -or $offResult.stalled -or $script:fakePlanStopCount -ne 0) { throw 'NoProgressMinutes=0 did not preserve the timeout-only behavior.' }
$script:fakeClockEnabled = $false
$script:fakePlanEnabled = $false
if ((Get-MinutesPerRound 16 5) -ne 3.2 -or $null -ne (Get-MinutesPerRound 16 0)) { throw 'Minutes per round was not computed or was not null when no rounds finished.' }
if ((Get-MedianMinutesPerRound @([pscustomobject]@{ minutesPerRound = 2 }, [pscustomobject]@{ minutesPerRound = 4 })) -ne 3) { throw 'Median minutes per round is wrong for an even set.' }
if ($null -ne (Get-MedianMinutesPerRound @([pscustomobject]@{ minutesPerRound = $null }))) { throw 'Median minutes per round must be null when there are no rounds.' }
Write-Host 'PASS: no-progress off, per-round time and median calculations.' -ForegroundColor Green
try {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $script:fleetConfig = [pscustomobject]@{ version = 3; nodes = @(
        [pscustomobject]@{ name = 'node-one'; url = 'http://192.0.2.11:11434/v1'; model = 'model-alpha:7b' },
        [pscustomobject]@{ name = 'node-two'; url = 'http://192.0.2.12:11434/v1'; model = 'model-beta:3b' }) }
    # The raw text is checked: ConvertFrom-Json turns date strings into local DateTime values, which hides the UTC form.
    Write-RunRecord ([datetime]::SpecifyKind([datetime]'2026-10-14T09:00:00', [DateTimeKind]::Utc)) @('semver-compare', 'slugify')
    $runText = Get-Content -LiteralPath (Join-Path $OutDir 'run.json') -Raw
    $run = $runText | ConvertFrom-Json
    if ($runText -notmatch '"startedAtUtc":\s*"2026-10-14T09:00:00(\.\d+)?Z"') { throw 'run.json does not record the batch start in UTC.' }
    if (($run.options.tasks -join ',') -ne 'semver-compare,slugify' -or $run.options.repeat -ne 2 -or $run.options.workerOnly -ne $true -or $null -ne $run.options.autoRetries -or $run.options.timeoutMinutes -ne 120 -or $run.options.noProgressMinutes -ne 0) { throw 'run.json options are wrong.' }
    $nodePairs = @($run.nodes | ForEach-Object { "$($_.name)=$($_.model)" })
    if (($nodePairs -join ' ') -ne 'node-one=model-alpha:7b node-two=model-beta:3b') { throw "run.json nodes are wrong: $($nodePairs -join ' ')" }
    foreach ($leak in '192\.0\.2\.', 'http', '11434', [regex]::Escape($runScratch)) {
        if ($runText -match $leak) { throw "run.json holds an address or a path: $leak" }
    }
    $script:fleetConfig = $null
    Write-RunRecord ([datetime]::SpecifyKind([datetime]'2026-10-14T10:00:00', [DateTimeKind]::Utc)) @('slugify')
    $failedText = Get-Content -LiteralPath (Join-Path $OutDir 'run.json') -Raw
    $run = $failedText | ConvertFrom-Json
    if (@($run.nodes).Count -ne 0 -or $failedText -notmatch '"startedAtUtc":\s*"2026-10-14T10:00:00') { throw 'A failed configuration call must still leave run.json, with no machines.' }
    Write-Host 'PASS: run.json holds names and models only, and a failed configuration call leaves it without machines.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $runScratch) {
        $resolved = (Resolve-Path -LiteralPath $runScratch).Path
        if ($resolved -ne $runScratch -or -not $resolved.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test scratch directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$prefix = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench-harness-'))
$scratch = $prefix + [Guid]::NewGuid().ToString('N')
$project = Join-Path $scratch 'project'
$hidden = Join-Path $scratch 'hidden'
try {
    New-Item -ItemType Directory -Path $project, $hidden -Force | Out-Null
    @'
{
  "name": "bench-harness",
  "version": "1.0.0",
  "scripts": { "test": "node --test" }
}
'@ | Set-Content -LiteralPath (Join-Path $project 'package.json') -Encoding utf8
    @'
const test = require('node:test');
const { spawn } = require('node:child_process');
test('synchronous infinite loop with a child process', () => {
  const child = spawn(process.execPath, ['-e', 'while (true) {}'], { detached: true, stdio: 'ignore' });
  child.unref();
  while (true) {}
});
'@ | Set-Content -LiteralPath (Join-Path $hidden 'hang.test.js') -Encoding utf8

    $before = @((Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.ProcessId }))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $score = Invoke-Hidden $project $hidden
    $timer.Stop()
    if ($timer.Elapsed.TotalSeconds -ge 150) { throw "Invoke-Hidden took $([Math]::Round($timer.Elapsed.TotalSeconds, 1)) s; expected under 150 s." }
    if ($timer.Elapsed.TotalSeconds -lt 100) { throw "Invoke-Hidden returned too early ($([Math]::Round($timer.Elapsed.TotalSeconds, 1)) s); expected the 120 s outer timeout." }
    if ($score.TimedOut -ne $true -or $score.Ok -ne $false) { throw 'A synchronous infinite loop must return TimedOut=true and Ok=false.' }
    if ($score.Summary -ne 'timed out after 120 s (an infinite loop?)') { throw 'The timeout summary changed.' }
    $testNodePattern = 'hang\.test\.js|while \(true\)'
    $left = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains [int]$_.ProcessId -and $_.CommandLine -match $testNodePattern })
    if ($left.Count) {
        foreach ($node in $left) { & taskkill.exe /F /T /PID $node.ProcessId 2>$null | Out-Null }
        throw "A node process started by the test remained after timeout and was cleaned up: PID $($left[0].ProcessId)."
    }
    Write-Host 'PASS: synchronous infinite loop timed out, was killed, and left no node process.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        if ($resolved -ne $scratch -or -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test scratch directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
