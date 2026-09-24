<#
.SYNOPSIS
    Prepares a Windows machine to be a worker in the fleet: Ollama installed, listening on the network,
    and reachable only from where you say.

.DESCRIPTION
    Run this on the WORKER machine (not the hub), in an elevated PowerShell. It:
      1. installs Ollama with winget if it is missing,
      2. makes Ollama listen on the network (sets OLLAMA_HOST for the machine),
      3. adds a firewall rule that lets only the named private peer addresses reach Ollama's port,
      4. narrows Ollama's own inbound rules to those same addresses,
      5. optionally stops the machine from sleeping.
    It does NOT download a model: the hub can do that over the network (Add-FleetNode.ps1 -Pull), and the
    command to run is printed at the end.

    Ollama has no login of its own, so anyone who can reach its port can use the machine. Keep it on your
    LAN, never port-forward it, and use only the hub's private IPv4 address in -AllowFrom.

.PARAMETER AllowFrom
    Required: one or more known private IPv4 peer addresses, separated by commas. CIDR networks, hostnames,
    wildcards, and LocalSubnet are rejected so the port is not opened to the whole LAN.

.PARAMETER RestrictOllamaRules
    Compatibility switch. Ollama's inbound rules are now always narrowed to the requested peer IPs.

.PARAMETER KeepAwake
    Stop the machine from sleeping while plugged in. A worker that sleeps shows up red in the fleet.

.PARAMETER DryRun
    Print what would be done and change nothing.

.EXAMPLE
    .\Setup-Worker.ps1 -AllowFrom 192.168.1.10
#>
[CmdletBinding()]
param(
    [string]$AllowFrom,
    [ValidateRange(1, 65535)][int]$Port = 11434,
    [switch]$RestrictOllamaRules,
    [switch]$KeepAwake,
    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DryRun) { throw 'Run this in an elevated PowerShell (Run as administrator), or add -DryRun to see what it would do.' }

function Do-Step([string]$What, [scriptblock]$Block) {
    if ($DryRun) { Write-Info "would: $What"; return }
    & $Block
}

$remote = @($AllowFrom -split '[,\s]+' | Where-Object { $_ })
if ($remote.Count -eq 0) { throw 'Pass -AllowFrom with the hub IP, for example -AllowFrom 192.168.1.10.' }

function Test-PrivateIPv4([string]$Address) {
    $parts = $Address.Split('.')
    if ($parts.Count -ne 4) { return $false }
    $octets = @()
    foreach ($part in $parts) {
        if ($part -notmatch '^(0|[1-9][0-9]{0,2})$') { return $false }
        $value = [int]$part
        if ($value -gt 255) { return $false }
        $octets += $value
    }
    return ($octets[0] -eq 10) -or
        ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
        ($octets[0] -eq 192 -and $octets[1] -eq 168)
}

foreach ($address in $remote) {
    if (-not (Test-PrivateIPv4 $address)) {
        throw "-AllowFrom accepts only a literal RFC1918 private IPv4 address (no CIDR, hostname, wildcard, or LocalSubnet): '$address'."
    }
}

# The listener has no authentication. Require Windows Firewall to be enabled
# with a block-by-default inbound policy before recording a network bind.
$ruleName = 'Agent Fleet - Ollama from the hub'
$requestedPeers = @($remote | Sort-Object -Unique)
$firewallProfiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
if ($firewallProfiles.Count -eq 0) { throw 'Could not read the active Windows Firewall profiles; refusing to expose Ollama.' }
$disabledProfiles = @($firewallProfiles | Where-Object { $_.Enabled -ne $true })
if ($disabledProfiles.Count -gt 0) {
    throw "Windows Firewall is disabled for: $($disabledProfiles.Name -join ', '). Enable it before exposing Ollama."
}
$permissiveProfiles = @($firewallProfiles | Where-Object { $_.DefaultInboundAction -ne 'Block' })
if ($permissiveProfiles.Count -gt 0) {
    throw "Windows Firewall does not block inbound traffic by default for: $($permissiveProfiles.Name -join ', '). Set those profiles to block before exposing Ollama."
}

