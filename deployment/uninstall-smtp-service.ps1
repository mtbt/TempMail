#Requires -RunAsAdministrator
#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
if ($PSCmdlet.ShouldProcess('TempMailSmtp', 'Stop and unregister service (preserve storage/configuration)')) {
    if (Get-Service TempMailSmtp -ErrorAction SilentlyContinue) { Stop-TempMailService; sc.exe delete TempMailSmtp; if ($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' } }
}
