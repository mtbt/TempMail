#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ShouldProcess('TempMailSmtp', 'Stop and unregister service (preserve storage/configuration)')) {
    if (Get-Service TempMailSmtp -ErrorAction SilentlyContinue) { Stop-Service TempMailSmtp; sc.exe delete TempMailSmtp; if ($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' } }
}