# A separate broad port rule could bypass the Ollama application rules below.
# Stop for review rather than changing unrelated firewall policy automatically.
$broadPortRules = @()
$enabledInboundRules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True -ErrorAction Stop)
foreach ($firewallRule in $enabledInboundRules) {
    if ($firewallRule.DisplayName -eq $ruleName) { continue }
    if ($firewallRule.PackageFamilyName) { continue }
    $applicationFilter = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $firewallRule -ErrorAction SilentlyContinue
    $program = [string]$applicationFilter.Program
    if ($program -and $program -ne 'Any') { continue }

    $portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $firewallRule -ErrorAction SilentlyContinue
    $protocol = [string]$portFilter.Protocol
    if ($protocol -notin @('Any', 'TCP', '6')) { continue }
    $coversOllamaPort = $false
    foreach ($localPort in @($portFilter.LocalPort)) {
        $localPortText = [string]$localPort
        if ($localPortText -eq 'Any' -or $localPortText -eq [string]$Port) { $coversOllamaPort = $true; break }
        if ($localPortText -match '^(\d+)-(\d+)$' -and $Port -ge [int]$Matches[1] -and $Port -le [int]$Matches[2]) { $coversOllamaPort = $true; break }
    }
    if (-not $coversOllamaPort) { continue }

    $addressFilter = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $firewallRule -ErrorAction SilentlyContinue
    $rulePeers = @($addressFilter.RemoteAddress | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    if (($rulePeers -join ',') -ne ($requestedPeers -join ',')) { $broadPortRules += $firewallRule.DisplayName }
}
if ($broadPortRules.Count -gt 0) {
    throw "Inbound firewall rules can allow port $Port from addresses other than the requested peers: $($broadPortRules -join ', '). Review or narrow them before exposing Ollama."
}

# --- 1. Ollama ----------------------------------------------------------------------------------
Write-Step 'Ollama'
if (Test-Command ollama) { Write-Ok "Installed: $(ollama --version 2>&1 | Select-Object -First 1)" }
elseif (Test-Command winget) {
    if (Confirm-Action 'Ollama is not installed. Install it with winget now?' $true -Yes:$Yes) {
        Do-Step 'winget install Ollama.Ollama' { winget install --id Ollama.Ollama -e --accept-package-agreements --accept-source-agreements; if ($LASTEXITCODE) { throw 'winget could not install Ollama.' } }
    } else { throw 'Ollama is required.' }
} else { throw 'Ollama is not installed and winget is not available. Install Ollama from https://ollama.com/download and run this again.' }

# --- 2. listen on the network -------------------------------------------------------------------
Write-Step 'Making Ollama listen on the network'
$listen = "0.0.0.0:$Port"
$current = [Environment]::GetEnvironmentVariable('OLLAMA_HOST', 'Machine')
if ($current -eq $listen) { Write-Ok "OLLAMA_HOST is already $listen" }
else {
    Do-Step "set the machine-wide OLLAMA_HOST to $listen (was: $current)" { [Environment]::SetEnvironmentVariable('OLLAMA_HOST', $listen, 'Machine') }
    Write-Ok "OLLAMA_HOST = $listen"
}

# --- 3. the firewall rule -----------------------------------------------------------------------
Write-Step "Firewall: allow $($remote -join ', ') to reach port $Port"
Do-Step "create the inbound rule '$ruleName' (TCP $Port from $($remote -join ', '))" {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $null = New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -RemoteAddress $remote -Profile Any
}
Write-Ok "Rule '$ruleName' in place"

# --- 4. rules Ollama's installer made -----------------------------------------------------------
Write-Step "Rules Ollama's own installer created"
$installerRules = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue |
    Where-Object { $_.Program -and $_.Program -like '*ollama*' } |
    ForEach-Object { Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' -and $_.Enabled -eq 'True' -and $_.DisplayName -ne $ruleName })
if ($installerRules.Count -eq 0) { Write-Ok 'None found.' }
foreach ($rule in $installerRules) {
    $addr = (Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule).RemoteAddress
    Write-Info "'$($rule.DisplayName)' allows: $($addr -join ', ')"
    $actual = @($addr | Sort-Object -Unique) -join ','
    $requested = @($remote | Sort-Object -Unique) -join ','
    if ($actual -ne $requested) {
        Do-Step "set '$($rule.DisplayName)' to $($remote -join ', ')" { Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule | Set-NetFirewallAddressFilter -RemoteAddress $remote }
        Write-Ok 'Restricted to the requested peer IPs'
    }
}

# --- 5. sleep -----------------------------------------------------------------------------------
if ($KeepAwake) {
    Write-Step 'Sleep'
    Do-Step 'set the machine to never sleep while plugged in' { powercfg /change standby-timeout-ac 0; powercfg /change hibernate-timeout-ac 0 }
    Write-Ok 'Will not sleep while plugged in'
}

# --- 6. check ----------------------------------------------------------------------------------
Write-Step 'Check'
$addresses = Get-LocalAddresses
if ($DryRun) { Write-Info 'dry run: nothing was changed.' }
else {
    $reachable = $false
    foreach ($a in $addresses) { if ($null -ne (Get-OllamaModels "http://${a}:$Port" 3)) { $reachable = $true; Write-Ok "Ollama answers on http://${a}:$Port"; break } }
    if (-not $reachable) {
        Write-Warn 'Ollama does not answer on the network address yet. It has to be restarted to pick up OLLAMA_HOST:'
        Write-Info 'quit Ollama from the tray icon (bottom right), open it again from the Start menu, then run:  Invoke-RestMethod http://localhost:11434/api/tags'
        Write-Info 'or simply restart this computer.'
    }
}

Write-Host "`nNext, on the HUB (replace worker1 and the model with your choices):" -ForegroundColor Green
$shown = if ($addresses.Count -gt 0) { $addresses[0] } else { '<this-machine-address>' }
Write-Host "  .\scripts\Add-FleetNode.ps1 -Name worker1 -Address $shown -Model qwen2.5-coder:7b -Tier standard -Pull"
if ($addresses.Count -gt 1) { Write-Info "This machine has more than one address: $($addresses -join ', '). Use the one on the same network as the hub." }
Write-Info 'A fixed address for this machine (a DHCP reservation on your router) avoids surprises later.'
