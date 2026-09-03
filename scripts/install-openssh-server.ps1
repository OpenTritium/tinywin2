<#
.SYNOPSIS
    One-command OpenSSH Server install for slimmed TinyWin2 images.

.DESCRIPTION
    The slim image removes the OpenSSH Server capability and the FoD metadata
    payload (a side effect of /ResetBase), so Add-WindowsCapability cannot
    resolve the name offline. This script restores an SSH server using
    Microsoft's official Win32-OpenSSH release from GitHub instead:

        powershell -ExecutionPolicy Bypass -File install-openssh-server.ps1

    Optional: pass -PublicKey to authorize a key for administrators
    (appended to C:\ProgramData\ssh\administrators_authorized_keys).
#>
param([string]$PublicKey)
$ErrorActionPreference = 'Stop'

$progress = 'Downloading latest Win32-OpenSSH release...'
Write-Host $progress
$release = Invoke-RestMethod 'https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases/latest'
$asset = $release.assets | Where-Object { $_.name -eq 'OpenSSH-Win64.zip' } | Select-Object -First 1
if (-not $asset) { throw 'OpenSSH-Win64.zip asset not found in the latest release.' }

$tmp = Join-Path $env:TEMP "openssh-$PID"
New-Item -ItemType Directory $tmp -Force | Out-Null
Invoke-WebRequest $asset.browser_download_url -OutFile "$tmp\OpenSSH-Win64.zip"
Expand-Archive "$tmp\OpenSSH-Win64.zip" 'C:\Program Files\' -Force
$dir = 'C:\Program Files\OpenSSH-Win64'

& powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\install-sshd.ps1"
& "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -A | Out-Null

if ($PublicKey) {
    $ak = 'C:\ProgramData\ssh\administrators_authorized_keys'
    Add-Content -Path $ak -Value $PublicKey -Encoding ascii
    icacls $ak /inheritance:r /grant 'SYSTEM:F' /grant 'BUILTIN\Administrators:F' | Out-Null
}

New-NetFirewallRule -Name 'OpenSSH-Server' -DisplayName 'OpenSSH Server' `
    -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null

Set-Service sshd -StartupType Automatic
Start-Service sshd
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

$listening = (Get-NetTCPConnection -LocalPort 22 -State Listen -ErrorAction SilentlyContinue) -ne $null
Write-Host "sshd: $((Get-Service sshd).Status), port 22 listening: $listening"
Write-Host 'connect with: ssh -i <key> TinyWin@<host>   (or any local account)'
