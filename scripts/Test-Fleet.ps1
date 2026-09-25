<#
.SYNOPSIS
    Checks that this machine (the hub) is set up correctly and says what to fix.

.DESCRIPTION
    Looks at the tools that are installed, the fleet configuration, every machine in it (reachable,
    model installed), the running backend and web UI, and the VS Code extension. Changes nothing.
    Exits with 1 if anything failed, 0 otherwise (warnings do not fail it).

.PARAMETER ConfigPath
    The fleet.config.json to check. By default it is found the same way the other scripts find it.

.PARAMETER BackendUrl
    Where the backend is expected to be running.

.PARAMETER FrontendUrl
    Where the web UI is expected to be running.

.EXAMPLE
    .\scripts\Test-Fleet.ps1
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$BackendUrl = 'http://localhost:8000',
    [string]$FrontendUrl = 'http://localhost:3000',
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$InstallRoot = Resolve-InstallRoot $InstallRoot
$failures = 0
$warnings = 0
function Bad($t)  { $script:failures++; Write-Bad $t }
function Warn($t) { $script:warnings++; Write-Warn $t }

Write-Step 'Tools on this machine'
if (Test-Path (Join-Path $script:RepoRoot 'web\server.js')) {
    Write-Ok 'Release download: the backend brings its own .NET runtime'
} elseif (Test-Command dotnet) {
    $sdks = @(dotnet --list-sdks 2>$null)
    $major = ($sdks | ForEach-Object { [int](($_ -split '\.')[0]) } | Sort-Object -Descending | Select-Object -First 1)
    if ($major -ge 9) { Write-Ok ".NET SDK $major (need 9 or newer)" } else { Bad ".NET SDK 9 or newer is required (found: $($sdks -join ', ')). Install: winget install Microsoft.DotNet.SDK.9" }
} else { Bad '.NET SDK not found. Install: winget install Microsoft.DotNet.SDK.9' }

if (Test-Command node) {
    $nodeMajor = [int]((node --version) -replace '^v(\d+)\..*', '$1')
    if ($nodeMajor -ge 20) { Write-Ok "Node.js $(node --version) (need 20 or newer)" } else { Bad "Node.js 20 or newer is required (found $(node --version)). Install: winget install OpenJS.NodeJS.LTS" }
} else { Bad 'Node.js not found. Install: winget install OpenJS.NodeJS.LTS' }

if (Test-Command ollama) { Write-Ok 'Ollama is installed' } else { Warn 'Ollama is not installed on this machine. Fine if the hub only talks to other machines, otherwise: winget install Ollama.Ollama' }
if (Test-Command git)    { Write-Ok 'git is installed' } else { Warn 'git not found. The run_git_command tool needs it.' }
if (Test-Command docker) { Write-Ok 'Docker is installed (run_sandboxed_code can use it)' } else { Write-Info 'Docker not found: the sandbox tool will be off unless you point it at another machine. Optional.' }

Write-Step 'Fleet configuration'
$config = $null
$path = Find-FleetConfig -Path $ConfigPath -InstallRoot $InstallRoot
if (-not $path) {
    Warn 'No fleet.config.json found yet. The backend creates one on first start; or copy agent-fleet\fleet.config.example.json and edit it.'
} else {
    Write-Ok "Found $path"
    try { $config = Read-FleetConfig $path } catch { Bad "That file is not valid JSON: $($_.Exception.Message)" }
}

if ($config) {
    $nodes = @($config.nodes)
    $fallbacks = @($nodes | Where-Object { $_.fallback })
    if ($nodes.Count -eq 0) { Bad 'The config has no nodes.' }
    if ($fallbacks.Count -ne 1) { Bad "Exactly one node must have ""fallback"": true (found $($fallbacks.Count))." }
    $names = @($nodes | ForEach-Object { $_.name })
    if (($names | Sort-Object -Unique).Count -ne $names.Count) { Bad 'Two nodes share a name.' }
    foreach ($n in $nodes) {
        if ($n.name -notmatch $script:NodeNamePattern) { Bad "Node name '$($n.name)' must be 1 to 32 lowercase letters, digits or hyphens." }
        if ($n.url -notmatch '/v1/?$') { Bad "Node '$($n.name)': the url must end in /v1 (found $($n.url))." }
    }

    Write-Step 'Machines'
    $fallback = $fallbacks | Select-Object -First 1
    foreach ($n in $nodes) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $models = Get-OllamaModels $n.url 5
        $ms = [int]$sw.Elapsed.TotalMilliseconds
        $role = if ($n.vision) { 'vision' } else { $n.tier }
        if ($null -eq $models) {
            Bad "$($n.name) ($role) at $($n.url) is not reachable."
            Write-Info 'Check that Ollama runs there and listens on the network (OLLAMA_HOST=0.0.0.0), and that its firewall lets this machine in. See docs\adding-machines.md.'
            continue
        }
        if (Test-ModelPresent $models $n.model) { Write-Ok "$($n.name) ($role): $($n.model) is installed ($ms ms)" }
        else { Bad "$($n.name) ($role): model '$($n.model)' is not installed there. On that machine: ollama pull $($n.model)" }
    }

    $textNodes = @($nodes | Where-Object { -not $_.vision })
    if ($textNodes.Count -gt 1 -and $fallback) {
        $models = Get-OllamaModels $fallback.url 5
        if ($null -ne $models -and -not (Test-ModelPresent $models $config.triageModel)) {
            Bad "The triage model '$($config.triageModel)' is not installed on the fallback node '$($fallback.name)', which does the routing. Run: ollama pull $($config.triageModel)"
        } elseif ($null -ne $models) { Write-Ok "Triage model '$($config.triageModel)' is installed on '$($fallback.name)'" }
    }

    # Which of this machine's addresses reaches each remote node: if it changes, a worker's
    # firewall rule that names the old address stops working (the classic "goes red at night").
    $sources = @()
    foreach ($n in $nodes) {
        $uri = [Uri]$n.url
        if ($uri.Host -in @('localhost', '127.0.0.1', '::1')) { continue }
        try {
            $route = Find-NetRoute -RemoteIPAddress ([Net.Dns]::GetHostAddresses($uri.Host)[0].IPAddressToString) -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
            if ($route) { $sources += $route.IPAddress }
        } catch { }
    }
    $sources = @($sources | Sort-Object -Unique)
    if ($sources.Count -gt 0) {
        Write-Info "This machine reaches the others from $($sources -join ', '). If a worker's firewall allows only that address, give this machine a fixed address (a DHCP reservation on your router) or the worker will drop it when the address changes."
    }
}

