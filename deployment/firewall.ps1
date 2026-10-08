#Requires -RunAsAdministrator
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet('Domain', 'Private', 'Public', 'Any')][string[]]$Profile = @('Any'))
$ErrorActionPreference = 'Stop'
foreach ($port in @(25, 80, 443)) {
    $name = "TempMail-TCP-$port"
    if ($PSCmdlet.ShouldProcess($name, 'Ensure enabled inbound TCP rule for this port')) {
        if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule -Name $name -Enabled True -Direction Inbound -Action Allow -Profile $Profile -Protocol TCP -LocalPort $port -RemotePort Any -LocalAddress Any -RemoteAddress Any | Out-Null
        } else {
            New-NetFirewallRule -Name $name -DisplayName $name -Enabled True -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile $Profile | Out-Null
        }
    }
}
# Do not expose SQL Server (1433) to the Internet.
