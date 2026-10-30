<#
.SYNOPSIS
    Succeeds but returns 3010 (restart required), to test restart return codes.
.DESCRIPTION
    Intune treats 3010 as a soft reboot: success, restart pending. PackPilot's Install.ps1
    still writes its detection marker, so the Sandbox test should PASS with
    "exit code 3010 (success, restart required)".
#>
$dir = Join-Path $env:ProgramData 'ContosoRestartTest'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'installed.txt') -Value (Get-Date -Format s)
Write-Output 'Installed. A restart is needed to finish.'
exit 3010
