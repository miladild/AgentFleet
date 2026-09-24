<#
.SYNOPSIS
    Lists every model installed on every machine in the fleet, grouped by machine.

.DESCRIPTION
    Ollama's own model picker only ever talks to one server, so there is no built-in way to see what is
    installed across several machines. This asks each machine's Ollama directly. The machines come from
    fleet.config.json (found the same way the other scripts find it), or pass -Address for machines that are not
    in it yet.

.EXAMPLE
    .\scripts\Get-FleetModels.ps1
.EXAMPLE
    .\scripts\Get-FleetModels.ps1 -Address http://192.168.1.30:11434
#>
[CmdletBinding()]
param(
    [string[]]$Address = @(),
    [string]$ConfigPath,
    [string]$InstallRoot
)

$ErrorActionPreference = 'SilentlyContinue'
. "$PSScriptRoot\FleetCommon.ps1"
$InstallRoot = Resolve-InstallRoot $InstallRoot

$machines = @()
$path = Find-FleetConfig -Path $ConfigPath -InstallRoot $InstallRoot
if ($path) {
    foreach ($node in @((Read-FleetConfig $path).nodes)) {
        $machines += [pscustomobject]@{ Name = $node.name; Url = (Get-OllamaBase $node.url); Wants = $node.model }
    }
}
foreach ($a in $Address) { $machines += [pscustomobject]@{ Name = $a; Url = (Get-OllamaBase $a); Wants = $null } }
if ($machines.Count -eq 0) { Write-Host 'No fleet.config.json found and no -Address given.'; exit 1 }

# Several nodes can share one machine (a text node and a vision node, for example): ask each address once.
$byUrl = $machines | Group-Object Url
foreach ($group in $byUrl) {
    $names = ($group.Group | ForEach-Object { $_.Name }) -join ', '
    Write-Host "$names  ($($group.Name))" -ForegroundColor Cyan
    try {
        $tags = Invoke-RestMethod -Uri "$($group.Name)/api/tags" -TimeoutSec 5
        if (-not $tags.models -or $tags.models.Count -eq 0) { Write-Host '    (no models installed)' }
        else {
            $used = @($group.Group | ForEach-Object { $_.Wants } | Where-Object { $_ })
            foreach ($m in ($tags.models | Sort-Object name)) {
                $sizeGB = [math]::Round($m.size / 1GB, 1)
                $mark = if ($used -contains $m.name -or $used -contains ($m.name -replace ':latest$', '')) { '  <- used by the fleet' } else { '' }
                Write-Host "    $($m.name)  -  $sizeGB GB, $($m.details.parameter_size) $($m.details.quantization_level)$mark"
            }
        }
    } catch {
        Write-Host "    UNREACHABLE - $($_.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ''
}
