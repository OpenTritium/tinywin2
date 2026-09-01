$ErrorActionPreference = 'Continue'
$ip = '172.25.151.204'
$unc = "\\$ip\Public"
$pw = ConvertTo-SecureString 'TinyWin2026Temp' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential ('TINYWIN2-HV\TinyWin', $pw)

"=== net view ==="
net view $ip 2>&1 | Out-String

"=== map + read ==="
New-SmbMapping -RemotePath $unc -Credential $cred | Format-List RemotePath, Status
Get-ChildItem $unc | Format-Table Name, Length
Get-Content "$unc\hello.txt"
"=== result: SMB share readable from host ==="
