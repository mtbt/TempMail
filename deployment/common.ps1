#Requires -Version 5.1
# Shared helpers for dedicated TempMail directories only. No credentials are accepted.
Set-StrictMode -Version Latest

function Assert-DeploymentPaths {
    param([string[]]$Paths)
    $normalized = @()
    foreach ($path in $Paths) {
        if ($path -notmatch '^[A-Za-z]:\\' -or $path.Contains('"')) { throw "Use an absolute local directory: $path" }
        $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
        if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'A drive root cannot be a deployment directory.' }
        foreach ($other in $normalized) {
            if ($full -eq $other -or $full.StartsWith($other + '\', [StringComparison]::OrdinalIgnoreCase) -or $other.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Publish, application, attachment and key directories must not overlap.'
            }
        }
        # Refuse junctions/symlinks in ancestors as well as the managed tree.
        $ancestor = $full
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $ancestor" }
            }
            $ancestor = Split-Path -Path $ancestor -Parent
        }
        if (Test-Path -LiteralPath $full) {
            if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Not a directory: $full" }
            if (Get-ChildItem -LiteralPath $full -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) {
                throw "Remove reparse points before deployment: $full"
            }
        }
        $normalized += $full
    }
}

function Set-TempMailDirectoryAcl {
    param([string]$Path, [string[]]$ReadExecute = @(), [string[]]$Modify = @())
    # Replace the DACL, including explicit broad grants; do not merely add permissions.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', $inherit, 'None', 'Allow'))
    }
    foreach ($identity in $ReadExecute) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'ReadAndExecute', $inherit, 'None', 'Allow'))
    }
    foreach ($identity in $Modify) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'Modify', $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    # Reset children to inherit the reviewed root policy, removing old explicit grants.
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
        & icacls.exe $child.FullName /reset /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Child ACL reset failed: $($child.FullName)" }
    }
}

function Stop-TempMailService {
    $service = Get-Service TempMailSmtp -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service TempMailSmtp -NoWait
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

function Stop-TempMailWeb {
    if (Test-Path 'IIS:\Sites\TempMail') {
        Set-ItemProperty 'IIS:\Sites\TempMail' -Name serverAutoStart -Value $false
        Stop-Website TempMail
    }
    if (Test-Path 'IIS:\AppPools\TempMail') {
        Set-ItemProperty 'IIS:\AppPools\TempMail' -Name autoStart -Value $false
        if ((Get-WebAppPoolState TempMail).Value -ne 'Stopped') { Stop-WebAppPool TempMail }
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-WebAppPoolState TempMail).Value -ne 'Stopped') {
            if ((Get-Date) -gt $deadline) { throw 'IIS pool did not stop within 60 seconds.' }
            Start-Sleep -Milliseconds 250
        }
    }
}
