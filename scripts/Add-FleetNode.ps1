<#
.SYNOPSIS
    Adds a machine (a node running Ollama) to the fleet.

.DESCRIPTION
    Checks that the machine's Ollama answers, optionally downloads the model onto it over the network,
    and adds it to fleet.config.json (or, when the backend is running, through the backend, which checks
    the change itself). A running backend applies the change at once; one that is stopped reads it when it starts.

    Set the machine up first with Setup-Worker.ps1 (Windows) or setup-worker.sh (Linux), or install
    Ollama there yourself and make it listen on the network.

.PARAMETER Name
    A short name: lowercase letters, digits and hyphens, for example worker1.

.PARAMETER Address
    The machine's address as Ollama prints it, for example http://192.168.1.21:11434. The /v1 that the
    backend needs is added for you. A bare host or IP also works.

.PARAMETER Model
    The model that machine should serve, for example qwen2.5-coder:7b.

.PARAMETER Tier
    heavy (hard work, planning), standard (ordinary work, plan steps) or light (quick, trivial). Ignored
    for a vision node.

.PARAMETER Vision
    This machine reads images. It is used only when a message contains an image.

.PARAMETER Pull
    Download the model onto the machine through its Ollama if it is not there yet.

.PARAMETER Fallback
    Make this the machine that answers when routing fails or another machine is down. Only one node can
    be the fallback; this moves the role.

.EXAMPLE
    .\scripts\Add-FleetNode.ps1 -Name worker1 -Address 192.168.1.21 -Model qwen2.5-coder:7b -Tier standard -Pull
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Address,
    [Parameter(Mandatory)][string]$Model,
    [ValidateSet('heavy', 'standard', 'light')][string]$Tier = 'standard',
    [switch]$Vision,
    [switch]$Fallback,
    [switch]$Pull,
    [string]$Purpose,
    [string]$ConfigPath,
    [string]$BackendUrl = 'http://localhost:8000',
    [string]$InstallRoot,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$InstallRoot = Resolve-InstallRoot $InstallRoot

# --- the address --------------------------------------------------------------------------------
$Name = $Name.Trim().ToLowerInvariant()
if ($Name -notmatch $script:NodeNamePattern) { throw "The name '$Name' is not valid: use 1 to 32 lowercase letters, digits or hyphens." }

$url = $Address.Trim()
if ($url -notmatch '^https?://') { $url = "http://$url" }
$uri = [Uri]$url
if (-not $uri.IsAbsoluteUri) { throw "'$Address' is not an address." }
if ($uri.IsDefaultPort -and $url -notmatch ':\d+') { $url = "$($uri.Scheme)://$($uri.Host):11434" }
$url = $url.TrimEnd('/') -replace '/v1$', ''
$nodeUrl = "$url/v1"

# --- is it there? -------------------------------------------------------------------------------
Write-Step "Checking $url"
$models = Get-OllamaModels $nodeUrl 8
if ($null -eq $models) {
    Write-Bad "Nothing answered at $url/api/tags."
    Write-Info 'On that machine, Ollama must be running and listening on the network (OLLAMA_HOST=0.0.0.0), and its firewall must let this machine in.'
    Write-Info 'Setup-Worker.ps1 (Windows) and setup-worker.sh (Linux) do this for you. See docs\adding-machines.md.'
    exit 1
}
Write-Ok "Ollama answers ($($models.Count) model(s) installed)"

if (-not (Test-ModelPresent $models $Model)) {
    if ($Pull -or (Confirm-Action "The model '$Model' is not on that machine. Download it there now?" $true -Yes:$Yes)) {
        Write-Step "Downloading $Model onto $($uri.Host) (this can take a while)"
        Invoke-OllamaPull $nodeUrl $Model
        Write-Ok "$Model is installed"
    } else {
        Write-Warn "Continuing without the model. The node shows as not ready until you run: ollama pull $Model (on that machine)"
    }
} else { Write-Ok "$Model is installed there" }

# --- build the node -----------------------------------------------------------------------------
if (-not $Purpose) {
    $Purpose = if ($Vision) { 'Reads screenshots and images.' } else { switch ($Tier) { 'heavy' { 'Hard coding work and planning.' } 'light' { 'Quick questions and trivial edits.' } default { 'Ordinary coding work and plan steps.' } } }
}
$node = [ordered]@{ name = $Name; url = $nodeUrl; model = $Model; purpose = $Purpose }
if ($Vision) { $node.vision = $true } else { $node.tier = $Tier }
if ($Fallback -and -not $Vision) { $node.fallback = $true }

