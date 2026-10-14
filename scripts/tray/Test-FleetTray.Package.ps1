<#
.SYNOPSIS
    Checks install previews and stages the Windows tray's release file list without building the application.
#>
[CmdletBinding()]
param([string]$Check)
$ErrorActionPreference = 'Stop'
$script:Checks = 0
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Assert-TrayPackage([string]$Name, [bool]$Condition) {
    if ($Check -and $Check -ne $Name) { return }
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:Checks++
    Write-Host "PASS: $Name"
}
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $tempBase ('fleet-tray-package-' + [Guid]::NewGuid().ToString('N'))
$prefix = 'AgentFleetTest' + [Guid]::NewGuid().ToString('N').Substring(0,12)
try {
    $releasePath = Join-Path $repo 'scripts\Build-Release.ps1'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($releasePath,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'The release script did not parse.' }
    $assignment = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -eq 'trayFiles' }, $true)
    $trayFiles = @()
    if ($assignment) { $trayFiles = @($assignment.Right.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] },$true) | ForEach-Object { $_.Value }) }
    $expected = @('Fleet-Tray.ps1','FleetTray.Functions.ps1','AgentFleet.ico','New-FleetIcon.ps1')
    $missing = @($expected | Where-Object { $_ -notin $trayFiles })
    Assert-TrayPackage 'Windows release includes all tray files' ($missing.Count -eq 0 -and $trayFiles.Count -eq $expected.Count)
    $release = Get-Content -LiteralPath $releasePath -Raw
    Assert-TrayPackage 'Windows release includes the tray installer' ($release.Contains("`$scripts += 'Install-FleetTray.ps1'"))
    Assert-TrayPackage 'Tray files are copied only for Windows' ($release -match "if \(\`$Runtime -like 'win-\*'\) \{\s*\`$trayFiles" -and $release -match 'foreach \(\$file in \$trayFiles\).*Copy-Item')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'stage\scripts\tray'),(Join-Path $root 'install\backend'),(Join-Path $root 'menu'),(Join-Path $root 'desktop') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'scripts\Install-FleetTray.ps1') -Destination (Join-Path $root 'stage\scripts')
    foreach ($file in $trayFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $root 'stage\scripts\tray')
        Write-Host ('STAGED: scripts/tray/' + $file)
    }
    $copied = @(Get-ChildItem -LiteralPath (Join-Path $root 'stage') -Recurse -File)
    Assert-TrayPackage 'Staging contains only the five required tray files' ($copied.Count -eq 5 -and @($copied | Where-Object { $_.Extension -eq '.png' -or $_.Name -like 'Test-*' }).Count -eq 0)
    Add-Type -AssemblyName System.Drawing
    $iconPath = Join-Path $PSScriptRoot 'AgentFleet.ico'
    $bytes = [IO.File]::ReadAllBytes($iconPath)
    $icon = New-Object Drawing.Icon($iconPath)
    try { Assert-TrayPackage 'Windows icon has nine frames and loads' ([BitConverter]::ToUInt16($bytes,2) -eq 1 -and [BitConverter]::ToUInt16($bytes,4) -eq 9 -and $icon.Width -gt 0) }
    finally { $icon.Dispose() }
    [xml]$svg = Get-Content -LiteralPath (Join-Path $repo 'agent-fleet\src\app\icon.svg') -Raw
    Assert-TrayPackage 'Web icon has the matching geometry' ($svg.svg.viewBox -eq '0 0 256 256' -and $svg.svg.rect.rx -eq '56' -and $svg.svg.path.d -eq 'M128 84 L69 179 M128 84 L187 179' -and @($svg.svg.circle).Count -eq 3 -and $svg.svg.circle[0].fill -eq '#F8FAFC' -and $svg.svg.circle[1].fill -eq '#E0F2FE')
    $starter = Get-Content -LiteralPath (Join-Path $repo 'scripts\release\Start Agent Fleet.cmd') -Raw
    Assert-TrayPackage 'Release starter keeps the console fallback' ($starter.Contains('if exist "%~dp0scripts\tray\Fleet-Tray.ps1"') -and $starter.Contains('start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "%~dp0scripts\tray\Fleet-Tray.ps1" --open') -and $starter.Contains('scripts\Start-Fleet.ps1" %*'))
    $installerText = Get-Content -LiteralPath (Join-Path $repo 'scripts\Install-FleetTray.ps1') -Raw
    Assert-TrayPackage 'Tray launch requires hidden STA PowerShell' ($installerText.Contains("System32\WindowsPowerShell\v1.0\powershell.exe") -and $installerText.Contains("`$taskArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File """))
    Assert-TrayPackage 'Shortcuts use minimized windows and match on reinstall' ($installerText.Contains('$link.WindowStyle=7') -and $installerText.Contains('$existing.WindowStyle -eq 7'))
    Assert-TrayPackage 'Release contains no script-host launcher' (@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.vbs').Count -eq 0 -and $starter -notmatch '(?i)wscript\.exe')
    $runtimeText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Fleet-Tray.ps1') -Raw
    Assert-TrayPackage 'Second launch opens through the named mutex and returns' ($runtimeText.Contains("'Local\AgentFleetTray-' + `$identity") -and $runtimeText -match '(?s)if \(-not \$created\) \{\s*if \(\$opening\) \{.*?Invoke-FleetOpen.*?\r?\n\s*return\s*\}')
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts\Install-FleetTray.ps1'),[ref]$tokens,[ref]$errors)
    Assert-TrayPackage 'PowerShell parser: tray installer' ($errors.Count -eq 0)
    $windowsText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-FleetTray.Windows.ps1') -Raw
    Assert-TrayPackage 'Windows fixtures use one fixed exclusion folder' ($windowsText.Contains("`$root = 'C:\fleet-test-tray'") -and $windowsText.Contains('Antivirus exclusion needed for this folder before running') -and -not $windowsText.Contains('GetTempPath') -and $windowsText.Contains("`$scriptsRoot = Join-Path `$root 'scripts'") -and $windowsText.Contains('Copy-Item -LiteralPath'))
    Assert-TrayPackage 'Windows fixture cleanup requires ownership and rejects reparse points' ($windowsText.Contains("if (Test-Path -LiteralPath `$root) { throw") -and $windowsText.Contains("if (`$full -ne 'C:\fleet-test-tray')") -and $windowsText.Contains('$createdRoot -and') -and $windowsText.Contains('[IO.FileAttributes]::ReparsePoint'))
    foreach ($file in 'FleetTray.Functions.ps1','Fleet-Tray.ps1','New-FleetIcon.ps1','Test-FleetTray.ps1','Test-FleetTray.Windows.ps1') {
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
        Assert-TrayPackage ('PowerShell parser: ' + $file) ($errors.Count -eq 0)
    }
    $installRoot = Join-Path $root 'install'
    Set-Content -LiteralPath (Join-Path $installRoot 'backend\AgentFleet.exe') -Value 'fixture, never executed'
    @{installRoot=$installRoot;backendUrl='http://localhost:8033';webUrl='http://localhost:3033';backendTask=($prefix+'Backend');frontendTask=($prefix+'Frontend');mode='Task';version='fixture'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'fleet-install.json')
    $before = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
    $options = @{ InstallRoot=$installRoot; BackendName=($prefix+'Backend'); FrontendName=($prefix+'Frontend'); TrayName=($prefix+'Tray'); BackendPort=8033; FrontendPort=3033; StartMenuFolder=(Join-Path $root 'menu'); DesktopFolder=(Join-Path $root 'desktop'); DryRun=$true; SkipBuild=$true; SkipFrontend=$true }
    $autostart = Join-Path $repo 'scripts\Install-Autostart.ps1'
    $output = & $autostart @options 6>&1 | Out-String
    Assert-TrayPackage 'Autostart DryRun forwards tray settings' ($output.Contains('would: register the logon-only tray task ' + $prefix + 'Tray'))
    $withoutTray = & $autostart @options -NoTray 6>&1 | Out-String
    Assert-TrayPackage 'NoTray skips tray registration' (-not $withoutTray.Contains('logon-only tray task') -and -not $withoutTray.Contains('Tray and shortcut are ready'))
    $removal = & $autostart @options -Uninstall -NoTray 6>&1 | Out-String
    Assert-TrayPackage 'Autostart Uninstall previews tray removal' ($removal.Contains('would: remove the install record'))
    $after = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
    Assert-TrayPackage 'Autostart previews write no fixtures or tasks' ($before -eq $after -and @(Get-ScheduledTask -TaskName ($prefix + '*') -ErrorAction SilentlyContinue).Count -eq 0)
    if ($script:Checks -eq 0) { throw 'No matching check was run.' }
    Write-Host "All $script:Checks package checks passed; application build and archive were not run."
} catch { Write-Host $_.Exception.Message; exit 1 }
finally {
    $full = [IO.Path]::GetFullPath($root)
    if (-not $full.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -or $full -eq $tempBase) { throw 'Refusing cleanup outside the test folder.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
