<#
.SYNOPSIS
    Builds and installs the @fleet VS Code extension into VS Code and/or VS Code Insiders.

.DESCRIPTION
    Runs in the vscode-fleet folder: npm install, compile, package a .vsix, then for each VS Code build that
    is installed, uninstalls the old copy first (so VS Code cannot keep two versions side by side) and installs
    the new one. Fully close and reopen VS Code afterwards; "Reload Window" is not enough.

    The extension talks to the backend at http://localhost:8000 by default. On another machine, set the VS Code
    setting agentFleet.backendUrl to the hub's address, for example http://192.168.1.10:8000.

.EXAMPLE
    .\scripts\Install-VSCodeExtension.ps1
#>
[CmdletBinding()]
param(
    [string]$BackendUrl
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$extensionDir = Join-Path $script:RepoRoot 'vscode-fleet'

if (-not (Test-Command node)) { throw 'Node.js is required. Install it (winget install OpenJS.NodeJS.LTS) and run this again.' }
$clis = @('code', 'code-insiders') | Where-Object { Test-Command $_ }
if ($clis.Count -eq 0) { throw 'Neither "code" nor "code-insiders" is on PATH. In VS Code, run "Shell Command: Install code command in PATH", or install the .vsix by hand (Extensions view, ... menu, Install from VSIX).' }

Push-Location $extensionDir
try {
    Write-Step 'Building the extension'
    npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed.' }
    npm run compile; if ($LASTEXITCODE) { throw 'npm run compile failed.' }
    $version = (Get-Content package.json -Raw | ConvertFrom-Json).version
    $vsix = Join-Path $extensionDir "agent-fleet-chat-$version.vsix"
    npx --yes '@vscode/vsce@4.0.0' package --out $vsix
    if ($LASTEXITCODE -or -not (Test-Path $vsix)) { throw 'Packaging failed.' }
    Write-Ok "Built $vsix"

    foreach ($cli in $clis) {
        Write-Step "Installing into $cli"
        & $cli --uninstall-extension local.agent-fleet-chat 2>&1 | Out-Null
        & $cli --install-extension $vsix --force 2>&1 | Where-Object { $_ -notmatch 'DEP0169|trace-deprecation' } | ForEach-Object { Write-Info $_ }
        $listed = @(& $cli --list-extensions --show-versions 2>$null | Where-Object { $_ -like 'local.agent-fleet-chat*' })
        if ($listed.Count -gt 0) { Write-Ok "$cli now has $($listed[0])" } else { Write-Bad "$cli does not list the extension after installing." }
    }
}
finally { Pop-Location }

Write-Host "`nFully close and reopen VS Code, then open Copilot Chat and type: @fleet hello" -ForegroundColor Green
if ($BackendUrl) { Write-Host "Set the VS Code setting  agentFleet.backendUrl  to  $BackendUrl" }
else { Write-Info 'VS Code on a different machine than the hub? Set agentFleet.backendUrl to the hub address, for example http://192.168.1.10:8000.' }
Write-Info 'GitHub Copilot Chat must be installed (the free tier is enough): @fleet lives inside its panel.'
