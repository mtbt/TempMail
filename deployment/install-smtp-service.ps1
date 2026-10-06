#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishPath, [string]$ServicePath = 'C:\Services\TempMail.Smtp', [string]$StoragePath = 'D:\TempMailStorage')
$ErrorActionPreference = 'Stop'
$serviceName = 'TempMailSmtp'
if (-not (Test-Path "$PublishPath\TempMail.SmtpServer.exe")) { throw 'Publish with -r win-x64 --self-contained false first.' }
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName }
New-Item -ItemType Directory -Path $ServicePath, $StoragePath, "$ServicePath\logs" -Force | Out-Null
robocopy $PublishPath $ServicePath /E /XF appsettings.Local.json /XD logs | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Copy failed.' }
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    sc.exe create $serviceName binPath= "`"$ServicePath\TempMail.SmtpServer.exe`"" start= delayed-auto obj= "NT SERVICE\$serviceName" DisplayName= 'TempMail SMTP Service'
    if ($LASTEXITCODE -ne 0) { throw 'Service creation failed.' }
}
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
sc.exe failureflag $serviceName 1
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
foreach ($entry in @(@($ServicePath, '(OI)(CI)RX'), @("$ServicePath\logs", '(OI)(CI)M'), @($StoragePath, '(OI)(CI)M'))) {
    icacls $entry[0] /grant "NT SERVICE\${serviceName}:$($entry[1])" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ACL update failed.' }
}
Write-Host 'Configure appsettings.Local.json or service environment securely; use the same SQL database/storage as Web.'
Write-Host 'After migrations, seed and config verification: Start-Service TempMailSmtp'
