<#
.SYNOPSIS
    Installs Contoso Notes, a fake app for testing PackPilot.
.DESCRIPTION
    Creates C:\Program Files\Contoso Notes\ with a small program file, and registers it in
    Programs and Features ("Contoso Notes" 1.2.0) the way a real installer does.
    Fails on purpose if it runs in 32-bit PowerShell on 64-bit Windows, which proves that
    PackPilot's Install.ps1 relaunched it in 64-bit PowerShell.
    Uninstall-ContosoNotes.ps1 in the same folder removes it again.
#>
$ErrorActionPreference = 'Stop'
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Write-Output 'ERROR: running as a 32-bit process. Expected 64-bit.'
    exit 1
}

$dir = Join-Path $env:ProgramFiles 'Contoso Notes'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'notes.txt') -Value "Contoso Notes 1.2.0, installed $(Get-Date -Format s) by $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-ContosoNotes.ps1') -Destination $dir -Force

$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoNotes'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -ExecutionPolicy Bypass -NoProfile -File `"$dir\Uninstall-ContosoNotes.ps1`""
@{
    DisplayName = 'Contoso Notes'; DisplayVersion = '1.2.0'; Publisher = 'Contoso Ltd'
    InstallLocation = $dir; UninstallString = $uninstall; QuietUninstallString = $uninstall
}.GetEnumerator() | ForEach-Object { Set-ItemProperty -Path $key -Name $_.Key -Value $_.Value }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord

Write-Output "Installed Contoso Notes to $dir"
exit 0
