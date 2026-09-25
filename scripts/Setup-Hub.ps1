<#
.SYNOPSIS
    Sets up this Windows machine as the fleet's hub, from a fresh clone, in one go.

.DESCRIPTION
    The hub is the machine that runs the backend and the web UI and usually the strongest model. This script:
      1. checks for (and offers to install with winget) the .NET 9 SDK, Node.js 20+ and Ollama,
      2. installs the web UI's packages and builds the backend,
      3. creates .env.local and fleet.config.json with this machine as the only node,
      4. downloads a coding model that suits this machine,
      5. optionally installs the @fleet VS Code extension.
    Run it as a normal user; nothing here needs administrator rights. It is safe to run again.

    Afterwards: start everything with `npm run dev` in the agent-fleet folder, open http://localhost:3000,
    and add more machines with Add-FleetNode.ps1 whenever you like.

.PARAMETER Model
    The model this machine should serve. By default one is chosen from this machine's graphics memory.

.PARAMETER SkipModel
    Do not download a model now.

.PARAMETER SkipPrerequisites
    Do not check for or install .NET, Node.js and Ollama.

.PARAMETER DryRun
    Say what would be done and change nothing.

.EXAMPLE
    .\scripts\Setup-Hub.ps1
#>
[CmdletBinding()]
param(
    [string]$Model,
    [string]$TriageModel = 'llama3.2:latest',
    [switch]$SkipModel,
    [switch]$SkipPrerequisites,
    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\FleetCommon.ps1"
$web = Join-Path $script:RepoRoot 'agent-fleet'
$backend = Join-Path $web 'agent'

function Do-Step([string]$What, [scriptblock]$Block) {
    if ($DryRun) { Write-Info "would: $What"; return }
    & $Block
}

# A tool installed a moment ago is not on PATH in this window yet.
function Update-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Install-WithWinget([string]$Id, [string]$Label) {
    if (-not (Test-Command winget)) { throw "$Label is required and winget is not available to install it. Install $Label yourself, then run this again." }
    if (-not (Confirm-Action "$Label is missing. Install it with winget now?" $true -Yes:$Yes)) { throw "$Label is required." }
    Do-Step "winget install $Id" {
        winget install --id $Id -e --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE) { throw "winget could not install $Label." }
        Update-Path
    }
}

# --- 1. prerequisites ---------------------------------------------------------------------------
Write-Step 'Prerequisites'
if (-not $SkipPrerequisites) {
    $dotnetOk = $false
    if (Test-Command dotnet) { $dotnetOk = (@(dotnet --list-sdks 2>$null) | ForEach-Object { [int](($_ -split '\.')[0]) } | Where-Object { $_ -ge 9 }).Count -gt 0 }
    if ($dotnetOk) { Write-Ok '.NET SDK 9 or newer' } else { Install-WithWinget 'Microsoft.DotNet.SDK.9' '.NET 9 SDK' }

    $nodeOk = $false
    if (Test-Command node) { $nodeOk = [int]((node --version) -replace '^v(\d+)\..*', '$1') -ge 20 }
    if ($nodeOk) { Write-Ok "Node.js $(node --version)" } else { Install-WithWinget 'OpenJS.NodeJS.LTS' 'Node.js (LTS)' }

    if (Test-Command ollama) { Write-Ok 'Ollama' } else { Install-WithWinget 'Ollama.Ollama' 'Ollama' }

    if (-not $DryRun) {
        foreach ($tool in 'dotnet', 'node', 'npm') {
            if (-not (Test-Command $tool)) { throw "$tool was installed but is not on PATH in this window yet. Open a new PowerShell window and run this script again." }
        }
    }
    if (-not (Test-Command git)) { Write-Warn 'git is not installed. Optional, but the run_git_command tool needs it (winget install Git.Git).' }
}

