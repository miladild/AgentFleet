# Get-HardwareInventory.ps1
# Collects precise hardware specs for AI workload planning.
# Run in PowerShell: powershell -ExecutionPolicy Bypass -File .\Get-HardwareInventory.ps1

$ErrorActionPreference = 'SilentlyContinue'
$report = [System.Text.StringBuilder]::new()

function Add-Line($text) { [void]$report.AppendLine($text) }

Add-Line "===== HARDWARE INVENTORY ====="
Add-Line "Generated: $(Get-Date)"
Add-Line ""

# OS
$os = Get-CimInstance Win32_OperatingSystem
Add-Line "--- OS ---"
Add-Line "Edition: $($os.Caption) (Build $($os.BuildNumber))"
Add-Line "Architecture: $($os.OSArchitecture)"
Add-Line ""

# CPU
$cpu = Get-CimInstance Win32_Processor
Add-Line "--- CPU ---"
foreach ($c in $cpu) {
    Add-Line "Model: $($c.Name.Trim())"
    Add-Line "Cores: $($c.NumberOfCores)  Logical processors: $($c.NumberOfLogicalProcessors)"
    Add-Line "Max clock: $($c.MaxClockSpeed) MHz"
}
Add-Line ""

# RAM
$ramModules = Get-CimInstance Win32_PhysicalMemory
$totalRamGB = [math]::Round(($ramModules | Measure-Object -Property Capacity -Sum).Sum / 1GB, 1)
Add-Line "--- RAM ---"
Add-Line "Total installed: $totalRamGB GB"
foreach ($m in $ramModules) {
    $capGB = [math]::Round($m.Capacity / 1GB, 1)
    Add-Line "  Stick: $capGB GB @ $($m.Speed) MHz ($($m.Manufacturer))"
}
Add-Line ""

# GPU
$gpus = Get-CimInstance Win32_VideoController
Add-Line "--- GPU ---"
foreach ($g in $gpus) {
    $vramGB = [math]::Round($g.AdapterRAM / 1GB, 1)
    Add-Line "Model: $($g.Name)"
    Add-Line "  Reported VRAM: $vramGB GB  Driver: $($g.DriverVersion)"
}

# NVIDIA-specific (accurate VRAM + CUDA info if present)
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    Add-Line ""
    Add-Line "--- NVIDIA (nvidia-smi) ---"
    $nvOut = & nvidia-smi --query-gpu=name,memory.total,driver_version,compute_cap --format=csv,noheader
    Add-Line $nvOut
}
Add-Line ""

# Disks
Add-Line "--- STORAGE ---"
$disks = Get-PhysicalDisk
foreach ($d in $disks) {
    $sizeGB = [math]::Round($d.Size / 1GB, 1)
    Add-Line "$($d.FriendlyName): $sizeGB GB, $($d.MediaType), Bus: $($d.BusType)"
}
Add-Line ""

$sysDrive = Get-PSDrive -Name C
$freeGB = [math]::Round($sysDrive.Free / 1GB, 1)
Add-Line "Free space on C: $freeGB GB"
Add-Line ""

# Network
Add-Line "--- NETWORK ADAPTERS ---"
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($a in $adapters) {
    Add-Line "$($a.Name): $($a.InterfaceDescription), Link speed: $($a.LinkSpeed)"
}
Add-Line ""

# Ollama check
# Note: invoking `ollama.exe` directly (e.g. `ollama list`) can launch the full
# desktop app on newer Ollama builds instead of returning text, hanging this script.
# Query the local HTTP API instead, which is instant and never touches the app.
Add-Line "--- OLLAMA ---"
$ollama = Get-Command ollama -ErrorAction SilentlyContinue
if ($ollama) {
    Add-Line "Installed: $($ollama.Source)"
    try {
        $tags = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3
        Add-Line "Models pulled:"
        foreach ($m in $tags.models) {
            $sizeGB = [math]::Round($m.size / 1GB, 2)
            Add-Line "  $($m.name): $sizeGB GB ($($m.details.parameter_size), $($m.details.quantization_level))"
        }
    } catch {
        Add-Line "Ollama server not responding on 127.0.0.1:11434 (service may not be running)."
    }
} else {
    Add-Line "Ollama not found in PATH."
}

$output = $report.ToString()
Write-Host $output

$outFile = Join-Path ([Environment]::GetFolderPath('Desktop')) "hardware-inventory.txt"
$output | Out-File -FilePath $outFile -Encoding utf8
Write-Host "`nSaved to $outFile. Compare it with the model sizes in docs/adding-machines.md to pick what this machine can run."