# --- apply it ----------------------------------------------------------------------------------
$viaBackend = $false
try { $null = Invoke-RestMethod -Uri "$BackendUrl/health" -TimeoutSec 3 -ErrorAction Stop; $viaBackend = $true }
catch { if ($_.ErrorDetails.Message) { $viaBackend = $true } }

if ($viaBackend) {
    Write-Step 'Adding it through the running backend (it validates the change)'
    $current = Invoke-RestMethod -Uri "$BackendUrl/api/fleet-config"
    $nodes = @($current.nodes | ForEach-Object {
        $o = [ordered]@{ name = $_.name; url = $_.url; model = $_.model; purpose = $_.purpose; tier = $_.tier; vision = [bool]$_.vision; fallback = [bool]$_.fallback }
        if ($Fallback -and -not $Vision) { $o.fallback = $false }
        $o
    })
    if ($nodes | Where-Object { $_.name -eq $Name }) { throw "A node called '$Name' already exists. Edit it in the web UI's Config panel or pick another name." }
    $newNode = [ordered]@{ name = $Name; url = $nodeUrl; model = $Model; purpose = $Purpose; tier = $(if ($Vision) { $null } else { $Tier }); vision = [bool]$Vision; fallback = [bool]($Fallback -and -not $Vision) }
    $body = @{ nodes = @($nodes + $newNode) } | ConvertTo-Json -Depth 8
    try { $saveResult = Invoke-RestMethod -Uri "$BackendUrl/api/fleet-config" -Method Put -Body $body -ContentType 'application/json' }
    catch {
        $detail = if ($_.ErrorDetails.Message) { ($_.ErrorDetails.Message | ConvertFrom-Json).error } else { $_.Exception.Message }
        throw "The backend refused the change: $detail"
    }
    Write-Ok "Saved by the backend to its fleet.config.json"
    # Current backends apply machine changes on save; older ones read machines only at startup.
    $appliedLive = [bool]($saveResult.PSObject.Properties.Name -contains 'restartRequired' -and -not $saveResult.restartRequired)
} else {
    $path = Find-FleetConfig -Path $ConfigPath -InstallRoot $InstallRoot
    if (-not $path) { throw 'The backend is not running and no fleet.config.json was found. Run Setup-Hub.ps1 first, or start the backend once so it creates one, or pass -ConfigPath.' }
    Write-Step "Editing $path (the backend is not running)"
    $config = Read-FleetConfig $path
    $existing = @($config.nodes)
    if ($existing | Where-Object { $_.name -eq $Name }) { throw "A node called '$Name' already exists in that file." }
    if ($Fallback -and -not $Vision) { foreach ($e in $existing) { if ($e.PSObject.Properties.Name -contains 'fallback') { $e.fallback = $false } } }
    Copy-Item -LiteralPath $path -Destination "$path.bak" -Force
    $config.nodes = @($existing + [pscustomobject]$node)
    if (@($config.nodes | Where-Object { $_.fallback }).Count -gt 1) { throw 'That would leave two fallback nodes.' }
    Write-FleetConfig $path $config
    Write-Ok "Saved (previous version kept as $path.bak)"
}

# --- the routing model must be on the fallback node ---------------------------------------------
$config2 = if ($viaBackend) { Invoke-RestMethod -Uri "$BackendUrl/api/fleet-config" } else { Read-FleetConfig (Find-FleetConfig -Path $ConfigPath -InstallRoot $InstallRoot) }
$textNodes = @($config2.nodes | Where-Object { -not $_.vision })
if ($textNodes.Count -gt 1) {
    $fb = $config2.nodes | Where-Object { $_.fallback } | Select-Object -First 1
    $fbModels = Get-OllamaModels $fb.url 5
    if ($null -ne $fbModels -and -not (Test-ModelPresent $fbModels $config2.triageModel)) {
        Write-Warn "With more than one machine the fleet routes each message with a small model ('$($config2.triageModel)') on '$($fb.name)', and it is not installed there."
        if (Confirm-Action "Download '$($config2.triageModel)' onto '$($fb.name)' now?" $true -Yes:$Yes) { Invoke-OllamaPull $fb.url $config2.triageModel; Write-Ok 'Installed' }
    }
}

if ($viaBackend -and $appliedLive) {
    Write-Host "`nAdded '$Name'. The running backend is using it already." -ForegroundColor Green
} else {
    Write-Host "`nAdded '$Name'. Restart the backend so it picks the change up:" -ForegroundColor Green
    Write-Host '  - as a service:   Restart-Service AgentFleetBackend    (elevated PowerShell)'
    Write-Host '  - from a terminal: stop it with Ctrl+C and start it again (npm run dev)'
}
Write-Host 'Then check everything with: .\scripts\Test-Fleet.ps1'
