$ErrorActionPreference = 'Continue'
$pw = ConvertTo-SecureString 'TinyWin2026Temp' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential ('TINYWIN2-HV\TinyWin', $pw)

$opt = New-CimSessionOption -Protocol DCOM
try {
    $session = New-CimSession -ComputerName tinywin2-hv -Credential $cred -SessionOption $opt -ErrorAction Stop
    'DCOM session: OK'
    $systems = Get-CimInstance -CimSession $session -Namespace 'root\virtualization\v2' -ClassName Msvm_ComputerSystem
    $systems | ForEach-Object { $_.ElementName + ' [state ' + $_.EnabledState + ']' }
    Remove-CimSession $session
    '=> DCOM/WMI channel works; Hyper-V Manager GUI should connect with these credentials.'
}
catch {
    'DCOM failed: ' + $_.Exception.Message.Split([char]10)[0]
}
