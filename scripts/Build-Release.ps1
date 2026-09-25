<#
.SYNOPSIS
    Builds the downloadable release files: the fleet for one platform, and the VS Code extension.

.DESCRIPTION
    Produces, in -OutDir:
      AgentFleet-<version>-<runtime>.zip (Windows) or .tar.gz (Linux, macOS)
          backend\   the backend, self-contained: no .NET needed on the machine
          web\       the web UI as a Next.js standalone server: needs Node.js 20 or newer
          scripts\   Start-Fleet, Install-Autostart, Test-Fleet and the machine setup scripts
          docs\, README.md, LICENSE, fleet.config.example.json
      agent-fleet-chat-<version>.vsix   the VS Code extension (with -Extension)
      SHA256SUMS-<runtime>.txt

    Only files tracked by git are copied from the repository, so notes kept out of the repository never end
    up in a download. The GitHub workflow .github/workflows/release.yml runs this for every version tag.
    Needs PowerShell 7, the .NET 9 SDK, Node.js 20+ and git.

.PARAMETER Runtime
    win-x64, linux-x64, linux-arm64 or osx-arm64. Build Linux and macOS files on Linux or macOS, so the
    programs keep their executable bit.

.PARAMETER Version
    The version in the file names. Default: the tag being built (GITHUB_REF_NAME without its leading v), else "dev".

.EXAMPLE
    pwsh scripts/Build-Release.ps1 -Runtime win-x64 -Extension
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')][string]$Runtime,
    [string]$Version,
    [string]$OutDir,
    [switch]$Extension,
    [switch]$SkipFleet
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot\FleetCommon.ps1"
$root = $script:RepoRoot
$web = Join-Path $root 'agent-fleet'
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
if (-not $Version) { $Version = if ($env:GITHUB_REF_NAME -match '^v?(\d+\.\d+\.\d+.*)$') { $Matches[1] } else { 'dev' } }
if ($Version -notmatch '^[0-9A-Za-z.\-]+$') { throw "Version '$Version' may only contain letters, digits, dots and hyphens." }
foreach ($tool in 'dotnet', 'node', 'npm', 'git') { if (-not (Test-Command $tool)) { throw "$tool is needed to build a release." } }
$null = New-Item -ItemType Directory -Force -Path $OutDir
$OutDir = (Resolve-Path $OutDir).Path

# Files git tracks under a folder, as paths relative to the repository root.
function Get-TrackedFiles([string]$Path) {
    Push-Location $root
    try { return @(git ls-files -- $Path) } finally { Pop-Location }
}

function Copy-Tracked([string]$Path, [string]$Destination) {
    foreach ($file in Get-TrackedFiles $Path) {
        $target = Join-Path $Destination $file
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
    }
}

$assets = @()
if (-not $SkipFleet) {
    $name = "AgentFleet-$Version-$Runtime"
    $stage = Join-Path $OutDir $name
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $null = New-Item -ItemType Directory -Path $stage

    Write-Step "Backend ($Runtime, self-contained)"
    dotnet publish (Join-Path $web 'agent') -c Release -r $Runtime --self-contained true -o (Join-Path $stage 'backend') -p:DebugType=None -nologo -v q
    Write-Ok 'Published'

    Write-Step 'Web UI (Next.js standalone)'
    # A separate build folder, so building a release never touches the .next folder a running web UI uses.
    $distDir = '.next-release'
    Push-Location $web
    try {
        if (-not (Test-Path 'node_modules')) { npm ci --no-audit --no-fund }
        $env:FLEET_STANDALONE = '1'; $env:NEXT_DIST_DIR = $distDir; $env:NEXT_TELEMETRY_DISABLED = '1'
        try { npm run build } finally { $env:FLEET_STANDALONE = $null; $env:NEXT_DIST_DIR = $null }
        # next build rewrites these two files to point at the build folder; put back the committed versions.
        if (Test-Path (Join-Path $root '.git')) { git checkout -- next-env.d.ts tsconfig.json 2>$null }

        $webStage = Join-Path $stage 'web'
        Copy-Item -Recurse (Join-Path $distDir 'standalone') $webStage
        Copy-Item -Recurse (Join-Path $distDir 'static') (Join-Path $webStage "$distDir/static")
        if (Test-Path 'public') { Copy-Item -Recurse 'public' (Join-Path $webStage 'public') }
        # Started as a child process or read from disk at run time, so the standalone build leaves them out.
        node scripts/trace-files.mjs scripts/validate-mermaid.mjs $webStage
        $guide = Join-Path $webStage 'src/content'
        $null = New-Item -ItemType Directory -Force -Path $guide
        Copy-Item 'src/content/usage-guide.md' $guide -Force
        # Local settings of whoever built it never belong in a download.
        Get-ChildItem $webStage -Force -File | Where-Object { $_.Name -like '.env*' } | Remove-Item -Force
    }
    finally { Pop-Location }
    Write-Ok 'Built'

    Write-Step 'Scripts, docs and license'
    $scripts = 'Start-Fleet.ps1', 'start-fleet.sh', 'Install-Autostart.ps1', 'install-autostart.sh', 'Test-Fleet.ps1', 'Add-FleetNode.ps1',
        'Get-FleetModels.ps1', 'Setup-Worker.ps1', 'setup-worker.sh', 'FleetCommon.ps1'
    $null = New-Item -ItemType Directory -Path (Join-Path $stage 'scripts')
    foreach ($script in $scripts) { Copy-Item (Join-Path $root "scripts/$script") (Join-Path $stage 'scripts') }
    Copy-Tracked 'docs' $stage
    Copy-Item (Join-Path $root 'LICENSE') $stage
    Copy-Item (Join-Path $web 'fleet.config.example.json') $stage
    Copy-Item (Join-Path $root 'scripts/release/README.md') (Join-Path $stage 'README.md')
    # Whatever this machine has lying around (its own config, keys, conversations) must never reach a download.
    Write-Ok 'Copied'

    Write-Step 'Archive'
    if ($Runtime -like 'win-*') {
        $archive = Join-Path $OutDir "$name.zip"
        if (Test-Path $archive) { Remove-Item $archive }
        Compress-Archive -Path $stage -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $OutDir "$name.tar.gz"
        chmod +x (Join-Path $stage 'backend/AgentFleet') (Join-Path $stage 'scripts/start-fleet.sh') (Join-Path $stage 'scripts/install-autostart.sh') (Join-Path $stage 'scripts/setup-worker.sh')
        tar -czf $archive -C $OutDir $name
    }
    $assets += $archive
    Write-Ok ("{0} ({1:N0} MB)" -f (Split-Path $archive -Leaf), ((Get-Item $archive).Length / 1MB))
}

if ($Extension) {
    Write-Step 'VS Code extension'
    Push-Location (Join-Path $root 'vscode-fleet')
    try {
        npm ci --no-audit --no-fund
        npm run compile
        $extensionVersion = (Get-Content package.json -Raw | ConvertFrom-Json).version
        $vsix = Join-Path $OutDir "agent-fleet-chat-$extensionVersion.vsix"
        npx --yes '@vscode/vsce' package --skip-license --out $vsix
    }
    finally { Pop-Location }
    $assets += $vsix
    Write-Ok (Split-Path $vsix -Leaf)
}

if ($assets.Count -gt 0) {
    $sums = Join-Path $OutDir "SHA256SUMS-$Runtime.txt"
    $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } | Set-Content $sums
    Write-Ok "Checksums in $(Split-Path $sums -Leaf)"
}
Write-Host "`nRelease files are in $OutDir" -ForegroundColor Green
