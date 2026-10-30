using System.Text;

namespace PackPilot.Core;

/// <summary>
/// The PowerShell scripts PackPilot writes. Each one is a fixed body plus a block of settings
/// at the top, so an admin can read and edit the settings without touching the logic.
/// </summary>
public static class Scripts
{
    static string Header(params (string Name, string Value)[] vars)
    {
        var sb = new StringBuilder("# ---- Settings (written by PackPilot; safe to edit) ----\n");
        foreach (var (name, value) in vars) sb.Append('$').Append(name).Append(" = ").Append(Util.PsQuote(value)).Append('\n');
        sb.Append("# ---- End of settings ----\n");
        return sb.ToString();
    }

    public static string Install(AppDefinition a) =>
        InstallHelp + Header(
            ("AppName", a.Name), ("AppVersion", a.Version), ("AppId", a.AppId),
            ("Kind", a.Kind.ToString()), ("PayloadFolder", a.PayloadFolder), ("SetupFile", a.SetupFileName), ("InstallArgs", a.InstallArgs),
            ("MarkerPath", MarkerPath(a)), ("WriteMarker", a.Detection == DetectionMethod.Marker ? "1" : "0"))
        + Common + InstallBody;

    public static string Uninstall(AppDefinition a) =>
        UninstallHelp + Header(
            ("AppName", a.Name), ("AppVersion", a.Version), ("AppId", a.AppId), ("Kind", a.Kind.ToString()),
            ("PayloadFolder", a.PayloadFolder), ("UninstallFile", a.UninstallFile), ("UninstallArgs", a.UninstallArgs),
            ("ProductCode", a.ProductCode), ("DisplayNamePattern", a.DisplayNamePattern),
            ("SilentArgs", a.UninstallSilentArgs), ("MarkerPath", MarkerPath(a)))
        + Common + UninstallBody;

    public static string Detect(AppDefinition a) =>
        DetectHelp + Header(
            ("Method", a.Detection.ToString()), ("ProductCode", a.ProductCode),
            ("DisplayNamePattern", a.DisplayNamePattern), ("MinVersion", Util.CleanVersion(a.DetectMinVersion)),
            ("FilePath", a.DetectFilePath), ("MarkerPath", MarkerPath(a)))
        + DetectBody;

    static string MarkerPath(AppDefinition a) => $@"SOFTWARE\PackPilot\Apps\{a.AppId}";

    const string InstallHelp = """
        <#
            Install.ps1 - written by PackPilot. Intune runs it from inside the package with:
                powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\Install.ps1
            It runs the installer silently, logs to the Intune logs folder (so "Collect diagnostics" picks
            the log up) and hands the installer's exit code back to Intune.
            Editing this file outside the .intunewin changes nothing: rebuild the package in PackPilot.
        #>

        """;

    const string UninstallHelp = """
        <#
            Uninstall.ps1 - written by PackPilot. Intune runs it from inside the package with:
                powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\Uninstall.ps1
            It uses the first of these that is set: UninstallFile, ProductCode, DisplayNamePattern
            (looked up in Programs and Features at uninstall time, so it survives app self-updates).
        #>

        """;

    const string DetectHelp = """
        <#
            Detect.ps1 - written by PackPilot. Upload it in Intune as a custom detection script
            (Run script as 32-bit process: No, Enforce signature check: No).
            Intune treats the app as installed when this exits 0 AND writes something to STDOUT.
        #>

        """;

