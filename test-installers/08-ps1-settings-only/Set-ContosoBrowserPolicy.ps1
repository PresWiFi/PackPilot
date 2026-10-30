<#
.SYNOPSIS
    Sets example Microsoft Edge policies (a settings-only script for testing PackPilot).
.DESCRIPTION
    Leaves nothing in Programs and Features, so PackPilot should use its registry marker
    for detection. The policies are harmless: they set the Edge home page and turn off
    the first-run experience. Run it in Windows Sandbox, not on your own PC, unless you want them.
#>
$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name HomepageLocation -Value 'https://www.contoso.com'
Set-ItemProperty -Path $key -Name HideFirstRunExperience -Value 1 -Type DWord
Write-Output 'Edge policies set.'
exit 0
