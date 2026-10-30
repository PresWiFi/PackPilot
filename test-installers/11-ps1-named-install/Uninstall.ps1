<#
.SYNOPSIS
    Removes what Install.ps1 in this folder created.
#>
Remove-Item -LiteralPath (Join-Path $env:ProgramData 'ContosoNameClash') -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Removed.'
exit 0
