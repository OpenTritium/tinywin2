# SecHealthUI removal — validated live on vm-init v22 smoke (2026-10-17).
# Run elevated at first logon (Unattend FirstLogonCommands Order 7).
# Key requirement: EndOfLife markers must exist for EVERY user SID
# (S-1-5-18 + all S-1-5-21-*), otherwise Remove-AppxPackage fails with
# 0x80070032 "此应用是 Windows 的一部分".
$ErrorActionPreference = 'Continue'
$log = "$env:SystemRoot\Temp\sechealth-live.log"
"[$(Get-Date -Format s)] begin" | Out-File $log -Encoding utf8
$store = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore'
$users = @('S-1-5-18')
$users += (Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like '*S-1-5-21*' }).PSChildName
"sids: $($users -join ',')" | Out-File $log -Append -Encoding utf8

Get-AppxPackage -AllUsers | Where-Object { $_.Name -like '*SecHealthUI*' } | ForEach-Object {
    $fam = $_.PackageFamilyName
    $full = $_.PackageFullName
    New-Item "$store\Deprovisioned\$fam" -Force -ErrorAction SilentlyContinue | Out-Null
    foreach ($sid in $users) { New-Item "$store\EndOfLife\$sid\$full" -Force -ErrorAction SilentlyContinue | Out-Null }
    dism /online /set-nonremovableapppolicy /packagefamily:$fam /nonremovable:0 | Out-File $log -Append -Encoding utf8
    "removing $full" | Out-File $log -Append -Encoding utf8
    Remove-AppxPackage -Package $full -AllUsers -ErrorAction Continue
}
Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*SecHealthUI*' } | ForEach-Object {
    foreach ($sid in $users) { New-Item "$store\EndOfLife\$sid\$($_.PackageName)" -Force -ErrorAction SilentlyContinue | Out-Null }
    "removing provisioned $($_.PackageName)" | Out-File $log -Append -Encoding utf8
    Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null
}
"[$(Get-Date -Format s)] done, remaining: " + ((Get-AppxPackage -AllUsers *SecHealthUI* -ErrorAction SilentlyContinue | Measure-Object).Count) | Out-File $log -Append -Encoding utf8
