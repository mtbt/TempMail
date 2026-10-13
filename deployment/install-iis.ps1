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
$existingService = Get-CimInstance Win32_Service -Filter "Name='TempMailSmtp'"
if ($existingService) {
    $smtpPath = Split-Path -Path (Get-TempMailServiceExecutable $existingService) -Parent
    Assert-DeploymentPaths @($PublishPath, $SitePath, $StoragePath, $KeyPath, $smtpPath)
}
$CertificateThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
if ($CertificateThumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Supply a SHA-1 certificate thumbprint.' }
if ($HostName -notmatch '^(?=.{1,253}$)[A-Za-z0-9]+([.-][A-Za-z0-9]+)*$') { throw 'Supply a DNS hostname without port or wildcard.' }
if (-not (Test-Path -LiteralPath "$PublishPath\TempMail.Web.dll" -PathType Leaf) -or -not (Test-Path -LiteralPath "$PublishPath\web.config" -PathType Leaf)) { throw 'Publish the Web project first.' }
if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) { throw 'HTTPS certificate missing from LocalMachine\My.' }
$certificate = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'HTTPS certificate must be current and have a private key.' }
if ($HostingBundleInstaller) {
    if (-not (Test-Path -LiteralPath $HostingBundleInstaller -PathType Leaf)) { throw 'Hosting Bundle installer missing.' }
    $HostingBundleInstaller = (Get-Item -LiteralPath $HostingBundleInstaller).FullName
    $signature = Get-AuthenticodeSignature -LiteralPath $HostingBundleInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Hosting Bundle signature must be valid and signed by Microsoft.' }
}
# Inspect existing IIS before installing features/bundles or stopping production.
if (Get-Module -ListAvailable WebAdministration) {
    # A rerun in the same session must not reuse a provider snapshot from before a commit.
    if (Get-Module WebAdministration) { Remove-Module WebAdministration -ErrorAction Stop }
    Import-Module WebAdministration
    Assert-TempMailIisConfiguration -SitePath $SitePath -HostName $HostName
    if (-not $HostingBundleInstaller -and -not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 10 Hosting Bundle (after IIS), then rerun.' }
} else {
    if ($existingService -or (Get-Service WAS, W3SVC -ErrorAction SilentlyContinue)) { throw 'Install/repair IIS management tools first; existing deployment cannot be validated safely.' }
    if (-not $HostingBundleInstaller) { throw 'On a clean server, supply the signed Hosting Bundle installer or install IIS and the Hosting Bundle first.' }
}
# Prerequisite installation can require a reboot. No TempMail stop/copy/ACL has
# occurred; inspect newly installed IIS/ANCM again before application deployment.
$features = Install-WindowsFeature Web-Server, Web-WebSockets, Web-AppInit, Web-Mgmt-Console -IncludeManagementTools
if (-not $features.Success) { throw 'IIS feature installation failed.' }
if ($features.RestartNeeded -eq 'Yes') { throw 'Restart Windows after feature installation, then rerun.' }
if ($HostingBundleInstaller) {
    $p = Start-Process -FilePath $HostingBundleInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin @(0, 3010)) { throw "Hosting Bundle failed: $($p.ExitCode)" }
    if ($p.ExitCode -eq 3010) { throw 'Hosting Bundle requires a restart; restart Windows and rerun.' }
}
Import-Module WebAdministration
if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 10 Hosting Bundle (after IIS), then rerun.' }
Assert-TempMailIisConfiguration -SitePath $SitePath -HostName $HostName
# Windows PowerShell does not reliably load this assembly when importing the provider.
$administrationAssembly = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
if (-not (Test-Path -LiteralPath $administrationAssembly -PathType Leaf)) {
    throw "IIS administration assembly missing: $administrationAssembly. Install/repair IIS management tools and rerun in 64-bit Windows PowerShell."
}
try {
    Add-Type -Path $administrationAssembly -ErrorAction Stop
} catch {
    throw "Unable to load IIS administration assembly '$administrationAssembly': $($_.Exception.Message)"
}
# Replacing shared ACLs requires a maintenance window for both applications.
Stop-TempMailService
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
# Configure the site and bindings through one ServerManager snapshot. Do not use
# the IIS provider after this commit: its cached snapshot can omit the new site.
$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    $site = $manager.Sites['TempMail']
    if ($null -eq $site) {
        $site = $manager.Sites.Add('TempMail', 'http', "*:80:$HostName", $SitePath)
        $site.Applications['/'].ApplicationPoolName = 'TempMail'
    }
    # Create disabled in one commit: never briefly serve an unconfigured app.
    # Existing sites and the pool have already been stopped/disabled above.
    $site.ServerAutoStart = $false
    $site.ApplicationDefaults['preloadEnabled'] = $true
    $httpsBinding = $site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq "*:443:$HostName" }
    if ($null -eq $httpsBinding) {
        $httpsBinding = $site.Bindings.Add("*:443:$HostName", 'https')
    }
    $httpsBinding.SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni
    $httpsBinding.CertificateHash = $certificate.GetCertHash()
    $httpsBinding.CertificateStoreName = 'My'
    $manager.CommitChanges()
} finally { $manager.Dispose() }
Set-TempMailDirectoryAcl -Path $SitePath -ReadExecute 'IIS AppPool\TempMail'
Set-TempMailDirectoryAcl -Path "$SitePath\logs" -Modify 'IIS AppPool\TempMail'
$storageIdentities = @('IIS AppPool\TempMail')
if (Get-Service TempMailSmtp -ErrorAction SilentlyContinue) { $storageIdentities += 'NT SERVICE\TempMailSmtp' }
Set-TempMailDirectoryAcl -Path $StoragePath -Modify $storageIdentities
Set-TempMailDirectoryAcl -Path $KeyPath -Modify 'IIS AppPool\TempMail'
Write-Host 'Configure connection string, AllowedHosts, storage/key paths and Security__IpHashKey securely. Apply migrations and bootstrap before starting the site.'
Write-Host 'After acceptance: enable serverAutoStart, start the pool and site, then verify /health/ready over HTTPS. Restart SMTP only after its configuration is verified.'
