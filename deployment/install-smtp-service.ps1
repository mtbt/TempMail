#Requires -RunAsAdministrator
#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishPath, [string]$ServicePath = 'C:\Services\TempMail.Smtp', [string]$StoragePath = 'D:\TempMailStorage', [string]$KeyPath = 'D:\TempMailKeys')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
Assert-DeploymentPaths @($PublishPath, $ServicePath, $StoragePath)
$serviceName = 'TempMailSmtp'
if (-not (Test-Path "$PublishPath\TempMail.SmtpServer.exe")) { throw 'Publish with -r win-x64 --self-contained false first.' }
$existing = Get-CimInstance Win32_Service -Filter "Name='TempMailSmtp'"
$binaryPath = "`"$ServicePath\TempMail.SmtpServer.exe`""
if ($existing -and ($existing.StartName -ne "NT SERVICE\$serviceName" -or $existing.PathName -ne $binaryPath)) { throw 'Existing service identity/path differs. Review migration manually; no identity is silently replaced.' }
Import-Module WebAdministration
if (-not (Test-Path 'IIS:\AppPools\TempMail')) { throw 'Install the IIS application pool first so shared storage ACLs can resolve both identities.' }
if (-not (Test-Path 'IIS:\Sites\TempMail')) { throw 'Install the IIS site first.' }
Assert-DeploymentPaths @($PublishPath, $ServicePath, $StoragePath, $KeyPath, (Get-Item 'IIS:\Sites\TempMail').physicalPath)
Stop-TempMailService
Stop-TempMailWeb
New-Item -ItemType Directory -Path $ServicePath, $StoragePath, "$ServicePath\logs" -Force | Out-Null
robocopy $PublishPath $ServicePath /E /XJ /R:2 /W:2 /XF appsettings.Local.json *.pfx *.p12 *.key *.pem /XD logs | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Copy failed.' }
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    sc.exe create $serviceName binPath= "`"$ServicePath\TempMail.SmtpServer.exe`"" start= delayed-auto obj= "NT SERVICE\$serviceName" DisplayName= 'TempMail SMTP Service'
    if ($LASTEXITCODE -ne 0) { throw 'Service creation failed.' }
}
sc.exe config $serviceName start= delayed-auto
if ($LASTEXITCODE -ne 0) { throw 'Automatic start configuration failed.' }
sc.exe sidtype $serviceName unrestricted
if ($LASTEXITCODE -ne 0) { throw 'Service SID configuration failed.' }
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
sc.exe failureflag $serviceName 1
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
Set-TempMailDirectoryAcl -Path $ServicePath -ReadExecute "NT SERVICE\$serviceName"
Set-TempMailDirectoryAcl -Path "$ServicePath\logs" -Modify "NT SERVICE\$serviceName"
Set-TempMailDirectoryAcl -Path $StoragePath -Modify @('IIS AppPool\TempMail', "NT SERVICE\$serviceName")
Write-Host 'Configure appsettings.Local.json or service environment securely; use the same SQL database/storage as Web.'
Write-Host 'After migrations, seed, certificate ACL and config verification: start the IIS pool/site and Start-Service TempMailSmtp. Both applications were stopped for deployment.'
