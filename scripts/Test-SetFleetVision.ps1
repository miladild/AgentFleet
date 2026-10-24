<#
.SYNOPSIS
    Checks the pure parts of Set-FleetVision.ps1 without a backend: mode spellings, choosing the machine,
    and the body that is saved. Exit code 1 when any check fails.

.DESCRIPTION
    The functions are read out of the script's syntax tree, so the script's main body (which talks to the backend)
    never runs here. Run it in Windows PowerShell 5.1 and in PowerShell 7.
#>
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$scriptPath = Join-Path $PSScriptRoot 'Set-FleetVision.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Set-FleetVision.ps1 has syntax errors.' }

foreach ($name in @('ConvertTo-VisionMode', 'Get-NodeVisionMode', 'Select-VisionNode', 'New-FleetVisionPutBody', 'Get-VisionModeMeaning')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "$name was not found in Set-FleetVision.ps1." }
    Invoke-Expression $definition.Extent.Text
}

$failures = 0
function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ("$Actual" -ceq "$Expected") { Write-Host "  [ ok ] $Label" -ForegroundColor Green }
    else { $script:failures++; Write-Host "  [FAIL] $Label (expected '$Expected', got '$Actual')" -ForegroundColor Red }
}
function Assert-Throws([scriptblock]$Action, [string]$Fragment, [string]$Label) {
    try { & $Action; $script:failures++; Write-Host "  [FAIL] $Label (no error)" -ForegroundColor Red }
    catch {
        if ($_.Exception.Message -like "*$Fragment*") { Write-Host "  [ ok ] $Label" -ForegroundColor Green }
        else { $script:failures++; Write-Host "  [FAIL] $Label (message: $($_.Exception.Message))" -ForegroundColor Red }
    }
}

Write-Host 'Mode spellings'
Assert-Equal (ConvertTo-VisionMode 'off') 'off' 'off is off'
Assert-Equal (ConvertTo-VisionMode 'ondemand') 'ondemand' 'ondemand is ondemand'
Assert-Equal (ConvertTo-VisionMode 'on-demand') 'ondemand' 'on-demand is ondemand'
Assert-Equal (ConvertTo-VisionMode 'on_demand') 'ondemand' 'on_demand is ondemand'
Assert-Equal (ConvertTo-VisionMode 'On Demand') 'ondemand' 'On Demand (with a space, any case) is ondemand'
Assert-Equal (ConvertTo-VisionMode 'ALWAYS') 'always' 'ALWAYS is always'
Assert-Equal (ConvertTo-VisionMode 'sometimes') $null 'sometimes is refused'
Assert-Equal (ConvertTo-VisionMode '') $null 'an empty value is refused'

$oneVision = @'
{"version":"v7","nodes":[
 {"name":"hub","vision":false,"tier":"heavy","fallback":true,"thinking":"model"},
 {"name":"eyes","vision":true,"tier":null,"fallback":false,"visionMode":"always","workspace":null},
 {"name":"reader","vision":false,"tier":"light","fallback":false}
]}
'@ | ConvertFrom-Json

$twoVision = @'
{"version":"v9","nodes":[
 {"name":"eyes","vision":true,"tier":null,"fallback":false,"visionMode":"off"},
 {"name":"eyes-b","vision":true,"tier":null,"fallback":false}
]}
'@ | ConvertFrom-Json

$noVision = @'
{"version":"v1","nodes":[{"name":"hub","vision":false,"tier":"heavy","fallback":true}]}
'@ | ConvertFrom-Json

Write-Host 'Current mode'
Assert-Equal (Get-NodeVisionMode $oneVision.nodes[1]) 'always' 'a stored always is read as always'
Assert-Equal (Get-NodeVisionMode $twoVision.nodes[1]) 'ondemand' 'a vision machine with no mode reads as on demand'

Write-Host 'Choosing the machine'
Assert-Equal (Select-VisionNode $oneVision.nodes '').name 'eyes' 'the only vision machine is chosen without -Node'
Assert-Equal (Select-VisionNode $twoVision.nodes 'eyes-b').name 'eyes-b' 'the named machine is chosen when there are two'
Assert-Equal (Select-VisionNode $twoVision.nodes 'eyes').name 'eyes' 'the first of two is chosen when it is named'
Assert-Throws { Select-VisionNode $noVision.nodes '' } 'no vision machine' 'no vision machine is an error'
Assert-Throws { Select-VisionNode $twoVision.nodes '' } 'Pass -Node' 'two vision machines and no -Node is an error'
Assert-Throws { Select-VisionNode $twoVision.nodes 'nobody' } "no vision machine called 'nobody'" 'an unknown -Node is an error'

Write-Host 'The body that is saved'
$body = New-FleetVisionPutBody $twoVision 'eyes-b' 'always' | ConvertFrom-Json
Assert-Equal $body.ifVersion 'v9' 'the version that was read is sent as ifVersion'
Assert-Equal $body.nodes[0].visionMode 'off' 'the other vision machine keeps its mode'
Assert-Equal $body.nodes[1].visionMode 'always' 'the chosen machine gets the new mode'
Assert-Equal $body.nodes[1].name 'eyes-b' 'the chosen machine is the one written'

