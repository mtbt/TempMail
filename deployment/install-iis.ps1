#Requires -RunAsAdministrator
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishPath,
    [Parameter(Mandatory)][string]$HostName,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [string]$SitePath = 'C:\Sites\TempMail',
    [string]$StoragePath = 'D:\TempMailStorage',
    [string]$KeyPath = 'D:\TempMailKeys',
    [string]$HostingBundleInstaller
)
$ErrorActionPreference = 'Stop'
Install-WindowsFeature Web-Server, Web-WebSockets, Web-AppInit, Web-Mgmt-Console -IncludeManagementTools | Out-Null
if ($HostingBundleInstaller) {
    $signature = Get-AuthenticodeSignature $HostingBundleInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Hosting Bundle signature must be valid and signed by Microsoft.' }
    $p = Start-Process -FilePath $HostingBundleInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin @(0, 3010)) { throw "Hosting Bundle failed: $($p.ExitCode)" }
}
Import-Module WebAdministration
if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 10 Hosting Bundle (after IIS), then rerun.' }
if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) { throw 'HTTPS certificate missing from LocalMachine\My.' }
if (-not (Test-Path "$PublishPath\TempMail.Web.dll")) { throw 'Publish the Web project first.' }
foreach ($path in @($SitePath, $StoragePath, $KeyPath, "$SitePath\logs")) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
if (Test-Path 'IIS:\Sites\TempMail') { Stop-Website TempMail }
robocopy $PublishPath $SitePath /E /XF appsettings.Local.json /XD logs | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Copy failed.' }
if (-not (Test-Path 'IIS:\AppPools\TempMail')) { New-WebAppPool TempMail | Out-Null }
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name managedRuntimeVersion -Value ''
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.identityType -Value 4
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.loadUserProfile -Value $true
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.maxProcesses -Value 1
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
# Single Web worker is required by the database notification dispatcher. Avoid overlapping recycles.
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name recycling.disallowOverlappingRotation -Value $true
if (-not (Test-Path 'IIS:\Sites\TempMail')) { New-Website -Name TempMail -PhysicalPath $SitePath -ApplicationPool TempMail -Port 80 -HostHeader $HostName | Out-Null }
Stop-Website TempMail
Set-ItemProperty 'IIS:\Sites\TempMail' -Name applicationDefaults.preloadEnabled -Value $true
if (-not (Get-WebBinding -Name TempMail -Protocol https)) { New-WebBinding -Name TempMail -Protocol https -Port 443 -HostHeader $HostName -SslFlags 1 }
(Get-WebBinding -Name TempMail -Protocol https).AddSslCertificate($CertificateThumbprint, 'My')
foreach ($entry in @(@($SitePath, '(OI)(CI)RX'), @("$SitePath\logs", '(OI)(CI)M'), @($StoragePath, '(OI)(CI)M'), @($KeyPath, '(OI)(CI)M'))) {
    icacls $entry[0] /grant "IIS AppPool\TempMail:$($entry[1])" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ACL update failed: $($entry[0])" }
}
# Key directory must not inherit permissions from a broadly readable drive/folder.
icacls $KeyPath /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' 'IIS AppPool\TempMail:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Key directory ACL update failed.' }
Write-Host 'Configure connection string, AllowedHosts, storage/key paths and Security__IpHashKey securely. Apply migrations and bootstrap before starting the site.'
Write-Host 'Then run Start-Website TempMail. Verify /health/ready over HTTPS.'
