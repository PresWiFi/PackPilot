# PackPilot test installers

Real installers from the vendors' own sites, plus small PowerShell scripts that each exercise one path through PackPilot. Drop any file from a folder onto PackPilot, build, then test in Sandbox.

The `.exe`/`.msi` files are not in git. If they're missing, run `.\Get-TestInstallers.ps1` here.

## Installers

| Folder | File | PackPilot should detect | Default switches | What it tests |
|---|---|---|---|---|
| `01-msi-7zip` | 7-Zip 26.05 MSI | Windows Installer (MSI), x64 | `/qn /norestart` | MSI product code read, built-in MSI detection rule |
| `02-msi-putty` | PuTTY 0.85 MSI | Windows Installer (MSI), x64 | `/qn /norestart` | A second MSI; per-machine install |
| `03-exe-custom-7zip` | 7-Zip 26.05 EXE | Custom setup (7-Zip payload), low confidence | `/S` | An EXE with no well-known engine; "Use what the test learned" fixes detection |
| `04-exe-nsis-notepadplusplus` | Notepad++ 8.9.8.1 | NSIS | `/S` | NSIS; uninstaller hands off to a copy of itself (Uninstall.ps1 waits for it) |
| `05-exe-inno-innosetup` | Inno Setup 7.1.1 | Inno Setup | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` | Inno; uninstall via UninstallString + `/VERYSILENT` |
| `06-exe-wixburn-vcredist` | VC++ 2015-2022 Redistributable x64 | WiX Burn bundle | `/install /quiet /norestart` | Burn bundle; QuietUninstallString. Note: Windows Sandbox may already have a VC++ runtime, so "Detect before install" can fail. That's a correct finding, and the reason to use a version check. |

**Signing matters.** The 7-Zip files (01, 03) are not code-signed. If Smart App Control is on for this PC (check: `(Get-MpComputerStatus).SmartAppControlState`), the Sandbox blocks them with "An Application Control policy has blocked this file", and PackPilot reports an **App Control** failure. That is the correct result: devices that enforce App Control would block them too. Notepad++, Inno Setup, VC++ and PuTTY are signed.

Expected for 01-05 with Smart App Control off (02, 04, 05 either way): Sandbox test **PASS** (for 03-05, after "Use what the test learned" and a second test run).

## Scripts

| Folder | Script | Expected result | What it tests |
|---|---|---|---|
| `07-ps1-app-with-uninstall` | `Install-ContosoNotes.ps1` | PASS | Fake app with a Programs and Features entry. `Uninstall-ContosoNotes.ps1` is picked up automatically. Fails on purpose if not run as 64-bit, proving the 64-bit relaunch. The test learns "Contoso Notes" 1.2.0 and offers registry detection. |
| `08-ps1-settings-only` | `Set-ContosoBrowserPolicy.ps1` | PASS | Leaves nothing in Programs and Features, so marker detection. Sets two harmless Edge policies. |
| `09-ps1-fails` | `Install-BrokenApp.ps1` | **FAIL** (exit 1603) | Failure reporting |
| `10-ps1-needs-restart` | `Install-NeedsRestart.ps1` | PASS (exit 3010) | Restart return codes still count as success |
| `11-ps1-named-install` | `Install.ps1` | PASS | Name clash with PackPilot's own wrapper; files go in `Files\` inside the package |
| `12-ps1-hangs-on-dialog` | `Install-WaitsForClick.ps1` | **FAIL** (time limit) | A "silent" install that never finishes. Set **Install time limit** to 5 first. |

Don't run the scripts on your own PC; they're meant for the Sandbox (07 and 10 write to Program Files/ProgramData, 08 sets Edge policies).
