$ErrorActionPreference = 'Continue'
$pw = ConvertTo-SecureString 'TinyWin2026Temp' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('TinyWin', $pw)
# make sure VM is running (start if off)
if ((Get-VM TinyWin2-VMSmoke).State -ne 'Running') {
    Set-VMFirmware -VMName TinyWin2-VMSmoke -FirstBootDevice (Get-VMHardDiskDrive -VMName TinyWin2-VMSmoke)
    Start-VM TinyWin2-VMSmoke
    Start-Sleep -Seconds 90
}
"applying bypass regs"
Invoke-Command -VMName TinyWin2-VMSmoke -Credential $cred -ScriptBlock {
    Set-ItemProperty "HKLM:\SYSTEM\Setup" -Name SetupType -Value 0 -Type DWord
    Set-ItemProperty "HKLM:\SYSTEM\Setup" -Name SystemSetupInProgress -Value 0 -Type DWord
    Set-ItemProperty "HKLM:\SYSTEM\Setup" -Name SetupPhase -Value 0 -Type DWord
    Remove-ItemProperty "HKLM:\SYSTEM\Setup" -Name CmdLine -ErrorAction SilentlyContinue
    Set-ItemProperty "HKLM:\SYSTEM\Setup" -Name OOBEInProgress -Value 0 -Type DWord
    $wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty $wl -Name AutoAdminLogon -Value 1 -Type String
    Set-ItemProperty $wl -Name DefaultUserName -Value TinyWin -Type String
    Set-ItemProperty $wl -Name DefaultPassword -Value TinyWin2026Temp -Type String
    $oobe = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\OOBE"
    if (-not (Test-Path $oobe)) { New-Item $oobe -Force | Out-Null }
    foreach ($n in "SetupDisplayedEULA","SetupDisplayedProductKey","SetupDisplayedLanguageSelection","SetupDisplayedPrivacyPolicy","SetupDisplayedRegionSelection","UnattendCreatedUser") {
        Set-ItemProperty $oobe -Name $n -Value 1 -Type DWord
    }
    "regs written; flushing via clean restart"
    Restart-Computer -Force
}
"guest restarting; waiting"
Start-Sleep -Seconds 100
$vm = Get-VM TinyWin2-VMSmoke
"State: " + $vm.State + " Uptime: " + $vm.Uptime
if ($vm.State -eq 'Off') {
    "fixing boot order and starting"
    Set-VMFirmware -VMName TinyWin2-VMSmoke -FirstBootDevice (Get-VMHardDiskDrive -VMName TinyWin2-VMSmoke)
    Start-VM TinyWin2-VMSmoke
    Start-Sleep -Seconds 120
    $vm = Get-VM TinyWin2-VMSmoke
    "State: " + $vm.State + " Uptime: " + $vm.Uptime
}
$hb = Get-VMIntegrationService -VMName TinyWin2-VMSmoke -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'Heartbeat' }
"Heartbeat: " + $hb.PrimaryStatusDescription
'--- bypass clean cycle done ---'
