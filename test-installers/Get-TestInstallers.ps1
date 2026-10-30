<#
    Downloads the test installers again from the vendors' own sites (they are not kept in git).
    Run from this folder:  .\Get-TestInstallers.ps1
    Versions are pinned so the expected results in README.md stay true. Bump them when you like.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster
$files = @(
    @{ Folder = '01-msi-7zip';                 File = '7z2605-x64.msi';                    Url = 'https://www.7-zip.org/a/7z2605-x64.msi' }
    @{ Folder = '02-msi-putty';                File = 'putty-64bit-0.85-installer.msi';    Url = 'https://the.earth.li/~sgtatham/putty/0.85/w64/putty-64bit-0.85-installer.msi' }
    @{ Folder = '03-exe-custom-7zip';          File = '7z2605-x64.exe';                    Url = 'https://www.7-zip.org/a/7z2605-x64.exe' }
    @{ Folder = '04-exe-nsis-notepadplusplus'; File = 'npp.8.9.8.1.Installer.x64.exe';     Url = 'https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.9.8.1/npp.8.9.8.1.Installer.x64.exe' }
    @{ Folder = '05-exe-inno-innosetup';       File = 'innosetup-7.1.1-x64.exe';           Url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' }
    @{ Folder = '06-exe-wixburn-vcredist';     File = 'vc_redist.x64.exe';                 Url = 'https://aka.ms/vs/17/release/vc_redist.x64.exe' }
)
foreach ($f in $files) {
    $dir = Join-Path $PSScriptRoot $f.Folder
    $out = Join-Path $dir $f.File
    if (Test-Path -LiteralPath $out) { Write-Host "have  $($f.File)"; continue }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Write-Host "get   $($f.File)"
    Invoke-WebRequest -Uri $f.Url -OutFile $out -UseBasicParsing
    $sig = Get-AuthenticodeSignature -LiteralPath $out
    Write-Host "      signature: $($sig.Status) $($sig.SignerCertificate.Subject)"
}
