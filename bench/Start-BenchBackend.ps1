<#
.SYNOPSIS
    Runs the current source as a second backend, on its own port with its own plans folder, for the benchmark.

.DESCRIPTION
    Publishes agent-fleet\agent to a temporary folder and starts it on -Port (default 8010) with a COPY of the fleet
    configuration (your machines and models) and an empty plans folder, so benchmark plans never mix with yours and the
    running fleet is not touched. The copy has the notice webhook removed. It uses the same machines as the real fleet,
    so they are busy while a benchmark runs.

    Stop it with:  .\bench\Start-BenchBackend.ps1 -Stop

.PARAMETER Config
    The fleet.config.json to copy. Default: the one Find-FleetConfig finds (the installed copy).

.PARAMETER NoHubModel
    Points the copied configuration's hub node (the one marked fallback, which the configuration must have) at a closed local port,
    so nothing in this backend can load or call the hub's model: a plan that tries to use it fails at once instead of filling the
    hub machine's graphics memory. Use it for runs that must use only the workers; combine it with Invoke-Bench.ps1 -WorkerOnly.

.PARAMETER WorkRoot
    Where the published backend, the copied configuration, the plans and the logs live.

.PARAMETER HealthTimeoutSeconds
    How long to wait for /health before giving up. Default 90.

.PARAMETER Setting
    NAME=value settings to start the backend with, for comparing arms. Only FLEET_PLAN_TEST_FIRST (off, on) and
    FLEET_PLAN_FAIL_FIRST (off, warn, enforce) are accepted. The settings are written to backend.settings.json in the
    work folder, which Invoke-Bench.ps1 copies into run.json. Without this parameter the backend starts with none.
#>
[CmdletBinding()]
param(
    [int]$Port = 8010,
    [string]$Config,
    [string]$InstallRoot,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench'),
    [ValidateRange(10, 900)][int]$HealthTimeoutSeconds = 90,
    [switch]$NoHubModel,
    [string[]]$Setting,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\FleetCommon.ps1"
. "$PSScriptRoot\BenchSettings.ps1"
$pidFile = Join-Path $WorkRoot 'backend.pid'
# Refused before anything is stopped or published: a bad setting must not touch the backend that is running.
$settings = ConvertTo-BenchSettings $Setting

function Stop-BenchBackend {
    if (Test-Path -LiteralPath $pidFile) {
        Stop-Process -Id ([int](Get-Content -LiteralPath $pidFile)) -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    }
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($listener) { Stop-Process -Id $listener.OwningProcess -Force -ErrorAction SilentlyContinue }
}

if ($Stop) {
    Stop-BenchBackend
    # The file describes a backend that is gone now; a batch must not read its settings afterwards.
    Remove-Item -LiteralPath (Join-Path $WorkRoot 'backend.settings.json') -Force -ErrorAction SilentlyContinue
    Write-Ok "The benchmark backend on port $Port is stopped."
    return
}

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
Stop-BenchBackend
Start-Sleep -Seconds 1
# A file left by an earlier start describes a backend that is gone; it is replaced once this one answers.
Remove-Item -LiteralPath (Join-Path $WorkRoot 'backend.settings.json') -Force -ErrorAction SilentlyContinue

$source = Find-FleetConfig $Config $InstallRoot
if (-not $source) { throw 'No fleet.config.json found. Pass -Config <path>.' }
# (not $config: PowerShell variable names are case-insensitive and that is the typed -Config parameter)
$fleetConfig = Read-FleetConfig $source
if ($fleetConfig.PSObject.Properties.Name -contains 'notifyUrl') { $fleetConfig.PSObject.Properties.Remove('notifyUrl') }
$configCopy = Join-Path $WorkRoot 'fleet.config.json'
if ($NoHubModel) {
    foreach ($node in @($fleetConfig.nodes | Where-Object { $_.fallback -eq $true })) { $node.url = 'http://127.0.0.1:9/v1' }
    Write-Ok 'The hub node of the copy points at a closed port: its model cannot be used by this backend.'
}
Write-FleetConfig $configCopy $fleetConfig
Write-Ok "Configuration copied from $source"

Write-Step 'Publishing the current source'
$bin = Join-Path $WorkRoot 'bin'
dotnet publish (Join-Path $script:RepoRoot 'agent-fleet\agent') -c Release -o $bin -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

$plans = Join-Path $WorkRoot 'plans'
$env:FLEET_CONFIG_PATH = $configCopy
$env:FLEET_PLANS_DIR = $plans
$env:FLEET_FRONTEND_URL = 'http://localhost:3005'
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$previousSettings = $null
try {
    # The settings must be in place when the process starts: the backend reads them once, at startup, from its environment.
    # Names not given are removed here, so a value the launching shell holds is not passed on as if it were a setting.
    $previousSettings = Set-BenchSettingEnvironment $settings
    $startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    # Start-Process passes the argument text on as it is: the quotes keep a folder with a space in its path in one piece.
    $process = Start-Process dotnet -ArgumentList ('"' + (Join-Path $bin 'AgentFleet.dll') + '"') -WorkingDirectory $bin -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $WorkRoot 'backend.out.log') -RedirectStandardError (Join-Path $WorkRoot 'backend.err.log')
}
finally {
    $env:FLEET_CONFIG_PATH = $null; $env:FLEET_PLANS_DIR = $null; $env:FLEET_FRONTEND_URL = $null; $env:ASPNETCORE_URLS = $null
    # The started process keeps the copy it took; this puts back what the shell had, rather than deleting it.
    if ($null -ne $previousSettings) { Restore-BenchSettingEnvironment $previousSettings }
}
$process.Id | Set-Content -LiteralPath $pidFile

Write-Step "Waiting for http://localhost:$Port/health"
$up = $false
$deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
while (-not $up -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    try { $null = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 3; $up = $true }
    catch {
        # With the hub node closed off on purpose the endpoint says unhealthy (503) while the workers are fine: any answer means it is up.
        # Windows PowerShell 5.1 has no -SkipHttpErrorCheck, so an error status is read from the exception instead.
        if ($NoHubModel -and $_.Exception.Response) { $up = $true }
    }
}
if (-not $up) { throw "The benchmark backend did not answer within $HealthTimeoutSeconds s. See $WorkRoot\backend.err.log" }
# The file says which backend it describes: the port it was started on and when, so a batch can tell it is the right one.
$settingsDocument = [ordered]@{ port = $Port; startedAtUtc = $startedAtUtc; settings = $settings }
[IO.File]::WriteAllText((Join-Path $WorkRoot 'backend.settings.json'), (($settingsDocument | ConvertTo-Json -Depth 3) + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
$settingText = ''
if ($settings.Count) { $settingText = ' with ' + (@($settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ') }
Write-Ok "Benchmark backend up on port $Port (pid $($process.Id))$settingText. Run: .\bench\Invoke-Bench.ps1 -BaseUrl http://localhost:$Port"
