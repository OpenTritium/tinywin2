$ErrorActionPreference = 'Stop'
$name = 'TinyWin2-ServerSmoke'

Get-VM $name -ErrorAction SilentlyContinue | Where-Object State -ne 'Off' |
    Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue
Get-VM $name -ErrorAction SilentlyContinue | Remove-VM -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Remove-Item F:\tinywin2\vm\ServerSmoke.vhdx -Force -ErrorAction SilentlyContinue

New-VM -Name $name -Generation 2 -MemoryStartupBytes 1536MB -NoVHD -Path 'F:\tinywin2\vm' | Out-Null
Set-VM -Name $name -ProcessorCount 4 -DynamicMemory -MemoryStartupBytes 1536MB `
    -MemoryMinimumBytes 512MB -MemoryMaximumBytes 8GB
Set-VMKeyProtector -VMName $name -NewLocalKeyProtector
Enable-VMTPM -VMName $name
Set-VMFirmware -VMName $name -SecureBootTemplate MicrosoftWindows
New-VHD -Path 'F:\tinywin2\vm\ServerSmoke.vhdx' -SizeBytes 80GB -Dynamic | Out-Null
Add-VMHardDiskDrive -VMName $name -Path 'F:\tinywin2\vm\ServerSmoke.vhdx'
Add-VMDvdDrive -VMName $name -Path 'F:\tinywin2\iso\tinywin2-dev-server2025.iso'
Set-VMFirmware -VMName $name -FirstBootDevice (Get-VMDvdDrive -VMName $name)
Set-VMProcessor -VMName $name -ExposeVirtualizationExtensions $true

Add-Type -MemberDefinition '[DllImport("ntdll.dll")] public static extern uint RtlAdjustPrivilege(int p, bool e, bool t, ref bool prev); [DllImport("ntdll.dll")] public static extern uint NtSetSystemInformation(int cls, ref int info, int len);' -Name Nt -Namespace Win32
[bool]$prev = $false
[Win32.Nt]::RtlAdjustPrivilege(13, $true, $false, [ref]$prev) | Out-Null
[int]$purge = 4
'purge: ' + [Win32.Nt]::NtSetSystemInformation(80, [ref]$purge, 4)
Start-VM -Name $name
(Get-VM $name).State
