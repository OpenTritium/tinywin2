$ErrorActionPreference = 'Continue'
$pw = ConvertTo-SecureString 'TinyWin2026Temp' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential ('TinyWin', $pw)
$code = @'
"=== 1. new-plan services ==="
Get-Service PolicyAgent, DisplayEnhancementService, W32Time, vmicrdv -ErrorAction SilentlyContinue |
  Format-Table Name, Status, StartType -AutoSize
"=== 2. SearchHost ==="
Test-Path "$env:WINDIR\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe"
"=== 3. per-user svc template UdkUserSvc ==="
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\UdkUserSvc' -ErrorAction SilentlyContinue).Start
"=== 4. prior keeps/params ==="
Get-Service sppsvc, LanmanServer, UALSVCS, WinRM, SysMain, WSearch -ErrorAction SilentlyContinue |
  Format-Table Name, Status, StartType -AutoSize
"=== 5. perf registry ==="
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl').Win32PrioritySeparation
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').WaitToKillServiceTimeout
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -ErrorAction SilentlyContinue).NtfsDisableLastAccessUpdate
(Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection' -ErrorAction SilentlyContinue).AllowTelemetry
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile').SystemResponsiveness
"=== 6. active power scheme ==="
powercfg /getactivescheme
"=== 7. wuauserv state (firstlogon cmd) ==="
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\wuauserv').Start
"=== 8. counts ==="
"processes: " + (Get-Process).Count
"services running: " + (Get-Service | Where-Object Status -eq 'Running').Count
"=== 9. process list ==="
Get-Process | Sort-Object Name | ForEach-Object { $_.Name } | Select-Object -Unique
'@
$bytes = [Text.Encoding]::Unicode.GetBytes($code)
$b64 = [Convert]::ToBase64String($bytes)
$sb = [ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64)))
Invoke-Command -VMName TinyWin2-ServerSmoke -Credential $cred -ScriptBlock { param($script) Invoke-Expression $script } -ArgumentList $code