    // Shared start of Install.ps1 and Uninstall.ps1: 64-bit relaunch, logging, process runner.
    const string Common = """

        $ErrorActionPreference = 'Stop'
        $Action = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath)

        # The Intune agent is a 32-bit process, so on 64-bit Windows this starts in 32-bit PowerShell.
        # Re-run in 64-bit PowerShell so Program Files and HKLM\SOFTWARE are the real 64-bit ones.
        if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
            $ps64 = Join-Path $env:WINDIR 'sysnative\WindowsPowerShell\v1.0\powershell.exe'
            if (Test-Path -LiteralPath $ps64) {
                & $ps64 -ExecutionPolicy Bypass -NoProfile -NonInteractive -File $PSCommandPath
                exit $LASTEXITCODE
            }
        }

        $LogDir = Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension\Logs'
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
        $Log = Join-Path $LogDir "PackPilot-$AppId-$Action.log"
        $WorkDir = [IO.Path]::Combine($PSScriptRoot, $PayloadFolder)   # where the installer files are

        function Write-Log([string]$Text) {
            $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Text"
            Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
            Write-Host $line           # Write-Host, not Write-Output: functions below return exit codes
        }

        # Runs a program, waits for that process only (not for apps it leaves running), returns its exit code.
        function Invoke-Program([string]$File, [string]$Arguments) {
            Write-Log "Running: `"$File`" $Arguments"
            $sp = @{ FilePath = $File; PassThru = $true; WorkingDirectory = $WorkDir; WindowStyle = 'Hidden' }
            if ($Arguments -and $Arguments.Trim()) { $sp.ArgumentList = $Arguments }
            $p = Start-Process @sp
            $null = $p.Handle          # keeps the handle so ExitCode is available after exit
            $p.WaitForExit()
            $code = $p.ExitCode
            $meaning = switch ($code) {
                0 { 'success' } 1707 { 'success' } 3010 { 'success, restart required' } 1641 { 'success, the installer started a restart' }
                1618 { 'another installation is in progress (Intune retries)' } 1603 { 'fatal error during installation (see the MSI log)' }
                1605 { 'not installed' } 1638 { 'another version is already installed' } default { 'failure' }
            }
            Write-Log "Exit code $code ($meaning)"
            return [int]$code
        }

        Write-Log "=== $Action $AppName $AppVersion as $([Security.Principal.WindowsIdentity]::GetCurrent().Name) ==="
        $Success = 0, 1707, 3010, 1641

        """;

    const string InstallBody = """
        try {
            $setup = Join-Path $WorkDir $SetupFile
            switch ($Kind) {
                'Msi' {
                    $msiLog = Join-Path $LogDir "PackPilot-$AppId-MSI-Install.log"
                    $code = Invoke-Program (Join-Path $env:WINDIR 'System32\msiexec.exe') "/i `"$setup`" $InstallArgs /l*v `"$msiLog`""
                }
                'Exe' { $code = Invoke-Program $setup $InstallArgs }
                'PowerShell' {
                    $code = Invoke-Program (Join-Path $PSHOME 'powershell.exe') "-ExecutionPolicy Bypass -NoProfile -NonInteractive -File `"$setup`" $InstallArgs"
                }
            }
        } catch {
            Write-Log "Could not run the installer: $($_.Exception.Message)"
            exit 1
        }

        if ($code -in $Success -and $WriteMarker -eq '1') {
            $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64').CreateSubKey($MarkerPath)
            $key.SetValue('Version', $AppVersion)
            $key.SetValue('InstalledAt', (Get-Date).ToString('s'))
            $key.Close()
            Write-Log "Wrote detection marker HKLM\$MarkerPath (Version = $AppVersion)"
        }
        Write-Log "=== Done, returning $code to Intune ==="
        exit $code

        """;

