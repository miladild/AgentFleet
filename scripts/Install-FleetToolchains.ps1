<#
.SYNOPSIS
    Installs machine-wide runtimes commonly used by Fleet plans on Windows.

.DESCRIPTION
    Run this small, fixed-package installer in an elevated PowerShell. It does not run project scripts,
    npm installs, builds or anything from the repository. Agents stay under the Fleet process account.

.PARAMETER AuditOnly
    Check runtime availability without installing. Does not require elevation.

.PARAMETER DryRun
    Show the fixed winget installs that would run. Does not require elevation.
#>
[CmdletBinding()]
param(
    [switch]$AuditOnly,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This installer is for Windows. On Linux or macOS, install the runtimes through the system package manager.'
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-MachineCommand([string]$Name) {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    foreach ($directory in $machinePath.Split([IO.Path]::PathSeparator, [StringSplitOptions]::RemoveEmptyEntries)) {
        $candidate = Join-Path $directory.Trim('"') $Name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Get-ToolchainState {
    $dotnet = Find-MachineCommand 'dotnet.exe'
    $node = Find-MachineCommand 'node.exe'
    $npm = Find-MachineCommand 'npm.cmd'
    $python = Find-MachineCommand 'python.exe'

    $dotnetOk = $false
    if ($dotnet) {
        $sdks = @(& $dotnet --list-sdks 2>$null)
        $dotnetOk = @($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 9 }).Count -gt 0
    }
    $nodeVersion = if ($node) { (& $node --version 2>$null) -replace '^v', '' } else { '' }
    $nodeMajor = 0
    if ($nodeVersion -match '^(\d+)\.') { $nodeMajor = [int]$Matches[1] }
    $npmOk = $false
    if ($npm) { & $npm --version *> $null; $npmOk = $LASTEXITCODE -eq 0 }
    $pythonVersion = if ($python) { & $python --version 2>&1 | Out-String } else { '' }
    $pythonOk = $pythonVersion -match 'Python 3\.(1[3-9]|[2-9]\d)(?:\.|\s|$)'

    return [ordered]@{
        DotNet = $dotnetOk
        Node = ($nodeMajor -ge 20 -and $npmOk)
        Python = [bool]$pythonOk
    }
}

function Show-State($State) {
    foreach ($name in 'DotNet', 'Node', 'Python') {
        $label = switch ($name) { DotNet { '.NET SDK 9+' } Node { 'Node.js 20+ and npm' } Python { 'Python 3.13+ (python command)' } }
        if ($State[$name]) { Write-Host "  [ ok ] $label" -ForegroundColor Green }
        else { Write-Host "  [missing] $label" -ForegroundColor Yellow }
    }
}

$state = Get-ToolchainState
Write-Host 'Fleet machine toolchains (machine-wide visibility):'
Show-State $state
if ($AuditOnly) {
    return [pscustomobject]$state
}

$packages = @(
    @{ Key = 'DotNet'; Id = 'Microsoft.DotNet.SDK.9'; Label = '.NET SDK 9+' },
    @{ Key = 'Node';   Id = 'OpenJS.NodeJS.LTS';       Label = 'Node.js 20+ and npm' },
    @{ Key = 'Python'; Id = 'Python.Python.3.13';      Label = 'Python 3.13+' }
)
$missing = @($packages | Where-Object { -not $state[$_.Key] })
if ($missing.Count -eq 0) {
    Write-Host 'All supported toolchains are available machine-wide.' -ForegroundColor Green
    return
}

if (-not $DryRun -and -not (Test-Administrator)) {
    throw 'Machine-wide runtime installation needs administrator approval. Open PowerShell with Run as administrator and run this script again. Do not run Setup-Hub.ps1 elevated.'
}

$winget = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
if (-not $winget) { throw 'winget is missing. Install or update App Installer, then rerun this script as administrator.' }

foreach ($package in $missing) {
    $arguments = @('install', '--id', $package.Id, '--exact', '--source', 'winget', '--scope', 'machine', '--silent',
        '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')
    if ($DryRun) {
        Write-Host "Would install $($package.Label) machine-wide: winget $($arguments -join ' ')"
        continue
    }
    Write-Host "Installing $($package.Label) machine-wide..."
    & $winget.Source @arguments
    if ($LASTEXITCODE -ne 0) { throw "winget failed to install $($package.Label) (exit $LASTEXITCODE)." }
}

if ($DryRun) { return }

$state = Get-ToolchainState
Write-Host 'Verified runtimes after installation:'
Show-State $state
if (@($state.Values | Where-Object { -not $_ }).Count -gt 0) {
    throw 'One or more runtimes are still unavailable. Check the winget log and install them machine-wide before running plans that need them.'
}

Write-Host 'Open a fresh normal (non-elevated) shell. On a worker, restart sshd so new SSH sessions inherit the updated machine PATH; on the hub, restart Fleet.' -ForegroundColor Cyan