$keep = New-FleetVisionPutBody $oneVision 'eyes' 'off' | ConvertFrom-Json
Assert-Equal $keep.ifVersion 'v7' 'ifVersion is the version of the configuration read'
Assert-Equal $keep.nodes[0].thinking 'model' 'a text node keeps its settings'
Assert-Equal $keep.nodes[0].PSObject.Properties.Name.Count 5 'a text node is sent with the same fields as it was read'
Assert-Equal $keep.nodes[1].visionMode 'off' 'the vision machine gets the new mode'
Assert-Equal $keep.nodes[2].name 'reader' 'the other machines are kept, in order'

Write-Host 'Add-FleetNode keeps the mode of an existing vision machine'
$addPath = Join-Path $PSScriptRoot 'Add-FleetNode.ps1'
$addAst = [Management.Automation.Language.Parser]::ParseFile($addPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Add-FleetNode.ps1 has syntax errors.' }
$copyDefinition = $addAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-FleetNodeForSave' }, $true)
if (-not $copyDefinition) { throw 'ConvertTo-FleetNodeForSave was not found in Add-FleetNode.ps1.' }
Invoke-Expression $copyDefinition.Extent.Text
$copied = ConvertTo-FleetNodeForSave $oneVision.nodes[1]
Assert-Equal $copied.visionMode 'always' 'an existing vision machine keeps its mode'
Assert-Equal $copied.vision $true 'an existing vision machine stays a vision machine'
$copiedText = ConvertTo-FleetNodeForSave $oneVision.nodes[0]
Assert-Equal ($copiedText.Contains('visionMode')) $false 'a text machine gets no mode'

Write-Host 'Add-FleetNode keeps every setting of an existing node'
$rich = @'
{"version":"v3","nodes":[
 {"name":"eyes","url":"http://192.0.2.31:11434/v1","model":"vision-m","purpose":"Eyes.","vision":true,"tier":null,"fallback":false,"visionMode":"always","contextLength":8192,"api":"ollama","caveman":"lite","ponytail":"full","thinking":"model","workspace":null,"clearWorkspace":false},
 {"name":"worker-a","url":"http://192.0.2.32:11434/v1","model":"text-m","purpose":"Worker.","vision":false,"tier":"standard","fallback":true,"contextLength":16384,"api":"openai","caveman":"ultra","ponytail":"lite","thinking":"off","workspace":{"host":"192.0.2.40","user":"agentfleet","keyPath":"/keys/example","hostKey":"SHA256:example","root":"/home/agentfleet/workspaces","platform":"linux","port":2222}}
]}
'@ | ConvertFrom-Json
$keptEyes = ConvertTo-FleetNodeForSave $rich.nodes[0]
Assert-Equal $keptEyes.visionMode 'always' 'a vision machine keeps its mode'
Assert-Equal $keptEyes.contextLength 8192 'a vision machine keeps its context length'
Assert-Equal $keptEyes.api 'ollama' 'a vision machine keeps its API'
Assert-Equal $keptEyes.caveman 'lite' 'a vision machine keeps its caveman style'
Assert-Equal $keptEyes.ponytail 'full' 'a vision machine keeps its ponytail style'
Assert-Equal $keptEyes.thinking 'model' 'a vision machine keeps its thinking setting'
$keptWorker = ConvertTo-FleetNodeForSave $rich.nodes[1]
Assert-Equal $keptWorker.contextLength 16384 'a text machine keeps its context length'
Assert-Equal $keptWorker.api 'openai' 'a text machine keeps its API'
Assert-Equal $keptWorker.caveman 'ultra' 'a text machine keeps its caveman style'
Assert-Equal $keptWorker.ponytail 'lite' 'a text machine keeps its ponytail style'
Assert-Equal $keptWorker.thinking 'off' 'a text machine keeps its thinking setting'
Assert-Equal $keptWorker.fallback $true 'a text machine keeps its fallback flag as read'
Assert-Equal $keptWorker.workspace.host '192.0.2.40' 'a text machine keeps its workspace host'
Assert-Equal $keptWorker.workspace.port 2222 'a text machine keeps its workspace port'
Assert-Equal $keptWorker.Count $rich.nodes[1].PSObject.Properties.Name.Count 'every field of the node as read is sent back'

Write-Host 'Meaning'
Assert-Equal ((Get-VisionModeMeaning 'off') -like '*note*') $true 'off says that images get a note'
Assert-Equal ((Get-VisionModeMeaning 'ondemand') -like '*leaves*') $true 'on demand says it leaves when idle'

if ($failures -gt 0) { Write-Host "`n$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll Set-FleetVision checks passed." -ForegroundColor Green
exit 0
