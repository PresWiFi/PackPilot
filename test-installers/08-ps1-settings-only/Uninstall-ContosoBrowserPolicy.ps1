<#
.SYNOPSIS
    Removes the example Edge policies set by Set-ContosoBrowserPolicy.ps1.
#>
$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
Remove-ItemProperty -Path $key -Name HomepageLocation, HideFirstRunExperience -ErrorAction SilentlyContinue
Write-Output 'Edge policies removed.'
exit 0
