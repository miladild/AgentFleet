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

.PARAMETER WorkRoot
    Where the published backend, the copied configuration, the plans and the logs live.
#>
[CmdletBinding()]
param(
    [int]$Port = 8010,
    [string]$Config,
    [string]$InstallRoot,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) 'agent-fleet-bench'),
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\FleetCommon.ps1"
$pidFile = Join-Path $WorkRoot 'backend.pid'

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
    Write-Ok "The benchmark backend on port $Port is stopped."
    return
}

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
Stop-BenchBackend
Start-Sleep -Seconds 1

$source = Find-FleetConfig $Config $InstallRoot
if (-not $source) { throw 'No fleet.config.json found. Pass -Config <path>.' }
$config = Read-FleetConfig $source
if ($config.PSObject.Properties.Name -contains 'notifyUrl') { $config.PSObject.Properties.Remove('notifyUrl') }
$configCopy = Join-Path $WorkRoot 'fleet.config.json'
Write-FleetConfig $configCopy $config
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
try {
    $process = Start-Process dotnet -ArgumentList (Join-Path $bin 'AgentFleet.dll') -WorkingDirectory $bin -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $WorkRoot 'backend.out.log') -RedirectStandardError (Join-Path $WorkRoot 'backend.err.log')
}
finally {
    $env:FLEET_CONFIG_PATH = $null; $env:FLEET_PLANS_DIR = $null; $env:FLEET_FRONTEND_URL = $null; $env:ASPNETCORE_URLS = $null
}
$process.Id | Set-Content -LiteralPath $pidFile

Write-Step "Waiting for http://localhost:$Port/health"
$up = $false
for ($i = 0; $i -lt 90 -and -not $up; $i++) {
    Start-Sleep -Seconds 1
    try { $null = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 3; $up = $true } catch { }
}
if (-not $up) { throw "The benchmark backend did not answer. See $WorkRoot\backend.err.log" }
Write-Ok "Benchmark backend up on port $Port (pid $($process.Id)). Run: .\bench\Invoke-Bench.ps1 -BaseUrl http://localhost:$Port"
