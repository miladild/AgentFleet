<#
    Settings a benchmark backend may be started with, and the environment handling that goes with them.
    Dot-sourced by Start-BenchBackend.ps1, Invoke-Bench.ps1 and the benchmark checks. Works on Windows PowerShell 5.1 and PowerShell 7.

    Only the two test-first settings can be passed to a benchmark backend. Anything else is refused, so an arm of a
    comparison cannot change some other part of the backend without anyone noticing.
#>

$script:BenchSettingValues = [ordered]@{
    FLEET_PLAN_TEST_FIRST = @('off', 'on')
    FLEET_PLAN_FAIL_FIRST = @('off', 'warn', 'enforce')
}

# Turns NAME=value entries into an ordered dictionary with the canonical name and a lower-case value. Throws on anything
# else: an unknown name, a value the setting does not take, a missing '=', or a name given twice.
function ConvertTo-BenchSettings([string[]]$Setting) {
    $accepted = (@($script:BenchSettingValues.Keys) | ForEach-Object { "$_ = " + ($script:BenchSettingValues[$_] -join ' | ') }) -join '; '
    $result = [ordered]@{}
    foreach ($entry in $Setting) {
        $text = [string]$entry
        if ($text -notmatch '^([^=]*)=(.*)$') { throw "Setting '$text' is not NAME=value. Accepted settings: $accepted." }
        $name = $Matches[1]
        $value = $Matches[2].ToLowerInvariant()
        $canonical = $null
        foreach ($known in $script:BenchSettingValues.Keys) { if ($known -eq $name) { $canonical = $known } }
        if (-not $canonical) { throw "Unknown setting '$name'. Accepted settings: $accepted." }
        if ($script:BenchSettingValues[$canonical] -notcontains $value) { throw "Value '$($Matches[2])' is not accepted for $canonical. Accepted settings: $accepted." }
        if ($result.Contains($canonical)) { throw "Setting $canonical is given more than once. Accepted settings: $accepted." }
        $result[$canonical] = $value
    }
    return $result
}

# Sets the environment of every known setting name just before a backend process is started (it copies the environment when
# it starts): a name given is set, a name not given is removed, so a value the launching shell already holds is never passed
# on. Returns what each name held before, for Restore-BenchSettingEnvironment.
function Set-BenchSettingEnvironment([System.Collections.IDictionary]$Settings) {
    $previous = [ordered]@{}
    foreach ($name in $script:BenchSettingValues.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        if ($Settings.Contains($name)) { [Environment]::SetEnvironmentVariable($name, [string]$Settings[$name]) }
        else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    }
    return $previous
}

# Puts back what Set-BenchSettingEnvironment replaced. A name that held nothing is removed again. The removal goes through
# the environment drive: on PowerShell 7 SetEnvironmentVariable with a null value leaves an empty variable behind.
function Restore-BenchSettingEnvironment([System.Collections.IDictionary]$Previous) {
    foreach ($name in $Previous.Keys) {
        if ($null -eq $Previous[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, [string]$Previous[$name]) }
    }
}