    const string UninstallBody = """
        function Get-AppEntries([string]$Pattern) {
            foreach ($view in 'Registry64', 'Registry32') {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view).OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                if (-not $base) { continue }
                foreach ($name in $base.GetSubKeyNames()) {
                    $k = $base.OpenSubKey($name)
                    if (-not $k) { continue }
                    $dn = $k.GetValue('DisplayName')
                    if ($dn -and $dn -like $Pattern) {
                        [pscustomobject]@{
                            KeyName = $name; DisplayName = $dn; DisplayVersion = $k.GetValue('DisplayVersion')
                            QuietUninstallString = $k.GetValue('QuietUninstallString'); UninstallString = $k.GetValue('UninstallString')
                            WindowsInstaller = $k.GetValue('WindowsInstaller')
                        }
                    }
                }
            }
        }

        # Splits "C:\Path\unins000.exe" /SILENT or C:\Path\uninst.exe /S into file and arguments.
        function Split-CommandLine([string]$Cmd) {
            $Cmd = [Environment]::ExpandEnvironmentVariables($Cmd.Trim())
            if ($Cmd.StartsWith('"')) {
                $end = $Cmd.IndexOf('"', 1)
                return @($Cmd.Substring(1, $end - 1), $Cmd.Substring($end + 1).Trim())
            }
            $m = [regex]::Match($Cmd, '^(.+?\.(exe|cmd|bat))(\s+(.*))?$', 'IgnoreCase')
            if ($m.Success) { return @($m.Groups[1].Value, $m.Groups[4].Value) }
            return @($Cmd, '')
        }

        function Uninstall-Msi([string]$Code) {
            $msiLog = Join-Path $LogDir "PackPilot-$AppId-MSI-Uninstall.log"
            return Invoke-Program (Join-Path $env:WINDIR 'System32\msiexec.exe') "/x $Code /qn /norestart /l*v `"$msiLog`""
        }

        $codes = @()
        try {
            if ($UninstallFile) {
                $file = $UninstallFile
                $local = Join-Path $WorkDir $UninstallFile
                if (Test-Path -LiteralPath $local) { $file = $local }
                if ($file -like '*.ps1') {
                    $codes += Invoke-Program (Join-Path $PSHOME 'powershell.exe') "-ExecutionPolicy Bypass -NoProfile -NonInteractive -File `"$file`" $UninstallArgs"
                } else {
                    $codes += Invoke-Program $file $UninstallArgs
                }
            } elseif ($ProductCode) {
                $codes += Uninstall-Msi $ProductCode
                if ($codes[-1] -eq 1605) { Write-Log 'The product was not installed: nothing to do.'; $codes[-1] = 0 }
            } elseif ($DisplayNamePattern) {
                $entries = @(Get-AppEntries $DisplayNamePattern)
                if (-not $entries.Count) { Write-Log "Nothing in Programs and Features matches '$DisplayNamePattern': nothing to do." }
                foreach ($e in $entries) {
                    Write-Log "Found '$($e.DisplayName)' $($e.DisplayVersion) ($($e.KeyName))"
                    if ($e.KeyName -match '^\{[0-9A-Fa-f-]{36}\}$' -and ($e.WindowsInstaller -eq 1 -or "$($e.UninstallString)" -match '(?i)msiexec')) {
                        $codes += Uninstall-Msi $e.KeyName
                    } elseif ($e.QuietUninstallString) {
                        $f, $a = Split-CommandLine $e.QuietUninstallString
                        $codes += Invoke-Program $f $a
                    } elseif ($e.UninstallString) {
                        $f, $a = Split-CommandLine $e.UninstallString
                        $codes += Invoke-Program $f "$a $SilentArgs".Trim()
                    } else {
                        Write-Log 'This entry has no uninstall command.'
                        $codes += 1
                    }
                }
                # Some uninstallers (Inno Setup, NSIS) hand off to a copy of themselves and exit at once.
                # Wait up to two minutes for the entry to really go, so Intune's detection check sees the result.
                $deadline = (Get-Date).AddMinutes(2)
                while ($entries.Count -and @(Get-AppEntries $DisplayNamePattern).Count -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
            } else {
                Write-Log 'No uninstall method is set for this app.'
            }
        } catch {
            Write-Log "Could not run the uninstaller: $($_.Exception.Message)"
            $codes += 1
        }

        $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
        if ($hklm.OpenSubKey($MarkerPath)) { $hklm.DeleteSubKeyTree($MarkerPath); Write-Log "Removed detection marker HKLM\$MarkerPath" }

        $failed = @($codes | Where-Object { $_ -notin $Success })
        $code = if ($failed.Count) { $failed[0] } elseif ($codes.Count) { $codes[-1] } else { 0 }
        Write-Log "=== Done, returning $code to Intune ==="
        exit $code

        """;

    const string DetectBody = """

        function ConvertTo-Version([string]$Text) {
            $m = [regex]::Match("$Text", '\d+(\.\d+){0,3}')
            if (-not $m.Success) { return $null }
            $v = $m.Value
            if ($v -notmatch '\.') { $v += '.0' }
            return [version]$v
        }

        function Test-MinVersion([string]$Have) {
            if (-not $MinVersion) { return $true }
            $h = ConvertTo-Version $Have; $w = ConvertTo-Version $MinVersion
            if (-not $h -or -not $w) { return $Have -eq $MinVersion }
            return $h -ge $w
        }

        function Get-UninstallKeys {
            foreach ($view in 'Registry64', 'Registry32') {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view).OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                if (-not $base) { continue }
                foreach ($name in $base.GetSubKeyNames()) {
                    $k = $base.OpenSubKey($name)
                    if ($k) { [pscustomobject]@{ KeyName = $name; DisplayName = $k.GetValue('DisplayName'); DisplayVersion = $k.GetValue('DisplayVersion') } }
                }
            }
        }

        switch ($Method) {
            'MsiProductCode' {
                $hit = Get-UninstallKeys | Where-Object { $_.KeyName -eq $ProductCode -and (Test-MinVersion $_.DisplayVersion) } | Select-Object -First 1
                if ($hit) { Write-Output "Detected: $($hit.DisplayName) $($hit.DisplayVersion) ($ProductCode)"; exit 0 }
            }
            'Registry' {
                $hit = Get-UninstallKeys | Where-Object { $_.DisplayName -and $_.DisplayName -like $DisplayNamePattern -and (Test-MinVersion $_.DisplayVersion) } | Select-Object -First 1
                if ($hit) { Write-Output "Detected: $($hit.DisplayName) $($hit.DisplayVersion)"; exit 0 }
            }
            'File' {
                $path = [Environment]::ExpandEnvironmentVariables($FilePath)
                if (Test-Path -LiteralPath $path) {
                    $item = Get-Item -LiteralPath $path
                    $ver = if ($item.VersionInfo) { "$($item.VersionInfo.FileVersionRaw)" } else { '' }
                    if (Test-MinVersion $ver) { Write-Output "Detected: $path $ver"; exit 0 }
                }
            }
            'Marker' {
                $k = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64').OpenSubKey($MarkerPath)
                if ($k -and (Test-MinVersion $k.GetValue('Version'))) { Write-Output "Detected: HKLM\$MarkerPath Version $($k.GetValue('Version'))"; exit 0 }
            }
        }
        exit 1

        """;

