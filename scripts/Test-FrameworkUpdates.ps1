<#
.SYNOPSIS
    Checks the framework update report parser against canned `dotnet list package --outdated` text. No network access.

.DESCRIPTION
    Runs Get-FrameworkUpdates.ps1 with -FromFile in a child shell of the same edition that runs this script, and checks the
    filtering, the release links, the prerelease handling, the "current" message and the exit codes.
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-FrameworkUpdates.ps1'
$shell = (Get-Process -Id $PID).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('framework-updates-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-Updates([string]$Text) {
    $file = Join-Path $work ([Guid]::NewGuid().ToString('N') + '.txt')
    Set-Content -LiteralPath $file -Value $Text -Encoding ASCII
    $output = @(& $shell -NoProfile -File $script -FromFile $file 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "PASS $Message"
}

try {
    $table = @'
Project 'Sample' has the following updates to its packages
   [net9.0]:
   Top-level Package                          Requested               Resolved                Latest
   > Microsoft.Agents.AI                      1.0.0-preview.260101.1  1.0.0-preview.260101.1  1.0.0-preview.260301.1
   > Microsoft.Extensions.AI                  9.1.0                   9.1.0                   9.2.0
   > Microsoft.Extensions.Logging             9.0.0                   9.0.0                   9.1.0
   > ModelContextProtocol                     0.5.0-preview.1         0.5.0-preview.1         0.6.0-preview.1
   > AGUI.Core                                0.1.0                   0.1.0                   0.2.0
   > OllamaSharp                              5.0.0                   5.0.0                   5.1.0 (D)
   > OpenAI (D)                               2.0.0                   2.0.0                   2.1.0
   > Newtonsoft.Json                          13.0.1                  13.0.1                  13.0.4
   > Serilog                                  4.0.0                   4.0.0                   4.1.0
   [net8.0]:
   Top-level Package                          Requested               Resolved                Latest
   > Microsoft.Extensions.AI                  9.1.0                   9.1.0                   9.2.0
'@
    $result = Invoke-Updates $table
    Assert-Check ($result.ExitCode -eq 0) 'table report exits 0'
    Assert-Check ($result.Output -match 'Microsoft\.Agents\.AI  1\.0\.0-preview\.260101\.1 -> 1\.0\.0-preview\.260301\.1') 'agent framework row shows current and latest versions'
    Assert-Check ($result.Output -match 'ModelContextProtocol  0\.5\.0-preview\.1 -> 0\.6\.0-preview\.1') 'prerelease latest version is reported'
    Assert-Check ($result.Output -match 'OpenAI  2\.0\.0 -> 2\.1\.0') 'row with a (D) marker is parsed'
    Assert-Check ($result.Output -match 'OllamaSharp  5\.0\.0 -> 5\.1\.0') 'row with a trailing (D) marker is parsed'
    Assert-Check ($result.Output -match [regex]::Escape('https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.0.0-preview.260301.1')) 'agent framework row links to its GitHub release tag'
    Assert-Check ($result.Output -match [regex]::Escape('https://www.nuget.org/packages/OllamaSharp/5.1.0')) 'other rows link to their NuGet page'
    Assert-Check ($result.Output -notmatch 'nuget\.org/packages/Microsoft\.Agents\.AI') 'agent framework row does not use the NuGet link'
    Assert-Check ($result.Output -notmatch 'Newtonsoft\.Json|Serilog|Microsoft\.Extensions\.Logging') 'non-matching packages are not printed'
    Assert-Check (([regex]::Matches($result.Output, 'Microsoft\.Extensions\.AI ')).Count -eq 1) 'a package listed under two frameworks is printed once'

    $none = Invoke-Updates @'
Project 'Sample' has the following updates to its packages
   [net9.0]:
   Top-level Package                          Requested  Resolved  Latest
   > Newtonsoft.Json                          13.0.1     13.0.1    13.0.4
'@
    Assert-Check ($none.ExitCode -eq 0 -and $none.Output -match 'All matching packages are current\.') 'only non-matching rows print the current message and exit 0'

    $noUpdates = Invoke-Updates 'The given project `Sample` has no updates to its packages.'
    Assert-Check ($noUpdates.ExitCode -eq 0 -and $noUpdates.Output -match 'All matching packages are current\.') 'a no-updates message prints the current message and exits 0'

    $failed = Invoke-Updates @'
  Determining projects to restore...
  error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.
'@
    Assert-Check ($failed.ExitCode -eq 2 -and $failed.Output -match 'could not complete the check' -and $failed.Output -match 'error NU1301') 'a dotnet error exits 2 with its first error line'

    $colonError = Invoke-Updates "  error: Unable to read the project file"
    Assert-Check ($colonError.ExitCode -eq 2 -and $colonError.Output -match 'error: Unable to read') 'an error line with a colon exits 2'

    $noSource = Invoke-Updates @'
  Determining projects to restore...
  All projects are up-to-date for restore.

The following sources were used:
   http://192.0.2.10:9/v3/index.json


You are running the 'list package' operation with an 'HTTP' source, 'http://192.0.2.10:9/v3/index.json [http://192.0.2.10:9/v3/index.json]'. NuGet requires HTTPS sources. To use HTTP sources, you must e...
'@
    Assert-Check ($noSource.ExitCode -eq 2 -and $noSource.Output -notmatch 'All matching packages are current') 'dotnet output with no package rows and no no-updates sentence exits 2, not a false all-clear'

    $headerOnly = Invoke-Updates @'
   [net9.0]:
   > Newtonsoft.Json                          13.0.1     13.0.1    13.0.4
   > Serilog                                  4.0.0      4.0.0     4.1.0
'@
    Assert-Check ($headerOnly.ExitCode -eq 0 -and $headerOnly.Output -match 'All matching packages are current\.') 'a framework header with only non-matching rows exits 0 with the current message'

    $missing = Join-Path $work 'does-not-exist.txt'
    & $shell -NoProfile -File $script -FromFile $missing *> $null
    Assert-Check ($LASTEXITCODE -ne 0) 'a missing -FromFile file exits non-zero'

    Write-Host 'All framework update checks passed.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
