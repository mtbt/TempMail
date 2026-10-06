#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
foreach ($port in @(25, 80, 443)) {
    $name = "TempMail-TCP-$port"
    if (-not (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Any | Out-Null
    }
}
# Do not expose SQL Server (1433) to the Internet.
