<#
.SYNOPSIS
    Lists the agent-framework related NuGet packages in the agent project that have newer versions, with release links.

.DESCRIPTION
    Runs `dotnet list <project> package --outdated --include-prerelease` and prints the packages whose id starts with one
    of the prefixes: Microsoft.Agents.AI, Microsoft.Extensions.AI, ModelContextProtocol, AGUI, OllamaSharp and OpenAI. For
    each one it prints the current and latest versions and a release link: the agent framework's GitHub release tag for
    Microsoft.Agents.AI packages, the NuGet page for every other package.

    Read-only. It never edits the project or any tracked file. When the first list fails, the project is restored once
    (that writes build output under obj, which is ignored) and the list is run again. Use -FromFile with saved
    `dotnet list package --outdated` text to check without running dotnet.

    Exit codes: 0 when the check ran (whether or not anything is outdated), 1 when the project or -FromFile does not exist,
    2 when dotnet could not complete the check, for example without network access.
#>
[CmdletBinding()]
param(
    [string]$Project,
    [string]$FromFile,
    [ValidateNotNullOrEmpty()][string[]]$Prefix = @('Microsoft.Agents.AI', 'Microsoft.Extensions.AI', 'ModelContextProtocol', 'AGUI', 'OllamaSharp', 'OpenAI')
)

$ErrorActionPreference = 'Stop'
$versionPattern = '^\d+(\.\d+)+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'

function Invoke-Dotnet([string[]]$Arguments) {
    # dotnet can write diagnostics to stderr; under Windows PowerShell 5.1 that would stop the script, so keep them as text.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& dotnet @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = $lines }
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Lines = @($_.Exception.Message) }
    }
    finally { $ErrorActionPreference = $savedPreference }
}

function Stop-Check([string[]]$Lines) {
    $firstError = @($Lines | Where-Object { $_ -match '(?i)error' } | Select-Object -First 1)
    $firstLine = @($Lines | Where-Object { $_.Trim() } | Select-Object -First 1)
    $text = '(dotnet printed nothing)'
    if ($firstError.Count) { $text = $firstError[0].Trim() }
    elseif ($firstLine.Count) { $text = $firstLine[0].Trim() }
    Write-Host 'dotnet could not complete the check, so no package versions were read.'
    Write-Host "First line of dotnet's output: $text"
    exit 2
}

function Test-Prefix([string]$Id) {
    foreach ($item in $Prefix) {
        if ($Id.StartsWith($item, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-ReleaseLink([string]$Id, [string]$Version) {
    if ($Id.StartsWith('Microsoft.Agents.AI', [StringComparison]::OrdinalIgnoreCase)) {
        return "https://github.com/microsoft/agent-framework/releases/tag/dotnet-$Version"
    }
    return "https://www.nuget.org/packages/$Id/$Version"
}

if ($FromFile) {
    if (-not (Test-Path -LiteralPath $FromFile -PathType Leaf)) {
        Write-Host "Saved dotnet output not found: $FromFile"
        exit 1
    }
    $lines = @(Get-Content -LiteralPath $FromFile)
}
else {
    if (-not $Project) { $Project = Join-Path $PSScriptRoot '..\agent-fleet\agent\AgentFleet.csproj' }
    $Project = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Project)
    if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) {
        Write-Host "Project file not found: $Project"
        Write-Host 'Pass -Project with the path to a .csproj or .sln file.'
        exit 1
    }
    $list = Invoke-Dotnet @('list', $Project, 'package', '--outdated', '--include-prerelease')
    if ($list.ExitCode -ne 0) {
        $restore = Invoke-Dotnet @('restore', $Project)
        if ($restore.ExitCode -ne 0) { Stop-Check $restore.Lines }
        $list = Invoke-Dotnet @('list', $Project, 'package', '--outdated', '--include-prerelease')
        if ($list.ExitCode -ne 0) { Stop-Check $list.Lines }
    }
    $lines = $list.Lines
}

if (@($lines | Where-Object { $_ -match '^\s*error\b' }).Count) { Stop-Check $lines }

# The current message is only true when dotnet really read the feed: it printed a package row (any package), or it said
# that nothing is outdated. Anything else means the check did not complete, for example a source that was refused.
$found = @()
$seen = @{}
$parsedRows = 0
foreach ($line in $lines) {
    if ($line -notmatch '^\s*>\s+(.+?)\s*$') { continue }
    # Split on whitespace, not column positions. Drops markers such as (D).
    $tokens = @($Matches[1] -split '\s+' | Where-Object { $_ -and $_ -notmatch '^\(\w+\)$' })
    if ($tokens.Count -lt 4) { continue }
    $id = $tokens[0]
    $resolved = $tokens[2]
    $latest = $tokens[3]
    if ($resolved -notmatch $versionPattern -or $latest -notmatch $versionPattern) { continue }
    $parsedRows++
    if (-not (Test-Prefix $id)) { continue }
    $key = "$id|$latest"
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    $found += [pscustomobject]@{ Id = $id; Current = $resolved; Latest = $latest }
}

$noUpdatesSaid = @($lines | Where-Object { $_ -match 'has no updates' }).Count -gt 0
if ($found.Count -eq 0) {
    if ($parsedRows -eq 0 -and -not $noUpdatesSaid) { Stop-Check $lines }
    Write-Host 'All matching packages are current.'
    exit 0
}

Write-Host 'Outdated agent-framework related packages:'
foreach ($item in $found) {
    Write-Host ('{0}  {1} -> {2}' -f $item.Id, $item.Current, $item.Latest)
    Write-Host ('    {0}' -f (Get-ReleaseLink $item.Id $item.Latest))
}
exit 0