    /// <summary>
    /// Runs inside a reused ("session") Windows Sandbox. Waits for PackPilot to drop test jobs into
    /// C:\PackPilot\Session\Jobs\<run id>\ and runs each one with that job's Test-InSandbox.ps1.
    /// Writes a heartbeat to agent.json so PackPilot can tell the Sandbox is still there and ready.
    /// </summary>
    public const string SandboxAgent = """
        <#
            PackPilot-Agent.ps1 - written by PackPilot. Keeps one Windows Sandbox open for several tests.
            Leave this window open. Close the Sandbox (or use "Close test Sandbox" in PackPilot) to end the session.
        #>
        $ErrorActionPreference = 'Continue'
        $Root = 'C:\PackPilot\Session'
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
            exit 0
        }
        $Host.UI.RawUI.WindowTitle = 'PackPilot test Sandbox'
        $session = Get-Content -LiteralPath (Join-Path $Root 'session.json') -Raw | ConvertFrom-Json
        $seq = 0
        $runs = 0
        $done = @{}

        function Send-Beat([string]$State) {
            $script:seq++
            $beat = [ordered]@{ sessionId = $session.sessionId; seq = $script:seq; runs = $script:runs; state = $State }
            try { $beat | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'agent.json') -Encoding UTF8 } catch { }
        }

        Write-Host 'PackPilot test Sandbox is ready. PackPilot sends each test here; leave this window open.' -ForegroundColor Green
        while ($true) {
            Send-Beat 'ready'
            if (Test-Path -LiteralPath (Join-Path $Root 'shutdown.request')) {
                Write-Host 'PackPilot asked to close this Sandbox.'
                Remove-Item -LiteralPath (Join-Path $Root 'shutdown.request') -Force -ErrorAction SilentlyContinue
                Send-Beat 'closing'
                shutdown.exe /s /t 0
                break
            }
            $jobs = Get-ChildItem -LiteralPath (Join-Path $Root 'Jobs') -Directory -ErrorAction SilentlyContinue |
                Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'job.ready')) -and -not $done.ContainsKey($_.Name) } |
                Sort-Object Name
            foreach ($j in $jobs) {
                $done[$j.Name] = $true
                $runs++
                Write-Host ''
                Write-Host "=== Test $runs in this Sandbox: $($j.Name) ===" -ForegroundColor Cyan
                $script = Join-Path $j.FullName 'Test\Test-InSandbox.ps1'
                $p = Start-Process powershell.exe -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$script`" -NoPause -SessionRun $runs" -NoNewWindow -PassThru
                while (-not $p.HasExited) { Send-Beat 'busy'; Start-Sleep -Seconds 2 }
                Write-Host 'Waiting for the next test from PackPilot...' -ForegroundColor Green
            }
            Start-Sleep -Seconds 2
        }

        """;

