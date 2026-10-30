<#
.SYNOPSIS
    Never finishes, like an installer that is not really silent and waits on a dialog nobody can see.
.DESCRIPTION
    As SYSTEM there is no desktop, so a real installer in this state sits there until Intune's
    time limit. This script just waits for two hours to reproduce that.
    Set PackPilot's "Install time limit" to 5 minutes before testing this one.
    Expected Sandbox result: FAIL, "still running after 5 minutes".
#>
Write-Output 'Waiting for someone to click Next...'
Start-Sleep -Seconds 7200
exit 0
