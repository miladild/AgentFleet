# Optimize-AIWorkstation.ps1
# Frees up CPU/RAM/disk overhead for local AI workloads.
# Preview only:       powershell -ExecutionPolicy Bypass -File .\Optimize-AIWorkstation.ps1
# Apply safe tweaks:  powershell -ExecutionPolicy Bypass -File .\Optimize-AIWorkstation.ps1 -Apply
# Apply + optional:   powershell -ExecutionPolicy Bypass -File .\Optimize-AIWorkstation.ps1 -Apply -IncludeOptional
#
# This script NEVER touches Windows Defender, Windows Firewall, UAC, or Windows Update.
# Those stay on regardless of switches. Performance is not worth that trade-off.

param(
    [switch]$Apply,
    [switch]$IncludeOptional
)

function Step($name, [scriptblock]$action) {
    if ($Apply) {
        Write-Host "[applying] $name"
        try { & $action } catch { Write-Host "  failed: $_" -ForegroundColor Yellow }
    } else {
        Write-Host "[would apply] $name"
    }
}

Write-Host "===== AI workstation optimization ====="
Write-Host $(if ($Apply) { "Mode: APPLYING changes" } else { "Mode: PREVIEW ONLY (add -Apply to make changes)" })
Write-Host ""

Step "Set power plan to High performance" {
    powercfg -setactive SCHEME_MIN
}

Step "Enable hardware-accelerated GPU scheduling" {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" -Name "HwSchMode" -Value 2 -Type DWord -Force
}

Step "Disable Xbox Game Bar and Game DVR" {
    Set-ItemProperty -Path "HKCU:\System\GameConfigStore" -Name "GameDVR_Enabled" -Value 0 -Type DWord -Force
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\GameBar" -Name "AutoGameModeEnabled" -Value 0 -Type DWord -Force
}

Step "Disable background UWP apps" {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications" -Name "GlobalUserDisabled" -Value 1 -Type DWord -Force
}

Step "List startup apps (review, then trim manually via Task Manager > Startup)" {
    Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location | Format-Table -AutoSize | Out-String | Write-Host
}

if ($IncludeOptional) {
    Write-Host ""
    Write-Host "--- Optional (bigger tradeoff, read before applying) ---"

    Step "Disable SysMain or Superfetch. Skip this on spinning HDDs, where it still helps." {
        Stop-Service -Name SysMain -Force
        Set-Service -Name SysMain -StartupType Disabled
    }

    Step "Disable hibernation (frees disk space equal to installed RAM, loses fast-resume)" {
        powercfg -hibernate off
    }

    Step "Disable Connected User Experiences and Telemetry (DiagTrack)" {
        Stop-Service -Name DiagTrack -Force
        Set-Service -Name DiagTrack -StartupType Disabled
    }
}

Write-Host ""
Write-Host "Untouched on purpose: Windows Defender, Windows Firewall, UAC, Windows Update."
Write-Host "Done."
