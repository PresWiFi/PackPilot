<#
    Test-SandboxHost.ps1 - checks why Windows Sandbox will not start on this PC.
    Run in an ADMIN PowerShell:
        powershell -ExecutionPolicy Bypass -File .\Test-SandboxHost.ps1
        powershell -ExecutionPolicy Bypass -File .\Test-SandboxHost.ps1 -LaunchBlank
    -LaunchBlank also starts the simplest possible Sandbox (no folders, no network, no vGPU).
    If even that crashes, the cause is the PC (Hyper-V, drivers, security software), not PackPilot.
    It saves a copy of everything it prints to SandboxHost-<date>.txt next to this script.
#>
param([switch]$LaunchBlank)
$ErrorActionPreference = 'Continue'
$report = Join-Path $PSScriptRoot ("SandboxHost-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Transcript -Path $report | Out-Null

function Show([string]$Name, $Value, [string]$Bad) {
    $text = "{0,-38} {1}" -f $Name, $Value
    if ($Bad) { Write-Host $text -ForegroundColor Yellow; Write-Host "    -> $Bad" -ForegroundColor Yellow } else { Write-Host $text }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host 'Not running as admin: some checks will be skipped. Re-run from an admin PowerShell for the full picture.' -ForegroundColor Yellow }

Write-Host "`n== Windows" -ForegroundColor Cyan
$os = Get-CimInstance Win32_OperatingSystem
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Show 'Edition' $os.Caption $(if ($os.Caption -match 'Home') { 'Windows Sandbox needs Pro, Enterprise or Education.' })
Show 'Version' ("{0} (build {1}.{2})" -f $cv.DisplayVersion, $cv.CurrentBuild, $cv.UBR)
$freeGb = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
Show 'Free memory' "$freeGb GB" $(if ($freeGb -lt 4) { 'Under 4 GB free: close some apps (or VMs/WSL) and try again.' })
$sysFree = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
Show 'Free space on C:' "$sysFree GB" $(if ($sysFree -lt 10) { 'Low disk space can stop the Sandbox starting.' })

Write-Host "`n== Virtualization" -ForegroundColor Cyan
$cs = Get-CimInstance Win32_ComputerSystem
Show 'Hypervisor running' $cs.HypervisorPresent $(if (-not $cs.HypervisorPresent) { "The Hyper-V hypervisor is not running. Run: bcdedit /set hypervisorlaunchtype auto  then restart. If it still is not, enable virtualization (VT-x/AMD-V, SVM) in the BIOS/UEFI." })
if ($admin) {
    $bcd = (bcdedit /enum '{current}' | Select-String 'hypervisorlaunchtype') -replace '\s+', ' '
    Show 'hypervisorlaunchtype' $(if ($bcd) { "$bcd".Trim() } else { 'not set (= Auto)' }) $(if ("$bcd" -match 'Off') { 'Turned off. Run: bcdedit /set hypervisorlaunchtype auto  then restart.' })
    foreach ($f in 'Containers-DisposableClientVM', 'Microsoft-Hyper-V-Hypervisor', 'VirtualMachinePlatform', 'HypervisorPlatform') {
        $st = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue).State
        Show "Feature $f" $(if ($st) { $st } else { 'not present' }) $(if ($f -eq 'Containers-DisposableClientVM' -and $st -ne 'Enabled') { 'Windows Sandbox is not enabled.' })
    }
}
$sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
Show 'Smart App Control' $(switch ($sac) { 1 { 'On' } 2 { 'Evaluation' } 0 { 'Off' } default { 'unknown' } }) $(if ($sac -eq 1) { 'Unsigned installers are blocked, in the Sandbox too ("An Application Control policy has blocked this file"). That matches devices that enforce App Control.' })
$dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue
if ($dg) { Show 'Memory integrity (HVCI)' $(if ($dg.SecurityServicesRunning -contains 2) { 'on' } else { 'off' }) }