# --- 2. packages and build ----------------------------------------------------------------------
Write-Step 'Web UI packages'
Do-Step 'npm install in agent-fleet' { Push-Location $web; try { npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed.' } } finally { Pop-Location } }
if (-not $DryRun) { Write-Ok 'Done' }

Write-Step 'Building the backend'
Do-Step 'dotnet build' { Push-Location $backend; try { dotnet build -nologo -v q; if ($LASTEXITCODE) { throw 'dotnet build failed.' } } finally { Pop-Location } }
if (-not $DryRun) { Write-Ok 'Done' }

# --- 3. configuration ---------------------------------------------------------------------------
Write-Step 'Configuration'
$envLocal = Join-Path $web '.env.local'
if (-not (Test-Path $envLocal)) {
    Do-Step 'create agent-fleet\.env.local' { Copy-Item (Join-Path $web '.env.example') $envLocal }
    if (-not $DryRun) { Write-Ok 'Created agent-fleet\.env.local' }
} else { Write-Ok 'agent-fleet\.env.local already exists' }

# --- a model that suits this machine ------------------------------------------------------------
function Get-SuggestedModel {
    $vramGb = 0
    if (Test-Command nvidia-smi) {
        $line = (nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1)
        if ($line) { $vramGb = [math]::Round([double]$line / 1024) }
    }
    $ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    # The same ladder the web UI's Setup tab suggests from.
    if ($vramGb -ge 22) { return @('qwen3-coder:30b', "an NVIDIA GPU with ${vramGb} GB of memory") }
    if ($vramGb -ge 11) { return @('qwen2.5-coder:14b', "an NVIDIA GPU with ${vramGb} GB of memory") }
    if ($vramGb -ge 6 -or $ramGb -ge 16) { return @('qwen2.5-coder:7b', $(if ($vramGb -gt 0) { "an NVIDIA GPU with ${vramGb} GB of memory" } else { "$ramGb GB of RAM" })) }
    return @('qwen2.5-coder:3b', "$ramGb GB of RAM and no large GPU")
}
if (-not $Model) {
    $suggestion = Get-SuggestedModel
    $Model = $suggestion[0]
    Write-Info "Chose $Model for this machine ($($suggestion[1])). Override with -Model, for example -Model qwen2.5-coder:14b"
}

$configPath = Join-Path $web 'fleet.config.json'
if (-not (Test-Path $configPath)) {
    Do-Step 'create agent-fleet\fleet.config.json' {
        $tools = [ordered]@{}
        foreach ($t in 'read_file', 'write_file', 'edit_file', 'list_directory', 'find_files', 'search_files', 'run_git_command', 'run_command', 'run_sandboxed_code', 'web_search', 'web_fetch') { $tools[$t] = [ordered]@{ enabled = $true } }
        $config = [ordered]@{
            triageModel = $TriageModel
            mode = 'conservative'
            planModeEnabled = $false
            nodes = @([ordered]@{ name = 'hub'; url = 'http://127.0.0.1:11434/v1'; model = $Model; purpose = 'This machine: coding, tools and fallback.'; tier = 'heavy'; fallback = $true })
            tools = $tools
            mcpServers = [ordered]@{}
            sandbox = [ordered]@{ mode = 'auto' }
        }
        Write-FleetConfig $configPath $config
    }
    if (-not $DryRun) { Write-Ok "Created agent-fleet\fleet.config.json with this machine as the only node ($Model)" }
} else { Write-Ok 'agent-fleet\fleet.config.json already exists (left as it is)' }

# --- 4. the model -------------------------------------------------------------------------------
if (-not $SkipModel) {
    Write-Step "Model: $Model"
    $local = 'http://127.0.0.1:11434/v1'
    $models = Get-OllamaModels $local 3
    if ($null -eq $models -and -not $DryRun) {
        Write-Info 'Starting Ollama...'
        Start-Process -FilePath ollama -ArgumentList 'serve' -WindowStyle Hidden
        for ($i = 0; $i -lt 20 -and $null -eq $models; $i++) { Start-Sleep -Seconds 1; $models = Get-OllamaModels $local 2 }
    }
    if ($null -ne $models -and (Test-ModelPresent $models $Model)) { Write-Ok "$Model is already installed" }
    elseif ($DryRun) { Write-Info "would: download $Model" }
    elseif ($null -eq $models) { Write-Warn "Ollama did not start. Start it from the Start menu, then run: ollama pull $Model" }
    elseif (Confirm-Action "Download $Model now? (several GB)" $true -Yes:$Yes) {
        Invoke-OllamaPull $local $Model
        Write-Ok "$Model is installed"
    } else { Write-Warn "Skipped. Later: ollama pull $Model" }
}

# --- 5. the VS Code extension -------------------------------------------------------------------
if ((Test-Command code) -or (Test-Command code-insiders)) {
    Write-Step 'VS Code extension (@fleet in Copilot Chat)'
    if (Confirm-Action 'Install the @fleet extension into VS Code now?' $false -Yes:$false) {
        Do-Step 'run Install-VSCodeExtension.ps1' { & "$PSScriptRoot\Install-VSCodeExtension.ps1" }
    } else { Write-Info 'Later: .\scripts\Install-VSCodeExtension.ps1' }
}

Write-Host "`nThe hub is ready." -ForegroundColor Green
Write-Host '  Start it:        cd agent-fleet; npm run dev        (then open http://localhost:3000)'
Write-Host '  Check it:        .\scripts\Test-Fleet.ps1'
Write-Host '  Add a machine:   see docs\adding-machines.md, or .\scripts\Add-FleetNode.ps1'
Write-Host '  Run at logon:    .\scripts\Install-Autostart.ps1'
Write-Host '  Read this first: docs\security.md (the fleet has no login: keep it on your own network)'
