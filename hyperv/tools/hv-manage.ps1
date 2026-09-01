# Hyper-V management helper for hosts without the Hyper-V PowerShell module.
# Usage: .\hv-manage.ps1 list | start <name> | stop <name> | state <name>
param(
    [Parameter(Position = 0)][string]$Action = 'list',
    [Parameter(Position = 1)][string]$Name
)
$ErrorActionPreference = 'Stop'
$ns = 'root\virtualization\v2'
$vmms = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
$states = @{ 2 = 'Running'; 3 = 'Off'; 4 = 'Saved'; 6 = 'Stopping'; 9 = 'Starting'; 32768 = 'Paused'; 32769 = 'Suspended'; 32771 = 'Critial' }

function Find-VM([string]$n) {
    $vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem |
        Where-Object { $_.ElementName -eq $n -and $_.Caption -match 'Virtual' }
    if (-not $vm) { throw "VM '$n' not found" }
    return $vm
}

switch ($Action) {
    'list' {
        Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem |
            Where-Object Caption -match 'Virtual' |
            ForEach-Object { '{0}  [{1}]' -f $_.ElementName, $states[[int]$_.EnabledState] }
    }
    'state' {
        $vm = Find-VM $Name
        '{0}  [{1}]' -f $vm.ElementName, $states[[int]$vm.EnabledState]
    }
    'start' {
        $vm = Find-VM $Name
        $r = Invoke-CimMethod -InputObject $vm -MethodName RequestStateChange -Arguments @{ RequestedState = 2 }
        'start: return ' + $r.ReturnValue
    }
    'stop' {
        $vm = Find-VM $Name
        $r = Invoke-CimMethod -InputObject $vm -MethodName RequestStateChange -Arguments @{ RequestedState = 3 }
        'stop: return ' + $r.ReturnValue
    }
    default { 'actions: list | state <name> | start <name> | stop <name>' }
}
