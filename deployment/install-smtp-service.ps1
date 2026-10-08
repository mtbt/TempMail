#Requires -RunAsAdministrator
#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishPath, [string]$ServicePath = 'C:\Services\TempMail.Smtp', [string]$StoragePath = 'D:\TempMailStorage', [string]$KeyPath = 'D:\TempMailKeys')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
Assert-DeploymentPaths @($PublishPath, $ServicePath, $StoragePath, $KeyPath)
$ServicePath = [IO.Path]::GetFullPath($ServicePath).TrimEnd('\')
$serviceName = 'TempMailSmtp'
if (-not (Test-Path -LiteralPath "$PublishPath\TempMail.SmtpServer.exe" -PathType Leaf)) { throw 'Publish with -r win-x64 --self-contained false first.' }
$existing = Get-CimInstance Win32_Service -Filter "Name='TempMailSmtp'"
$executable = Join-Path $ServicePath 'TempMail.SmtpServer.exe'
$binaryPath = '"' + $executable + '"'
if ($existing -and (Get-TempMailServiceExecutable $existing) -ne $executable) { throw 'Existing service path differs. Review migration manually.' }
Import-Module WebAdministration
if (-not (Test-Path 'IIS:\AppPools\TempMail')) { throw 'Install the IIS application pool first so shared storage ACLs can resolve both identities.' }
if (-not (Test-Path 'IIS:\Sites\TempMail')) { throw 'Install the IIS site first.' }
$sitePath = (Get-Item 'IIS:\Sites\TempMail').physicalPath
Assert-TempMailIisConfiguration -SitePath $sitePath
Assert-DeploymentPaths @($PublishPath, $ServicePath, $StoragePath, $KeyPath, $sitePath)
# All read-only validation above must pass before either process is stopped.
Stop-TempMailService
Stop-TempMailWeb
New-Item -ItemType Directory -Path $ServicePath, $StoragePath, "$ServicePath\logs" -Force | Out-Null
robocopy $PublishPath $ServicePath /E /XJ /R:2 /W:2 /XF appsettings.Local.json *.pfx *.p12 *.key *.pem /XD logs | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Copy failed.' }
if (-not $existing) {
    # CIM passes PathName as a string to SCM, not through native command parsing.
    # Keep a new registration manual until its files and ACLs are configured.
    $result = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
        Name = $serviceName; DisplayName = 'TempMail SMTP Service'; PathName = $binaryPath
        ServiceType = [byte]16; ErrorControl = [byte]1; StartMode = 'Manual'
        DesktopInteract = $false; StartName = "NT SERVICE\$serviceName"
    }
    if ($result.ReturnValue -ne 0) { throw "Service creation failed: $($result.ReturnValue)" }
} elseif ($existing.PathName -cne $binaryPath) {
    # Preflight proved this is the same executable and virtual identity. Repair
    # only PathName; never silently migrate the account or executable location.
    $result = Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{ PathName = $binaryPath }
    if ($result.ReturnValue -ne 0) { throw "Service path repair failed: $($result.ReturnValue)" }
}
Assert-TempMailServiceRegistration -BinaryPath $binaryPath
sc.exe sidtype $serviceName unrestricted
if ($LASTEXITCODE -ne 0) { throw 'Service SID configuration failed.' }
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
sc.exe failureflag $serviceName 1
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
Set-TempMailDirectoryAcl -Path $ServicePath -ReadExecute "NT SERVICE\$serviceName"
Set-TempMailDirectoryAcl -Path "$ServicePath\logs" -Modify "NT SERVICE\$serviceName"
Set-TempMailDirectoryAcl -Path $StoragePath -Modify @('IIS AppPool\TempMail', "NT SERVICE\$serviceName")
sc.exe config $serviceName start= delayed-auto
if ($LASTEXITCODE -ne 0) { throw 'Automatic start configuration failed.' }
Assert-TempMailServiceRegistration -BinaryPath $binaryPath -VerifyStartup
Write-Host 'Configure appsettings.Local.json or service environment securely; use the same SQL database/storage as Web.'
Write-Host 'After migrations, seed, certificate ACL and config verification: start the IIS pool/site and Start-Service TempMailSmtp. Both applications were stopped for deployment.'