Write-Step 'Running services'
$health = $null
try { $health = Invoke-RestMethod -Uri "$BackendUrl/health" -TimeoutSec 5 }
catch {
    if ($_.ErrorDetails.Message) { try { $health = $_.ErrorDetails.Message | ConvertFrom-Json } catch { } }
}
if ($health) {
    Write-Ok "Backend answers at $BackendUrl (overall: $($health.status))"
    foreach ($n in @($health.nodes)) {
        if ($n.ready) { Write-Ok "backend sees $($n.name) as ready" } else { Warn "backend sees $($n.name) as NOT ready ($($n.reason))" }
    }
    try {
        # A JSON array can arrive as one object; ForEach-Object unrolls it so the count is right.
        $contexts = @(Invoke-RestMethod -Uri "$BackendUrl/api/contexts" -TimeoutSec 8 | ForEach-Object { $_ })
        Write-Ok "durable record answers ($($contexts.Count) conversation context(s))"
        try {
            $record = Invoke-RestMethod -Uri "$BackendUrl/api/contexts/storage" -TimeoutSec 8
            $mb = [Math]::Round($record.storage.bytes / 1MB, 1)
            $keep = if ($record.deleteAfterDays -gt 0) { "chats not used for $($record.deleteAfterDays) days are deleted" } else { 'every chat is kept' }
            if ($mb -ge 500 -and $record.deleteAfterDays -eq 0) {
                Warn "The durable record is $mb MB and $keep. Delete old chats in the web UI under Config > History."
            } else {
                Write-Ok "durable record is $mb MB; $keep (Config > History)"
            }
        } catch {
            Warn 'The backend has no history settings (/api/contexts/storage): it is an older build. Run .\scripts\Deploy-Fleet.ps1 to update it.'
        }
    } catch {
        Warn 'The backend has no durable record endpoint (/api/contexts): it is an older build. Run .\scripts\Deploy-Fleet.ps1 to update it.'
    }
} else {
    Warn "The backend is not answering at $BackendUrl. Start it with: cd agent-fleet; npm run dev   (or start the AgentFleetBackend service)"
}

try { $null = Invoke-WebRequest -Uri $FrontendUrl -UseBasicParsing -TimeoutSec 8; Write-Ok "Web UI answers at $FrontendUrl" }
catch { Warn "The web UI is not answering at $FrontendUrl (start it with: cd agent-fleet; npm run dev)" }

Write-Step 'VS Code extension'
$foundExtension = $false
$checkoutVersion = $null
$extensionManifest = Join-Path $script:RepoRoot 'vscode-fleet\package.json'
if (Test-Path $extensionManifest) { $checkoutVersion = (Get-Content $extensionManifest -Raw | ConvertFrom-Json).version }
foreach ($cli in 'code', 'code-insiders') {
    if (Test-Command $cli) {
        $listed = @(& $cli --list-extensions --show-versions 2>$null | Where-Object { $_ -like 'local.agent-fleet-chat*' })
        if ($listed.Count -gt 0) {
            $foundExtension = $true
            $installed = ($listed[0] -split '@')[-1]
            $older = $false
            try { $older = $checkoutVersion -and ([version]$installed -lt [version]$checkoutVersion) } catch { }
            if ($older) { Warn "$cli has $($listed[0]), older than this checkout's $checkoutVersion. Update it with: .\scripts\Install-VSCodeExtension.ps1" }
            else { Write-Ok "$cli has $($listed[0])" }
        }
        else { Write-Info "$cli does not have the @fleet extension. Install it with: .\scripts\Install-VSCodeExtension.ps1" }
    }
}
if (-not $foundExtension) { Warn 'The @fleet VS Code extension is not installed (optional).' }

Write-Host ''
if ($failures -gt 0) { Write-Host "$failures problem(s), $warnings warning(s)." -ForegroundColor Red; exit 1 }
Write-Host "No problems found ($warnings warning(s))." -ForegroundColor Green
exit 0
