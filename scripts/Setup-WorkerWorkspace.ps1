[CmdletBinding()]
param(
    [string]$UserName = 'agentfleet',
    [Parameter(Mandatory)][string]$PublicKey,
    [string]$WorkspaceRoot,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $installRoot = if ($env:AGENT_FLEET_INSTALL_ROOT) { $env:AGENT_FLEET_INSTALL_ROOT } else { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentFleet' }
    $WorkspaceRoot = Join-Path $installRoot 'Workspaces'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $DryRun -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell. It creates a dedicated standard account and does not change firewall rules.'
}
if ($UserName -notmatch '^[a-zA-Z0-9_.-]{1,32}$') { throw 'Use a simple local account name.' }
if ($PublicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+)\s+\S+') { throw 'Pass one complete SSH public key; never pass the private key.' }
if ($WorkspaceRoot -notmatch '^[A-Za-z]:\\') { throw 'WorkspaceRoot must be a fully qualified local path.' }
$WorkspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
if ($WorkspaceRoot -notmatch '(?i)\\AgentFleet\\Workspaces$') {
    throw 'WorkspaceRoot must be a dedicated AgentFleet\Workspaces folder (for example C:\fleet-test\workspaces); do not apply its ACL to a broader directory.'
}
for ($cursor = $WorkspaceRoot; $cursor; $cursor = Split-Path -Parent $cursor) {
    if (Test-Path -LiteralPath $cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "WorkspaceRoot cannot pass through a junction or symbolic link: $cursor"
        }
    }
    if ((Split-Path -Parent $cursor) -eq $cursor) { break }
}
if (-not $DryRun) {
    $sshd = Get-Service sshd -ErrorAction SilentlyContinue
    if (-not $sshd -or $sshd.Status -ne 'Running') { throw 'OpenSSH Server (sshd) must already be running; this script does not alter services or firewall rules.' }
}

$user = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if ($DryRun) {
    $action = if ($user) { "secure existing standard account '$UserName'" } else { "create standard account '$UserName'" }
    Write-Host "Would $action, install the supplied public key, and grant workspace access to $WorkspaceRoot."
    Write-Host 'No files, accounts, services, or firewall rules were changed.'
    return
}

if (-not $user) {
    $bytes = New-Object byte[] 36
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $random = [Convert]::ToBase64String($bytes) + 'aA1!'
    [Array]::Clear($bytes, 0, $bytes.Length)
    $password = ConvertTo-SecureString $random -AsPlainText -Force
    $random = $null
    $user = New-LocalUser -Name $UserName -Password $password -PasswordNeverExpires -UserMayNotChangePassword `
        -Description 'Agent Fleet worker workspace account'
    $password.Dispose()
}

$sid = $user.SID.Value
$adminSid = 'S-1-5-32-544'
$adminMembers = @(Get-LocalGroupMember -SID $adminSid -ErrorAction Stop | ForEach-Object { $_.SID.Value })
if ($adminMembers -contains $sid) { throw "The existing '$UserName' account is an administrator. Remove it from Administrators before using it for worker tools." }

$profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
$profilePath = (Get-ItemProperty $profileKey -Name ProfileImagePath -ErrorAction SilentlyContinue).ProfileImagePath
if (-not $profilePath) {
    if (-not ('AgentFleetUserProfileNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class AgentFleetUserProfileNative {
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CreateProfile(string sid, string userName, StringBuilder profilePath, uint pathLength);
}
'@
    }
    $profileBuffer = [Text.StringBuilder]::new(260)
    $result = [AgentFleetUserProfileNative]::CreateProfile($sid, $UserName, $profileBuffer, 260)
    if ($result -eq 0) {
        $profilePath = $profileBuffer.ToString()
    } else {
        $profilePath = (Get-ItemProperty $profileKey -Name ProfileImagePath -ErrorAction SilentlyContinue).ProfileImagePath
        if (-not $profilePath) { throw "Could not create the worker account profile (HRESULT 0x$($result.ToString('X8')))." }
    }
}
$sshDir = Join-Path $profilePath '.ssh'
$authorizedKeys = Join-Path $sshDir 'authorized_keys'
New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
if (-not (Test-Path $authorizedKeys) -or -not (Select-String -LiteralPath $authorizedKeys -SimpleMatch $PublicKey -Quiet)) {
    Add-Content -LiteralPath $authorizedKeys -Value $PublicKey -Encoding ascii
}

foreach ($path in @($profilePath, $sshDir)) {
    & icacls.exe $path /setowner "*$sid" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set worker ownership on $path." }
    & icacls.exe $path /inheritance:r /grant:r "*$($sid):(OI)(CI)F" 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set private account permissions on $path." }
}
& icacls.exe $authorizedKeys /setowner "*$sid" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set worker ownership on authorized_keys.' }
& icacls.exe $authorizedKeys /inheritance:r /grant:r "*$($sid):F" 'SYSTEM:F' 'BUILTIN\Administrators:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure authorized_keys.' }

New-Item -ItemType Directory -Path $WorkspaceRoot -Force | Out-Null
& icacls.exe $WorkspaceRoot /inheritance:r /grant:r "*$($sid):(OI)(CI)M" 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not set workspace permissions on $WorkspaceRoot." }

Write-Host "Ready: standard account '$UserName' can SSH with the supplied public key and write under $WorkspaceRoot."
Write-Host 'Configure the matching SSH key path and the verified SHA256 host fingerprint in Fleet > Config > Machines.'
