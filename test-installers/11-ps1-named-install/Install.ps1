<#
.SYNOPSIS
    A script that happens to be called Install.ps1, the same name as PackPilot's wrapper.
.DESCRIPTION
    PackPilot should notice the clash and put this file in a Files\ subfolder of the package.
    Its Uninstall.ps1 sibling clashes too. Expected Sandbox result: PASS.
#>
$dir = Join-Path $env:ProgramData 'ContosoNameClash'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'ran-from.txt') -Value $PSScriptRoot
Write-Output "Ran from $PSScriptRoot"
exit 0
