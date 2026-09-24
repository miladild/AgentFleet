# Shared helpers for the scripts in this folder. Dot-source it:  . "$PSScriptRoot\FleetCommon.ps1"
# Works on Windows PowerShell 5.1 and PowerShell 7.

$script:RepoRoot = Split-Path $PSScriptRoot -Parent

function Write-Ok($text)   { Write-Host "  [ ok ] $text" -ForegroundColor Green }
function Write-Warn($text) { Write-Host "  [warn] $text" -ForegroundColor Yellow }
function Write-Bad($text)  { Write-Host "  [FAIL] $text" -ForegroundColor Red }
function Write-Info($text) { Write-Host "         $text" -ForegroundColor DarkGray }
function Write-Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# Asks a yes/no question. -Yes (or a non-interactive session) answers for you with the default.
function Confirm-Action([string]$Question, [bool]$Default = $true, [switch]$Yes) {
    if ($Yes) { return $true }
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) { return $Default }
    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    $answer = Read-Host "$Question [$hint]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim().ToLowerInvariant().StartsWith('y')
}

function Test-Command([string]$Name) { return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

# Where the installed copy lives (Install-Autostart.ps1 publishes the backend to <root>\backend). In order: an
# explicit -InstallRoot, the AGENT_FLEET_INSTALL_ROOT environment variable, the folder the registered backend
# service or scheduled task runs from, and C:\AgentFleet when nothing is installed yet.
function Resolve-InstallRoot([string]$Given, [string]$BackendName = 'AgentFleetBackend') {
    if ($Given) { return $Given }
    if ($env:AGENT_FLEET_INSTALL_ROOT) { return $env:AGENT_FLEET_INSTALL_ROOT }
    $backendDir = $null
    try {
        $service = Get-CimInstance Win32_Service -Filter "Name='$BackendName'" -ErrorAction Stop
        if ($service -and $service.PathName -match '^\s*"([^"]+)"') { $backendDir = Split-Path $Matches[1] -Parent }
        elseif ($service -and $service.PathName -match '^\s*(\S+)') { $backendDir = Split-Path $Matches[1] -Parent }
    } catch { }
    if (-not $backendDir) {
        try {
            $task = Get-ScheduledTask -TaskName $BackendName -ErrorAction Stop
            foreach ($action in $task.Actions) {
                if ($action.Arguments -match "-LiteralPath '([^']+)'") { $backendDir = $Matches[1]; break }
            }
        } catch { }
    }
    if ($backendDir -and (Split-Path $backendDir -Leaf) -eq 'backend') { return (Split-Path $backendDir -Parent) }
    return 'C:\AgentFleet'
}

# The fleet's own configuration file. Looked for, in order: an explicit path, FLEET_CONFIG_PATH,
# the file used by `npm run dev` (agent-fleet\fleet.config.json), and the installed copy's
# <InstallRoot>\backend\fleet.config.json.
function Find-FleetConfig([string]$Path, [string]$InstallRoot) {
    $InstallRoot = Resolve-InstallRoot $InstallRoot
    $candidates = @()
    if ($Path) { $candidates += $Path }
    if ($env:FLEET_CONFIG_PATH) { $candidates += $env:FLEET_CONFIG_PATH }
    $candidates += (Join-Path $script:RepoRoot 'agent-fleet\fleet.config.json')
    $candidates += (Join-Path $InstallRoot 'backend\fleet.config.json')
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}

function Read-FleetConfig([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

# JSON without a byte-order mark, which is what the backend writes too.
function Write-FleetConfig([string]$Path, $Config) {
    $json = $Config | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
}

# A node's chat URL ends in /v1; Ollama's own API (model list, pull) lives at the address without it.
function Get-OllamaBase([string]$NodeUrl) {
    return ($NodeUrl.TrimEnd('/') -replace '/v1$', '')
}

# Asks a machine's Ollama what is installed. Returns $null when it cannot be reached.
function Get-OllamaModels([string]$NodeUrl, [int]$TimeoutSec = 5) {
    try {
        $tags = Invoke-RestMethod -Uri ((Get-OllamaBase $NodeUrl) + '/api/tags') -TimeoutSec $TimeoutSec
        return @($tags.models)
    } catch { return $null }
}

# Ollama reports names in full ("llama3.2:latest") even when you asked for "llama3.2".
function Test-ModelPresent($Models, [string]$Wanted) {
    foreach ($m in $Models) {
        if ($m.name -eq $Wanted -or $m.name -eq "$Wanted`:latest") { return $true }
    }
    return $false
}

# This machine's LAN addresses, for telling the user what to type on the hub.
function Get-LocalAddresses {
    try {
        return @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
            ForEach-Object { $_.IPAddress })
    } catch { return @() }
}

# Pulls a model through a machine's Ollama API, printing progress. Works on a remote machine too,
# which is how the hub can prepare a worker without logging into it.
function Invoke-OllamaPull([string]$NodeUrl, [string]$Model) {
    $uri = (Get-OllamaBase $NodeUrl) + '/api/pull'
    $body = @{ name = $Model; stream = $true } | ConvertTo-Json
    $request = [Net.HttpWebRequest]::Create($uri)
    $request.Method = 'POST'
    $request.ContentType = 'application/json'
    $request.Timeout = 3600000
    $request.ReadWriteTimeout = 600000
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $stream = $request.GetRequestStream(); $stream.Write($bytes, 0, $bytes.Length); $stream.Close()
    $reader = New-Object IO.StreamReader($request.GetResponse().GetResponseStream())
    $lastStatus = ''
    $lastPercent = -10
    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine()
        if (-not $line) { continue }
        $event = $line | ConvertFrom-Json
        if ($event.error) { $reader.Close(); throw "Ollama said: $($event.error)" }
        if ($event.total -and $event.completed) {
            $percent = [int](100 * $event.completed / $event.total)
            if ($percent -ge $lastPercent + 10) { Write-Info "$($event.status): $percent%"; $lastPercent = $percent }
        } elseif ($event.status -and $event.status -ne $lastStatus) {
            Write-Info $event.status; $lastStatus = $event.status; $lastPercent = -10
        }
    }
    $reader.Close()
}

$script:NodeNamePattern = '^[a-z0-9][a-z0-9-]{0,31}$'
$script:Tiers = @('heavy', 'standard', 'light')
