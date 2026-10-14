<#
.SYNOPSIS
    Checks the tray's public guide, troubleshooting and configuration text.
#>
[CmdletBinding()]
param([string]$Check)
$ErrorActionPreference='Stop'
$script:Checks=0
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Assert-TrayDocs([string]$Name,[bool]$Condition) {
    if ($Check -and $Check -ne $Name) { return }
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:Checks++
    Write-Host "PASS: $Name"
}
function Get-GuideSection([string]$File,[string]$Heading) {
    $text=Get-Content -LiteralPath (Join-Path $repo $File) -Raw
    $start=$text.IndexOf($Heading,[StringComparison]::Ordinal)
    if($start -lt 0){return ''}
    $remaining=$text.Substring($start+$Heading.Length)
    $next=[regex]::Match($remaining,'(?m)^## ')
    if($next.Success){$remaining=$remaining.Substring(0,$next.Index)}
    return $remaining
}
try {
    $guide=Get-GuideSection 'docs\getting-started.md' '## The tray icon and the shortcut'
    Assert-TrayDocs 'Guide has the tray and shortcut section' (-not [string]::IsNullOrWhiteSpace($guide))
    Assert-TrayDocs 'Guide explains hidden icons' ($guide.Contains('up-arrow') -and $guide.Contains('drag'))
    Assert-TrayDocs 'Guide explains Open Close and restarting from the shortcut' ($guide.Contains('Open Agent Fleet') -and $guide.Contains('Close Agent Fleet') -and $guide.Contains('logon starts') -and $guide.Contains('Start Menu shortcut again'))
    Assert-TrayDocs 'Guide explains the plan confirmation default' ($guide.Contains('running or approved') -and $guide.Contains('default is No'))
    $configuration=Get-GuideSection 'docs\configuration.md' '## Windows tray configuration'
    Assert-TrayDocs 'Configuration documents direct hidden STA PowerShell' ($configuration.Contains('powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "<scripts>\tray\Fleet-Tray.ps1"') -and $configuration.Contains('minimized window style'))
    $troubleshooting=Get-GuideSection 'docs\troubleshooting.md' '## The tray icon is missing'
    Assert-TrayDocs 'Troubleshooting documents the fixed antivirus exclusion folder' ($troubleshooting.Contains('C:\fleet-test-tray') -and $troubleshooting.Contains('Antivirus exclusion needed for this folder before running'))
    foreach($field in 'installRoot','backendUrl','webUrl','backendTask','frontendTask','mode','version') {
        Assert-TrayDocs ('Install field ' + $field) ($configuration.Contains('`' + $field + '`'))
    }
    Assert-TrayDocs 'Configuration describes browser-free checks' ($configuration.Contains('FLEET_TRAY_NO_BROWSER=1') -and $configuration.Contains('tray-open-url.txt') -and $configuration.Contains('-StartMenuFolder'))
    foreach($heading in '## The tray icon is missing','## Close did not stop everything','## The fleet starts again after Close') {
        $section=Get-GuideSection 'docs\troubleshooting.md' $heading
        Assert-TrayDocs ('Troubleshooting: '+$heading.Substring(3)) (-not [string]::IsNullOrWhiteSpace($section))
    }
    $scripts=Get-Content -LiteralPath (Join-Path $repo 'scripts\README.md') -Raw
    Assert-TrayDocs 'Scripts table includes installer launcher generator and checks' ($scripts.Contains('| `Install-FleetTray.ps1`') -and $scripts.Contains('| `tray/Fleet-Tray.ps1`') -and $scripts.Contains('| `tray/New-FleetIcon.ps1`') -and $scripts.Contains('tray/Test-FleetTray.Docs.ps1'))
    $newText=$guide+$configuration+(Get-GuideSection 'docs\troubleshooting.md' '## The tray icon is missing')+(Get-GuideSection 'docs\troubleshooting.md' '## Close did not stop everything')+(Get-GuideSection 'docs\troubleshooting.md' '## The fleet starts again after Close')
    Assert-TrayDocs 'New wording has no private run labels or user paths' ($newText -notmatch '(?i)\bloop\s+[a-z]\b|\b[A-Z]\d+(?:-\d+)?\b|[a-z]:[\\/]users[\\/]')
    if($script:Checks -eq 0){throw 'No matching check was run.'}
    Write-Host "All $script:Checks documentation checks passed."
} catch { Write-Host $_.Exception.Message; exit 1 }
