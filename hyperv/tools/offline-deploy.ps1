$ErrorActionPreference = 'Stop'
$name = 'TinyWin2-ServerSmoke'
$vhdx = 'F:\tinywin2\vm\ServerSmoke.vhdx'
$esd = 'F:\tinywin2\out\dev-server.esd'

Stop-VM $name -TurnOff -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Remove-Item $vhdx -Force -ErrorAction SilentlyContinue
New-VHD -Path $vhdx -SizeBytes 80GB -Dynamic | Out-Null
Mount-VHD -Path $vhdx
$disk = Get-VHD -Path $vhdx | ForEach-Object DiskNumber
Initialize-Disk -Number $disk -PartitionStyle GPT
$efi = New-Partition -DiskNumber $disk -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter
$msr = New-Partition -DiskNumber $disk -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'
$win = New-Partition -DiskNumber $disk -UseMaximumSize -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' -AssignDriveLetter
Format-Volume -DriveLetter ($efi.DriveLetter) -FileSystem FAT32 -NewFileSystemLabel System | Out-Null
Format-Volume -DriveLetter ($win.DriveLetter) -FileSystem NTFS -NewFileSystemLabel Windows | Out-Null
$w = $win.DriveLetter + ':'
$s = $efi.DriveLetter + ':'

dism /English /Apply-Image /ImageFile:$esd /Index:1 /ApplyDir:${w}\ | Select-Object -Last 3
bcdboot "${w}\Windows" /s $s /f UEFI | Select-Object -Last 1

# enable Hyper-V offline so setup's specialize pass sees Enabled (it otherwise
# payload-scrubs Disabled optional features on first boot) — payload ships in the ESD.
# core only: management features are payload-scrubbed (Resolved) and would fail the
# whole transaction atomically (0x800f0916)
dism /Image:${w}\ /English /Enable-Feature /FeatureName:Microsoft-Hyper-V /All /NoRestart | Select-Object -Last 2

New-Item -ItemType Directory -Path "${w}\Panther" -Force | Out-Null
Copy-Item 'F:\tinywin2\probe\Unattend-oobe.xml' "${w}\Panther\Unattend.xml" -Force
New-Item -ItemType Directory -Path "${w}\Windows\System32\Sysprep" -Force | Out-Null
Copy-Item 'F:\tinywin2\probe\Unattend-oobe.xml' "${w}\Windows\System32\Sysprep\Unattend.xml" -Force
# point setup at the unattend explicitly; msoobe otherwise ignored the Panther copy
reg load HKLM\TmpSystem "${w}\Windows\System32\config\SYSTEM" | Out-Null
reg add HKLM\TmpSystem\Setup /v UnattendFile /t REG_SZ /d "C:\Windows\Panther\Unattend.xml" /f | Out-Null
reg unload HKLM\TmpSystem | Out-Null
'copied unattend for specialize'

Dismount-VHD -Path $vhdx
Set-VM -Name $name -AutomaticCheckpointsEnabled $false
Set-VMFirmware -VMName $name -FirstBootDevice (Get-VMHardDiskDrive -VMName $name)
'deployed; booting from disk'
Start-VM $name
(Get-VM $name).State
