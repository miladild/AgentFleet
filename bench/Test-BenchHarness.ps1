<# Verifies that Invoke-Hidden kills a synchronous infinite loop and leaves no test process. #>
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
$source = $ast.Extent.Text
if ($ast.ParamBlock.Extent.Text -notmatch '\[double\]\$MaxHours = 4') { throw 'MaxHours must default to four hours.' }
foreach ($pattern in '\$result\.asleepMinutes\s*=\s*Get-AsleepMinutes', '\| task \| run \| plan \| hidden tests \| minutes \| asleep minutes \|', "stopped: MaxHours", 'Batch stopped: \$stopReason') {
    if ($source -notmatch $pattern) { throw "Benchmark timing output is missing: $pattern" }
}
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
