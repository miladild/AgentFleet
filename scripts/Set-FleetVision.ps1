<#
.SYNOPSIS
    Shows or changes the mode of the vision machine: off, on demand or always ready.

.DESCRIPTION
    A vision machine reads images. Its mode decides when the fleet loads its model:
    off (nothing contacts the machine; images in a chat get a note that vision is turned off),
    ondemand (the model loads when an image arrives and leaves when the machine is idle; the default),
    always (the model is kept loaded, as before).

    Without -Mode the script only prints the current mode. With -Mode it reads the configuration from the running
    backend, changes that one machine, and saves it back. The change applies at once; no restart is needed.
    The save is refused if the configuration changed since it was read, so nothing is overwritten.

.PARAMETER Mode
    off, ondemand (also written on-demand or on_demand) or always.

.PARAMETER Node
    The vision machine to change. Only needed when the configuration has more than one vision machine.

.PARAMETER BackendUrl
    Where the running backend answers. Defaults to http://localhost:8000.

.EXAMPLE
    .\scripts\Set-FleetVision.ps1

.EXAMPLE
    .\scripts\Set-FleetVision.ps1 -Mode always
#>
[CmdletBinding()]
param(
    [string]$Mode,
    [string]$Node,
    [string]$BackendUrl = 'http://localhost:8000'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"

# --- pure parts (tested by scripts\Test-SetFleetVision.ps1) --------------------------------------

# The mode in any of the spellings the backend accepts, or nothing when the value is not a mode.
function ConvertTo-VisionMode([string]$Raw) {
    $compact = ($Raw -replace '[\s_-]', '').Trim().ToLowerInvariant()
    switch ($compact) {
        'off' { return 'off' }
        'ondemand' { return 'ondemand' }
        'always' { return 'always' }
        default { return $null }
    }
}

# The mode a vision machine runs with. A missing or unknown value means on demand, as the backend reads it.
function Get-NodeVisionMode($Node) {
    $mode = $null
    if ($Node.PSObject.Properties.Name -contains 'visionMode') { $mode = ConvertTo-VisionMode ([string]$Node.visionMode) }
    if ($mode) { return $mode }
    return 'ondemand'
}

# The one vision machine to act on: the named one, or the only one. Throws with the reason when that is not clear.
function Select-VisionNode($Nodes, [string]$Wanted) {
    $visionNodes = @($Nodes | Where-Object { $_.vision -eq $true })
    if ($visionNodes.Count -eq 0) { throw 'The configuration has no vision machine. Add one with Add-FleetNode.ps1 -Vision.' }
    if ($Wanted) {
        $match = @($visionNodes | Where-Object { $_.name -eq $Wanted })
        if ($match.Count -eq 0) { throw "There is no vision machine called '$Wanted'." }
        return $match[0]
    }
    if ($visionNodes.Count -gt 1) {
        $names = ($visionNodes | ForEach-Object { $_.name }) -join ', '
        throw "The configuration has $($visionNodes.Count) vision machines ($names). Pass -Node <name> to say which one to change."
    }
    return $visionNodes[0]
}

# The body for the PUT: every node goes back as it was read, with only the chosen node's mode changed, and the
# version that was read, so the backend refuses the save if anything changed in between.
function New-FleetVisionPutBody($Config, [string]$NodeName, [string]$NewMode) {
    $nodes = foreach ($item in @($Config.nodes)) {
        $copy = [ordered]@{}
        foreach ($property in $item.PSObject.Properties) { $copy[$property.Name] = $property.Value }
        if ($item.name -eq $NodeName) { $copy['visionMode'] = $NewMode }
        $copy
    }
    return (@{ ifVersion = [string]$Config.version; nodes = @($nodes) } | ConvertTo-Json -Depth 20)
}

# --- what the mode means, in one line -------------------------------------------------------------
function Get-VisionModeMeaning([string]$Current) {
    switch ($Current) {
        'off' { return 'Nothing contacts this machine. Images in a chat get a note that vision is turned off.' }
        'always' { return 'The model is kept loaded, as before.' }
        default { return 'The model loads when an image arrives and leaves when the machine is idle.' }
    }
}

# --- the backend ----------------------------------------------------------------------------------
$requested = $null
if ($Mode) {
    $requested = ConvertTo-VisionMode $Mode
    if (-not $requested) { throw "The mode '$Mode' is not one of: off, ondemand (also on-demand), always." }
}

try { $config = Invoke-RestMethod -Uri "$BackendUrl/api/fleet-config" -TimeoutSec 10 }
catch { throw "The backend did not answer at $BackendUrl. Start it, or pass -BackendUrl with its address." }

$target = Select-VisionNode $config.nodes $Node
$current = Get-NodeVisionMode $target

if (-not $requested) {
    Write-Host "Vision machine '$($target.name)' is $current." -ForegroundColor Cyan
    Write-Info (Get-VisionModeMeaning $current)
    exit 0
}

if ($requested -eq $current) {
    Write-Ok "Vision machine '$($target.name)' is already $current. Nothing changed."
    exit 0
}

Write-Step "Setting '$($target.name)' to $requested"
$body = New-FleetVisionPutBody $config $target.name $requested
try { $saved = Invoke-RestMethod -Uri "$BackendUrl/api/fleet-config" -Method Put -Body $body -ContentType 'application/json' -TimeoutSec 15 }
catch {
    $code = $null
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    if ($code -eq 409) { throw 'The configuration changed after it was read (someone saved a change first). Nothing was changed; run the command again.' }
    $detail = $_.Exception.Message
    if ($_.ErrorDetails.Message) {
        try { $parsed = $_.ErrorDetails.Message | ConvertFrom-Json; if ($parsed.reason) { $detail = $parsed.reason } elseif ($parsed.error) { $detail = $parsed.error } }
        catch { }
    }
    throw "The backend refused the change: $detail"
}

# Print what the backend stored, not what was sent.
$confirmed = $requested
$after = @($saved.nodes | Where-Object { $_.name -eq $target.name })
if ($after.Count -gt 0) { $confirmed = Get-NodeVisionMode $after[0] }
Write-Ok "Vision machine '$($target.name)' is now $confirmed."
Write-Info (Get-VisionModeMeaning $confirmed)
