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
. "$PSScriptRoot\common.ps1"
Assert-DeploymentPaths @($PublishPath, $SitePath, $StoragePath, $KeyPath)
$CertificateThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
if ($CertificateThumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Supply a SHA-1 certificate thumbprint.' }
if ($HostName -notmatch '^(?=.{1,253}$)[A-Za-z0-9]+([.-][A-Za-z0-9]+)*$') { throw 'Supply a DNS hostname without port or wildcard.' }
if (-not (Test-Path "$PublishPath\TempMail.Web.dll") -or -not (Test-Path "$PublishPath\web.config")) { throw 'Publish the Web project first.' }
# Replacing shared ACLs requires a maintenance window for both applications.
Stop-TempMailService
$features = Install-WindowsFeature Web-Server, Web-WebSockets, Web-AppInit, Web-Mgmt-Console -IncludeManagementTools
if (-not $features.Success) { throw 'IIS feature installation failed.' }
if ($features.RestartNeeded -eq 'Yes') { throw 'Restart Windows after feature installation, then rerun.' }
if ($HostingBundleInstaller) {
    $signature = Get-AuthenticodeSignature $HostingBundleInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Hosting Bundle signature must be valid and signed by Microsoft.' }
    $p = Start-Process -FilePath $HostingBundleInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin @(0, 3010)) { throw "Hosting Bundle failed: $($p.ExitCode)" }
    if ($p.ExitCode -eq 3010) { throw 'Hosting Bundle requires a restart; restart Windows and rerun.' }
}
Import-Module WebAdministration
if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 10 Hosting Bundle (after IIS), then rerun.' }
if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) { throw 'HTTPS certificate missing from LocalMachine\My.' }
$certificate = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'HTTPS certificate must be current and have a private key.' }
if (Test-Path 'IIS:\Sites\TempMail') {
    $site = Get-Item 'IIS:\Sites\TempMail'
    if ([IO.Path]::GetFullPath($site.physicalPath).TrimEnd('\') -ne [IO.Path]::GetFullPath($SitePath).TrimEnd('\') -or $site.applicationPool -ne 'TempMail') { throw 'Existing site path/pool differs. Review a migration manually.' }
    foreach ($binding in Get-WebBinding -Name TempMail) {
        if ($binding.protocol -notin @('http', 'https') -or $binding.bindingInformation -notin @("*:80:$HostName", "*:443:$HostName")) { throw 'Unexpected existing site bindings; review manually before deployment.' }
    }
}
if (Test-Path 'IIS:\AppPools\TempMail') {
    if ((Get-Item 'IIS:\AppPools\TempMail').processModel.identityType -ne 'ApplicationPoolIdentity') { throw 'Existing pool identity differs; review SQL, certificate and directory permissions manually.' }
}
Stop-TempMailWeb
foreach ($path in @($SitePath, $StoragePath, $KeyPath, "$SitePath\logs")) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
if (Test-Path 'IIS:\Sites\TempMail') { Stop-Website TempMail }
robocopy $PublishPath $SitePath /E /XJ /R:2 /W:2 /XF appsettings.Local.json *.pfx *.p12 *.key *.pem /XD logs | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Copy failed.' }
if (-not (Test-Path 'IIS:\AppPools\TempMail')) { New-WebAppPool TempMail | Out-Null }
Stop-TempMailWeb
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name managedRuntimeVersion -Value ''
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.identityType -Value 4
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.loadUserProfile -Value $true
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.maxProcesses -Value 1
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
# Single Web worker is required by the database notification dispatcher. Avoid overlapping recycles.
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name recycling.disallowOverlappingRotation -Value $true
# Create disabled in one configuration commit: never briefly serve an unconfigured app.
if (-not (Test-Path 'IIS:\Sites\TempMail')) {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $newSite = $manager.Sites.Add('TempMail', 'http', "*:80:$HostName", $SitePath)
        $newSite.ServerAutoStart = $false
        $newSite.Applications['/'].ApplicationPoolName = 'TempMail'
        $manager.CommitChanges()
    } finally { $manager.Dispose() }
}
Set-ItemProperty 'IIS:\Sites\TempMail' -Name serverAutoStart -Value $false
Stop-Website TempMail
Set-ItemProperty 'IIS:\Sites\TempMail' -Name applicationDefaults.preloadEnabled -Value $true
if (-not (Get-WebBinding -Name TempMail -Protocol https)) { New-WebBinding -Name TempMail -Protocol https -Port 443 -HostHeader $HostName -SslFlags 1 }
Set-WebBinding -Name TempMail -BindingInformation "*:443:$HostName" -PropertyName sslFlags -Value 1
(Get-WebBinding -Name TempMail -Protocol https).AddSslCertificate($CertificateThumbprint, 'My')
Set-TempMailDirectoryAcl -Path $SitePath -ReadExecute 'IIS AppPool\TempMail'
Set-TempMailDirectoryAcl -Path "$SitePath\logs" -Modify 'IIS AppPool\TempMail'
$storageIdentities = @('IIS AppPool\TempMail')
if (Get-Service TempMailSmtp -ErrorAction SilentlyContinue) { $storageIdentities += 'NT SERVICE\TempMailSmtp' }
Set-TempMailDirectoryAcl -Path $StoragePath -Modify $storageIdentities
Set-TempMailDirectoryAcl -Path $KeyPath -Modify 'IIS AppPool\TempMail'
Write-Host 'Configure connection string, AllowedHosts, storage/key paths and Security__IpHashKey securely. Apply migrations and bootstrap before starting the site.'
Write-Host 'After acceptance: enable serverAutoStart, start the pool and site, then verify /health/ready over HTTPS. Restart SMTP only after its configuration is verified.'
