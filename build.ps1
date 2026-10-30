<#
    Builds PackPilot.exe into .\dist.
      .\build.ps1                     self-contained single exe (~70 MB, runs on any Windows 10/11 x64)
      .\build.ps1 -FrameworkDependent small exe that needs the .NET 8 Desktop Runtime installed
      .\build.ps1 -Test               run the self-checks first
    Needs the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8
#>
param([switch]$FrameworkDependent, [switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Test) {
    dotnet run --project tests\PackPilot.Tests
    if ($LASTEXITCODE) { throw 'Self-checks failed.' }
}
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish src\PackPilot -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$selfContained `
    -p:DebugType=none -o dist
if ($LASTEXITCODE) { throw 'Build failed.' }
Write-Host "Built $(Join-Path $PSScriptRoot 'dist\PackPilot.exe')" -ForegroundColor Green
