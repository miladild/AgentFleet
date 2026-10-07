<# Checks hidden TAP retention using the benchmark's actual function, without a backend or model. #>
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-Bench.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Invoke-Bench.ps1 has syntax errors.' }
$hiddenFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Hidden' }, $true)
if (-not $hiddenFunction) { throw 'Invoke-Hidden was not found.' }
Invoke-Expression $hiddenFunction.Extent.Text

$prefix = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench-evidence-'))
$scratch = $prefix + [Guid]::NewGuid().ToString('N')
$project = Join-Path $scratch 'project'
$hidden = Join-Path $scratch 'hidden'
$evidence = Join-Path $scratch 'results/task-test-1/hidden'
try {
    New-Item -ItemType Directory -Path $project, $hidden -Force | Out-Null
    @'
const test = require('node:test');
const assert = require('node:assert/strict');
test('passing case', () => assert.equal(2, 2));
'@ | Set-Content -LiteralPath (Join-Path $hidden 'pass.test.js') -Encoding utf8
    @'
const test = require('node:test');
const assert = require('node:assert/strict');
test('named failing assertion', () => assert.equal(1, 2, 'expected synthetic result'));
'@ | Set-Content -LiteralPath (Join-Path $hidden 'fail.test.js') -Encoding utf8

    $without = Invoke-Hidden $project $hidden
    $with = Invoke-Hidden $project $hidden $evidence
    foreach ($score in $without, $with) {
        if ($score.Ok -ne $false -or $score.Passed -ne 1 -or $score.Failed -ne 1) { throw 'Hidden scoring changed: expected false, one pass, one fail.' }
    }
    if (@(Get-ChildItem -LiteralPath $evidence -File).Count -ne 2) { throw 'Expected TAP evidence for both hidden files.' }
    $failure = Get-Content -LiteralPath (Join-Path $evidence 'fail.test.js.tap.txt') -Raw
    foreach ($pattern in '(?m)^TAP version 13', 'not ok 1 - named failing assertion', 'expected synthetic result', '(?m)^# tests 1', '(?m)^# pass 0', '(?m)^# fail 1', '(?m)^# duration_ms ') {
        if ($failure -notmatch $pattern) { throw "Missing failure evidence: $pattern" }
    }
    $passing = Get-Content -LiteralPath (Join-Path $evidence 'pass.test.js.tap.txt') -Raw
    foreach ($pattern in 'ok 1 - passing case', '(?m)^# pass 1', '(?m)^# fail 0') {
        if ($passing -notmatch $pattern) { throw "Missing passing evidence: $pattern" }
    }
    Write-Host 'PASS: hidden scores unchanged; complete passing and failing TAP retained.'
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        if ($resolved -ne $scratch -or -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test scratch directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
