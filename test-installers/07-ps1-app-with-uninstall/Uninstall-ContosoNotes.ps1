<#
.SYNOPSIS
    Removes Contoso Notes (the fake test app from Install-ContosoNotes.ps1).
#>
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:ProgramFiles 'Contoso Notes'
Remove-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoNotes' -Recurse -Force -ErrorAction SilentlyContinue
# This script may be running from inside the folder it deletes, so remove the files, then the folder.
Get-ChildItem -LiteralPath $dir -ErrorAction SilentlyContinue | Where-Object { $_.FullName -ne $PSCommandPath } | Remove-Item -Recurse -Force
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Removed Contoso Notes'
exit 0
