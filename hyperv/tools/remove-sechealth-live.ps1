$ErrorActionPreference = 'Continue'
$pw = ConvertTo-SecureString 'TinyWin2026Temp' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('TinyWin', $pw)
$code = @'
$store = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore"
$users = @("S-1-5-18")
$users += (Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore" -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "*S-1-5-21*" }).PSChildName
"users: " + ($users -join ", ")
$pkg = Get-AppxPackage -AllUsers | Where-Object { $_.Name -like "*SecHealthUI*" }
$prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like "*SecHealthUI*" }
if ($pkg) {
  $fam = $pkg.PackageFamilyName
  $full = $pkg.PackageFullName
  New-Item "$store\Deprovisioned\$fam" -Force | Out-Null
  foreach ($sid in $users) { New-Item "$store\EndOfLife\$sid\$full" -Force | Out-Null }
  dism /online /set-nonremovableapppolicy /packagefamily:$fam /nonremovable:0 | Out-Null
  "removing installed: $full"
  Remove-AppxPackage -Package $full -AllUsers
}
if ($prov) {
  $pn = $prov.PackageName
  foreach ($sid in $users) { New-Item "$store\EndOfLife\$sid\$pn" -Force | Out-Null }
  "removing provisioned: $pn"
  Remove-AppxProvisionedPackage -Online -PackageName $pn -AllUsers
}
Start-Sleep -Seconds 3
"final pkg count: " + ((Get-AppxPackage -AllUsers *SecHealthUI* | Measure-Object).Count)
"Start has 安全中心: " + ((Get-StartApps | Where-Object Name -eq "Windows 安全中心" | Measure-Object).Count)
'@
Invoke-Command -VMName TinyWin2-VMSmoke -Credential $cred -ScriptBlock { param($s) Invoke-Expression $s } -ArgumentList $code
'SecHealthUI reference-flow removal done'
