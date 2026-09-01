$ErrorActionPreference = 'Stop'
$vhdx = 'F:\tinywin2\vm\ServerSmoke.vhdx'

Stop-VM TinyWin2-ServerSmoke -TurnOff -Force
Start-Sleep -Seconds 3
Mount-VHD -Path $vhdx
$disk = (Get-VHD $vhdx).DiskNumber
$p = Get-Partition -DiskNumber $disk | Where-Object { $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' } | Select-Object -First 1
if (-not $p.DriveLetter) { Add-PartitionAccessPath -DiskNumber $disk -PartitionNumber $p.PartitionNumber -DriveLetter K }
$win = (Get-Partition -DiskNumber $disk | Where-Object { $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' }).DriveLetter + ':'
"windows at $win"

reg load HKLM\TmpSys "$win\Windows\System32\config\SYSTEM" | Out-Null
reg load HKLM\TmpSoft "$win\Windows\System32\config\SOFTWARE" | Out-Null

# clear forced-setup state so winlogon stops launching msoobe
reg add HKLM\TmpSys\Setup /v SetupType /t REG_DWORD /d 0 /f | Out-Null
reg add HKLM\TmpSys\Setup /v SystemSetupInProgress /t REG_DWORD /d 0 /f | Out-Null
reg add HKLM\TmpSys\Setup /v SetupPhase /t REG_DWORD /d 0 /f | Out-Null
reg delete HKLM\TmpSys\Setup /v CmdLine /f 2>$null | Out-Null
reg add HKLM\TmpSys\Setup /v OOBEInProgress /t REG_DWORD /d 0 /f | Out-Null

# force console autologon as TinyWin (account created by unattend oobeSystem GC)
$wl = 'HKLM\TmpSoft\Microsoft\Windows NT\CurrentVersion\Winlogon'
reg add $wl /v AutoAdminLogon /t REG_SZ /d 1 /f | Out-Null
reg add $wl /v DefaultUserName /t REG_SZ /d TinyWin /f | Out-Null
reg add $wl /v DefaultPassword /t REG_SZ /d TinyWin2026Temp /f | Out-Null
reg add $wl /v DefaultDomainName /t REG_SZ /d TINYWIN2-HV /f | Out-Null
reg add $wl /v AutoLogonCount /t REG_DWORD /d 999 /f | Out-Null

# mark every OOBE page as already displayed
$oobe = 'HKLM\TmpSoft\Microsoft\Windows\CurrentVersion\Setup\OOBE'
reg add $oobe /v SetupDisplayedEULA /t REG_DWORD /d 1 /f | Out-Null
reg add $oobe /v SetupDisplayedProductKey /t REG_DWORD /d 1 /f | Out-Null
reg add $oobe /v SetupDisplayedLanguageSelection /t REG_DWORD /d 1 /f | Out-Null
reg add $oobe /v SetupDisplayedPrivacyPolicy /t REG_DWORD /d 1 /f | Out-Null
reg add $oobe /v SetupDisplayedRegionSelection /t REG_DWORD /d 1 /f | Out-Null
reg add $oobe /v UnattendCreatedUser /t REG_DWORD /d 1 /f | Out-Null

reg unload HKLM\TmpSys | Out-Null
reg unload HKLM\TmpSoft | Out-Null

Dismount-VHD -Path $vhdx
Start-VM TinyWin2-ServerSmoke
'setup state cleared; booting'