Write-Host "`n== Services" -ForegroundColor Cyan
foreach ($s in 'vmcompute', 'hvhost', 'CmService', 'vmms') {
    $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
    if ($svc) { Show "Service $s" "$($svc.Status) ($($svc.StartType))" $(if ($s -in 'vmcompute', 'CmService' -and $svc.StartType -eq 'Disabled') { "Disabled. Windows Sandbox needs it: Set-Service $s -StartupType Manual" }) }
}

Write-Host "`n== Software that often conflicts" -ForegroundColor Cyan
$suspects = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(VBox|vmx86|VMAuthd|VMnet|hcmon)' -or $_.DisplayName -match 'VirtualBox|VMware' }
if ($suspects) { $suspects | ForEach-Object { Show "  $($_.DisplayName)" $_.Status 'Older VirtualBox/VMware versions break Hyper-V VMs. Update them, or stop them while testing.' } } else { Write-Host 'No VirtualBox/VMware services found.' }
$av = Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue
if ($av) { Show 'Antivirus' (($av.displayName | Sort-Object -Unique) -join ', ') $(if ($av.displayName -notmatch 'Defender') { 'Third-party security software can block the Sandbox VM. Check its logs, or test with it paused.' }) }
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 2
foreach ($g in $gpu) { Show 'GPU / driver' "$($g.Name) / $($g.DriverVersion) ($([datetime]$g.DriverDate | Get-Date -Format yyyy-MM-dd))" }

Write-Host "`n== Recent Hyper-V errors (last 2 days) - the real reason is usually here" -ForegroundColor Cyan
$logs = 'Microsoft-Windows-Hyper-V-Compute-Admin', 'Microsoft-Windows-Hyper-V-Worker-Admin', 'Microsoft-Windows-Hyper-V-Compute-Operational'
$events = foreach ($l in $logs) { Get-WinEvent -FilterHashtable @{ LogName = $l; Level = 1, 2, 3; StartTime = (Get-Date).AddDays(-2) } -MaxEvents 15 -ErrorAction SilentlyContinue }
if ($events) {
    $events | Sort-Object TimeCreated -Descending | Select-Object -First 15 | ForEach-Object {
        Write-Host ("{0:yyyy-MM-dd HH:mm:ss}  [{1}] {2}" -f $_.TimeCreated, $_.Id, (($_.Message -split "`n")[0..2] -join ' ').Trim())
    }
} else { Write-Host 'No Hyper-V errors logged (or not admin).' }

Write-Host "`n== Things to try for 0x80370106 (VM exited unexpectedly)" -ForegroundColor Cyan
@(
    '1. Restart the PC (pending updates often leave Hyper-V half-configured).',
    '2. Update the GPU driver. PackPilot already starts the Sandbox with vGPU off.',
    '3. Re-enable the feature: Disable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM, restart,',
    '   then Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All, restart.',
    '4. bcdedit /set hypervisorlaunchtype auto, then restart.',
    '5. Pause third-party antivirus/EDR or VirtualBox/VMware and try again.',
    '6. Run: sfc /scannow  and  DISM /Online /Cleanup-Image /RestoreHealth'
) | ForEach-Object { Write-Host $_ }

if ($LaunchBlank) {
    Write-Host "`n== Starting a blank Sandbox (no folders, no network, no vGPU)" -ForegroundColor Cyan
    $wsb = Join-Path $env:TEMP 'PackPilot-blank.wsb'
    '<Configuration><vGPU>Disable</vGPU><Networking>Disable</Networking></Configuration>' | Set-Content -LiteralPath $wsb -Encoding UTF8
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe') -ArgumentList "`"$wsb`""
    Write-Host 'If this one also crashes, the cause is this PC. If it opens fine, tell PackPilot''s developer: the mapped folders or logon command are the trigger.'
}
Stop-Transcript | Out-Null
Write-Host "`nSaved to $report"