    /// <summary>Runs inside Windows Sandbox. Settings come from test-config.json next to it.</summary>
    public const string SandboxTest = """
        <#
            Test-InSandbox.ps1 - written by PackPilot. Windows Sandbox runs it at logon.
            It does what Intune does, in the same order, as SYSTEM:
              1. Unpacks the .intunewin with the keys in its Detection.xml (HMAC checked, AES decrypted).
              2. Runs Detect.ps1           - expected: not installed (the Sandbox is a clean Windows).
              3. Runs Install.ps1          - as SYSTEM, from 32-bit PowerShell, like the Intune agent.
              4. Runs Detect.ps1           - expected: installed.
              5. Records what the install added to Programs and Features.
              6. Runs Uninstall.ps1, then Detect.ps1 again - expected: not installed.
            Results go to Results\<run id>\ next to this script: result.json, progress.log and every log.
            The package is in the Package folder next to this script's folder.
            -NoPause and -SessionRun are used by PackPilot-Agent.ps1 when one Sandbox runs several tests.
        #>
        param([switch]$NoPause, [int]$SessionRun = 0)
        $ErrorActionPreference = 'Stop'
        $Here = $PSScriptRoot
        $cfg = Get-Content -LiteralPath (Join-Path $Here 'test-config.json') -Raw | ConvertFrom-Json
        $RunId = $cfg.runId

        # Registering a task that runs as SYSTEM needs an elevated shell.
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`" -SessionRun $SessionRun$(if ($NoPause) { ' -NoPause' })"
            exit 0
        }

        $Host.UI.RawUI.WindowTitle = "PackPilot test - $($cfg.appName)"
        $results = Join-Path $Here "Results\$RunId"
        New-Item -ItemType Directory -Path $results -Force | Out-Null
        $work = "C:\PackPilotWork\$RunId"     # per run, so a reused Sandbox never mixes runs
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        $steps = New-Object System.Collections.ArrayList
        $installTimeout = [int]$cfg.timeoutMinutes
        $detectTimeout = 5

        function Write-Step([string]$Text) {
            $line = "$(Get-Date -Format 'HH:mm:ss')  $Text"
            Write-Host $line
            Add-Content -LiteralPath (Join-Path $results 'progress.log') -Value $line -Encoding UTF8
        }
        function Add-Step([string]$Name, [bool]$Ok, [string]$Detail) {
            [void]$steps.Add([ordered]@{ step = $Name; ok = $Ok; detail = $Detail })
            Write-Step ("{0}  {1} - {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
        }

        # Runs a command as SYSTEM through a scheduled task, the same account Intune installs with.
        function Invoke-AsSystem([string]$Exe, [string]$Arguments, [string]$WorkDir, [int]$TimeoutMinutes) {
            $name = 'PackPilot-' + [guid]::NewGuid().ToString('N')
            $out = Join-Path $work "$name.out"
            $cmdLine = "/c `"`"$Exe`" $Arguments > `"$out`" 2>&1`""
            $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $cmdLine -WorkingDirectory $WorkDir
            $taskUser = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes ($TimeoutMinutes + 5)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            Register-ScheduledTask -TaskName $name -Action $action -Principal $taskUser -Settings $settings | Out-Null
            Start-ScheduledTask -TaskName $name
            $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
            $timedOut = $false
            # 0x41301 running, 0x41303 not run yet, 0x41325 queued; anything else is the exit code.
            do {
                Start-Sleep -Seconds 2
                $code = (Get-ScheduledTaskInfo -TaskName $name).LastTaskResult
                $busy = (Get-ScheduledTask -TaskName $name).State -eq 'Running' -or $code -in 0x41301, 0x41303, 0x41325
                if ($busy -and (Get-Date) -gt $deadline) { Stop-ScheduledTask -TaskName $name; $timedOut = $true; break }
            } while ($busy)
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            $text = if (Test-Path $out) { Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue } else { '' }
            return @{ ExitCode = [int]$code; Output = "$text".Trim(); TimedOut = $timedOut }
        }

        function Get-UninstallEntries {
            $list = @{}
            $roots = @(
                @{ Hive = 'LocalMachine'; View = 'Registry64'; Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Label = 'HKLM' },
                @{ Hive = 'LocalMachine'; View = 'Registry32'; Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Label = 'HKLM (32-bit)' },
                @{ Hive = 'Users'; View = 'Registry64'; Path = 'S-1-5-18\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Label = 'SYSTEM user' }
            )
            foreach ($r in $roots) {
                try { $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($r.Hive, $r.View).OpenSubKey($r.Path) } catch { continue }
                if (-not $base) { continue }
                foreach ($k in $base.GetSubKeyNames()) {
                    $key = $base.OpenSubKey($k)
                    if (-not $key) { continue }
                    $e = [ordered]@{ Location = $r.Label; KeyName = $k }
                    foreach ($v in 'DisplayName', 'DisplayVersion', 'Publisher', 'UninstallString', 'QuietUninstallString', 'InstallLocation', 'DisplayIcon', 'SystemComponent', 'WindowsInstaller') {
                        $e[$v] = $key.GetValue($v)
                    }
                    if ($e.DisplayName) { $list["$($r.Label)\$k"] = $e }
                }
            }
            return $list
        }

        function Test-Detected($r) { return ($r.ExitCode -eq 0 -and $r.Output) }

        $ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path $ps32)) { $ps32 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
        $ps64 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $verdict = 'PASS'
        $learned = $null
        $installCode = -1
        $installSecs = 0
        $installOutput = ''
        $uninstallOutput = ''

        # The last few meaningful lines of a step's output, for the step detail.
        function Get-Tail([string]$Text, [int]$Lines = 3) {
            $l = @("$Text" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^=== ' })
            if (-not $l.Count) { return '' }
            return ($l | Select-Object -Last $Lines) -join ' | '
        }
        $appName = "$($cfg.appName)"

        try {
            Write-Step "Sandbox test started for $appName."

            # 1. Unpack the .intunewin the way the Intune agent does.
            $pkgDir = Join-Path (Split-Path $Here -Parent) 'Package'
            $pkg = Get-ChildItem -LiteralPath $pkgDir -Filter '*.intunewin' -File | Select-Object -First 1
            if (-not $pkg) { throw "No .intunewin found in $pkgDir." }
            Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($pkg.FullName)
            try {
                $xmlEntry = $zip.Entries | Where-Object { $_.FullName -like '*Metadata/Detection.xml' } | Select-Object -First 1
                $encEntry = $zip.Entries | Where-Object { $_.FullName -like '*Contents/*.intunewin' } | Select-Object -First 1
                if (-not $xmlEntry -or -not $encEntry) { throw "$($pkg.Name) is not a .intunewin (no Detection.xml or Contents)." }
                $reader = New-Object IO.StreamReader($xmlEntry.Open())
                [xml]$detection = $reader.ReadToEnd(); $reader.Close()
                $encPath = Join-Path $work 'content.bin'
                [IO.Compression.ZipFileExtensions]::ExtractToFile($encEntry, $encPath, $true)
            } finally { $zip.Dispose() }
            $info = $detection.ApplicationInfo.EncryptionInfo
            $fs = [IO.File]::OpenRead($encPath)
            try {
                $storedMac = New-Object byte[] 32
                [void]$fs.Read($storedMac, 0, 32)
                $hmac = New-Object Security.Cryptography.HMACSHA256 (, [Convert]::FromBase64String($info.MacKey))
                $mac = $hmac.ComputeHash($fs)
                if ([Convert]::ToBase64String($mac) -ne [Convert]::ToBase64String($storedMac) -or [Convert]::ToBase64String($mac) -ne $info.Mac) {
                    throw 'the HMAC does not match: the package is damaged'
                }
                $aes = [Security.Cryptography.Aes]::Create()
                $aes.Key = [Convert]::FromBase64String($info.EncryptionKey)
                $aes.IV = [Convert]::FromBase64String($info.InitializationVector)
                $fs.Position = 48
                $payload = Join-Path $work 'payload.zip'
                $crypto = New-Object Security.Cryptography.CryptoStream($fs, $aes.CreateDecryptor(), 'Read')
                $outFs = [IO.File]::Create($payload)
                $crypto.CopyTo($outFs); $outFs.Close(); $crypto.Close()
            } finally { $fs.Dispose() }
            $content = Join-Path $work 'Content'
            [IO.Compression.ZipFile]::ExtractToDirectory($payload, $content)
            Add-Step 'Unpack package' $true ("{0}: HMAC valid, decrypted, {1} file(s)" -f $pkg.Name, (Get-ChildItem $content -Recurse -File).Count)

            Copy-Item -LiteralPath (Join-Path $Here 'Detect.ps1') -Destination (Join-Path $work 'Detect.ps1') -Force
            $detectArgs = "-ExecutionPolicy Bypass -NoProfile -NonInteractive -File `"$(Join-Path $work 'Detect.ps1')`""

            # 2. Detection on a clean machine.
            $d = Invoke-AsSystem $ps64 $detectArgs $work $detectTimeout
            if (Test-Detected $d) { Add-Step 'Detect before install' $false "the app is reported as ALREADY installed on a clean Windows: the rule is too broad ($($d.Output))"; $verdict = 'FAIL' }
            else { Add-Step 'Detect before install' $true 'not installed, as expected' }

            # 3. Install as SYSTEM from 32-bit PowerShell, the way the Intune agent starts it.
            $before = Get-UninstallEntries
            Write-Step "Installing (time limit $installTimeout minutes)..."
            $started = Get-Date
            $i = Invoke-AsSystem $ps32 '-ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\Install.ps1' $content $installTimeout
            $installSecs = [int]((Get-Date) - $started).TotalSeconds
            $installCode = $i.ExitCode
            $installOutput = $i.Output
            if ($i.TimedOut) {
                Add-Step 'Install' $false "still running after $installTimeout minutes: it is probably waiting on a dialog nobody can click as SYSTEM. Check the install arguments."
                $verdict = 'FAIL'
            } else {
                $meaning = switch ($i.ExitCode) {
                    0 { 'success' } 1707 { 'success' } 3010 { 'success, restart required' } 1641 { 'success, the installer started a restart' }
                    1618 { 'another installation was in progress' } 1603 { 'fatal error (see the MSI log)' } default { 'failure' }
                }
                $ok = $i.ExitCode -in 0, 1707, 3010, 1641
                $tail = if ($ok) { '' } else { Get-Tail $i.Output }
                Add-Step 'Install' $ok ("exit code $($i.ExitCode) ($meaning) after $installSecs s" + $(if ($tail) { ". Output: $tail" }))
                if (-not $ok) { $verdict = 'FAIL' }
            }

            # Blocked by App Control (Smart App Control / App Control for Business)? The Code Integrity log names
            # the file and the policy. Checked on any failure, because a blocked child process can surface as a plain exit code.
            if ($installCode -notin 0, 1707, 3010, 1641) {
                $ci = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; Id = 3077, 3033, 3034, 3089, 8028, 8029; StartTime = $started.AddSeconds(-5) } -MaxEvents 20 -ErrorAction SilentlyContinue)
                $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
                $sacText = switch ($sac) { 1 { 'Smart App Control is ON in this Sandbox' } 2 { 'Smart App Control is in evaluation mode' } 0 { 'Smart App Control is off' } default { 'Smart App Control state unknown' } }
                if ($ci.Count -or "$installOutput" -match 'Application Control policy has blocked') {
                    $files = @($ci | Where-Object { $_.Id -in 3077, 3033, 8029 } | ForEach-Object {
                        if ($_.Message -match '(?:attempted to load|process) ([^\s]+\.(?:exe|dll|msi|sys|ps1))') { Split-Path $Matches[1] -Leaf } } | Select-Object -Unique)
                    $pol = @($ci | ForEach-Object { if ($_.Message -match 'Policy (?:ID|Name):\s*\{?([^}\s,)]+)') { $Matches[1] } } | Select-Object -Unique)
                    $detail = "Windows App Control blocked the installer ($sacText)"
                    if ($files.Count) { $detail += ". Blocked: $($files -join ', ')" }
                    if ($pol.Count) { $detail += ". Policy: $($pol -join ', ')" }
                    Add-Step 'App Control' $false $detail
                    $ci | Select-Object TimeCreated, Id, Message | Format-List | Out-String -Width 300 |
                        Set-Content -LiteralPath (Join-Path $results 'CodeIntegrity-events.txt') -Encoding UTF8
                }
            }

            # 4. Detection after install.
            $d = Invoke-AsSystem $ps64 $detectArgs $work $detectTimeout
            if (Test-Detected $d) { Add-Step 'Detect after install' $true "detected ($($d.Output))" }
            elseif ($installCode -notin 0, 1707, 3010, 1641) { Add-Step 'Detect after install' $false 'not detected, because the install failed'; $verdict = 'FAIL' }
            else { Add-Step 'Detect after install' $false 'Detect.ps1 does not see the app after the install. Use what the test learned.'; $verdict = 'FAIL' }

            # 5. What the install added to Programs and Features.
            $after = Get-UninstallEntries
            $newEntries = @($after.Keys | Where-Object { -not $before.ContainsKey($_) } | ForEach-Object { $after[$_] })
            if ($newEntries.Count) {
                $visible = @($newEntries | Where-Object { $_.SystemComponent -ne 1 })
                if (-not $visible.Count) { $visible = $newEntries }
                $pattern = "$($cfg.displayNamePattern)"
                $main = $visible | Where-Object { $pattern -and $_.DisplayName -like $pattern } | Select-Object -First 1
                if (-not $main) { $main = $visible | Select-Object -First 1 }
                $isGuid = $main.KeyName -match '^\{[0-9A-Fa-f-]{36}\}$'
                $isMsi = $isGuid -and ($main.WindowsInstaller -eq 1 -or "$($main.UninstallString)" -match '(?i)msiexec')
                $icon = "$($main.DisplayIcon)" -replace ',\s*-?\d+$', '' -replace '^"|"$', ''
                $file = $null
                if ($icon -match '\.exe$' -and (Test-Path -LiteralPath $icon)) { $file = $icon }
                elseif ($main.InstallLocation -and (Test-Path -LiteralPath $main.InstallLocation)) {
                    $file = Get-ChildItem -LiteralPath $main.InstallLocation -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -notmatch '^(unins|uninst|setup|update)' } | Sort-Object Length -Descending | Select-Object -First 1 -ExpandProperty FullName
                }
                $learned = [ordered]@{
                    displayName = "$($main.DisplayName)"
                    displayVersion = "$($main.DisplayVersion)"
                    publisher = "$($main.Publisher)"
                    productCode = $(if ($isMsi) { $main.KeyName } else { '' })
                    registryKey = "$($main.Location)\...\Uninstall\$($main.KeyName)"
                    installLocation = "$($main.InstallLocation)"
                    detectFile = "$file"
                    quietUninstallString = "$($main.QuietUninstallString)"
                    uninstallString = "$($main.UninstallString)"
                }
                Add-Step 'Learn' $true ("Programs and Features gained {0} entr{1}; the app's is '{2}' {3}" -f $newEntries.Count, $(if ($newEntries.Count -eq 1) { 'y' } else { 'ies' }), $main.DisplayName, $main.DisplayVersion)
                if ($main.Location -eq 'SYSTEM user') { Add-Step 'Install scope' $false 'the app installed per-user into the SYSTEM profile, so users will not see it. Use a machine-wide installer or switch, or deploy in the user context.'; if ($verdict -eq 'PASS') { $verdict = 'PASS WITH WARNINGS' } }
            } elseif ($installCode -in 0, 1707, 3010, 1641) {
                Add-Step 'Learn' $true 'the install added nothing to Programs and Features (a script, a portable app, or a per-user install)'
            }

            # 6. Uninstall, then detection again.
            if ($cfg.skipUninstall) {
                Add-Step 'Uninstall' $true 'skipped (turned off in PackPilot)'
            } else {
                Write-Step "Uninstalling (time limit $installTimeout minutes)..."
                $u = Invoke-AsSystem $ps32 '-ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\Uninstall.ps1' $content $installTimeout
                if ($u.TimedOut) { Add-Step 'Uninstall' $false "still running after $installTimeout minutes: probably waiting on a dialog"; $verdict = 'FAIL' }
                else {
                    $ok = $u.ExitCode -in 0, 1707, 3010, 1641
                    $uninstallOutput = $u.Output
                    $tail = if ($ok) { '' } else { Get-Tail $u.Output }
                    Add-Step 'Uninstall' $ok ("exit code $($u.ExitCode)" + $(if ($tail) { ". Output: $tail" }))
                    if (-not $ok) { $verdict = 'FAIL' }
                }
                $d = Invoke-AsSystem $ps64 $detectArgs $work $detectTimeout
                if (Test-Detected $d) { Add-Step 'Detect after uninstall' $false "the app is still detected after the uninstall ($($d.Output))"; $verdict = 'FAIL' }
                else { Add-Step 'Detect after uninstall' $true 'not installed, as expected' }
            }
        } catch {
            Add-Step 'Error' $false $_.Exception.Message
            $verdict = 'FAIL'
        }

        $logDir = Join-Path $results 'Logs'
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        Copy-Item -Path (Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension\Logs\PackPilot-*') -Destination $logDir -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $work -Filter '*.out' -ErrorAction SilentlyContinue | Copy-Item -Destination $logDir -ErrorAction SilentlyContinue

        $json = [ordered]@{
            runId = $RunId; app = $appName; runAt = (Get-Date).ToString('s'); verdict = $verdict; sessionRun = $SessionRun
            installExitCode = $installCode; installSeconds = $installSecs; steps = $steps; learned = $learned
            installOutput = "$installOutput".Substring([Math]::Max(0, "$installOutput".Length - 6000))
            uninstallOutput = "$uninstallOutput".Substring([Math]::Max(0, "$uninstallOutput".Length - 6000))
        }
        $tmp = Join-Path $results 'result.json.tmp'
        $json | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp -Encoding UTF8
        Move-Item -LiteralPath $tmp -Destination (Join-Path $results 'result.json') -Force
        Write-Step "Done: $verdict"
        Write-Host ''
        if ($NoPause) { return }
        Write-Host 'PackPilot has the results. You can close Windows Sandbox (everything in it is thrown away).' -ForegroundColor Green
        Read-Host 'Press Enter to close this window'

        """;
}
