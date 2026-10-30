<#
.SYNOPSIS
    Always fails with exit code 1603 (fatal error), to test how PackPilot reports a failed install.
.DESCRIPTION
    Expected Sandbox result: FAIL, with "Install: exit code 1603" and detection not finding the app.
#>
Write-Output 'Simulating a failed installation (missing prerequisite).'
exit 1603
