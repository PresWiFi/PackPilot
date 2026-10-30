# PackPilot guide

Reference details. For the overview, see the [README](../README.md).

## Upload to Intune (in testing)

1. Pick the **cloud**: Commercial / GCC, GCC High, DoD, or China (21Vianet). This sets the sign-in and Graph endpoints (`login.microsoftonline.us` + `graph.microsoft.us` for GCC High, `dod-graph.microsoft.us` for DoD).
2. Optionally enter the **tenant** (`contoso.onmicrosoft.com` or its ID), then **Sign in**. A browser window opens.
3. If a Win32 app with the same name exists, PackPilot offers to **update** it (new package, same assignments) instead of creating a duplicate.
4. Optionally **assign** it: Required, Available or Uninstall, to a group, all devices or all users.

**Permissions:** delegated `DeviceManagementApps.ReadWrite.All` and `Group.Read.All`, plus an Intune role that can manage apps. By default PackPilot signs in through **Microsoft Graph Command Line Tools** (the public app `Connect-MgGraph` uses), so there's nothing to register; an admin consents once. To use your own app registration, open **Sign-in app (advanced)** and enter its client ID. It needs those delegated permissions, "Allow public client flows" on, and the redirect URI `http://localhost`.

Tokens stay in memory and are gone when PackPilot closes. Each upload is recorded in `project.json` and the report.

## What it writes

Each app gets its own folder (default `Documents\PackPilot\<App>_<Version>`):

| Path | What it is |
|---|---|
| `Package\<App>.intunewin` | The package for Intune |
| `Source\` | What's inside it: the installer, `Install.ps1`, `Uninstall.ps1` |
| `Deploy\DEPLOYMENT.md` | Step-by-step Intune settings, pilot checks, log locations |
| `Deploy\Deploy-ToIntune.ps1` | Uploads and assigns the app with the IntuneWin32App module |
| `Deploy\Detect.ps1` | Detection script |
| `Deploy\intune-app.json` | All settings, machine-readable |
| `SandboxTest\Results\<run>\` | `result.json`, `progress.log` and every log from a test |
| `Report.html` | What was done, verification, test results, deploy settings |
| `project.json` | Reopen the project with **Open project** |

Elsewhere: settings in `%LOCALAPPDATA%\PackPilot\settings.json`, and Sandbox staging in `%LOCALAPPDATA%\PackPilot\SandboxRuns` and `Session` (cleaned up automatically).

## Intune settings it uses

- Install: `powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\Install.ps1`
- Uninstall: the same with `.\Uninstall.ps1`
- Install behavior System; restart based on return codes; Intune's default return codes.
- Device logs: `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\PackPilot-<App>-Install.log` (also `-Uninstall.log`, `-MSI-Install.log`), which **Collect diagnostics** picks up.

| Type | Default detection | Uninstall |
|---|---|---|
| MSI | MSI product code (built-in rule) | `msiexec /x {ProductCode}` |
| EXE | Programs and Features name (detection script), refined by the Sandbox test | Looked up at uninstall time (QuietUninstallString, or UninstallString plus the engine's silent flag) |
| PS1 | Registry marker written by `Install.ps1` after the script exits 0 | Your uninstall script (an `Uninstall*.ps1` next to it is picked up automatically) |

## Limits

- A Sandbox pass is strong evidence, not a guarantee. Pilot on a small group first.
- The Sandbox can't reboot mid-test (3010/1641 are reported, not acted on). Apps that need virtualization themselves (Hyper-V, WSL, Docker Desktop) won't run in it.
- Per-user installers (Squirrel and the like) land in the SYSTEM profile when installed as SYSTEM. The test flags this.
- Unsigned installers are blocked where Smart App Control or App Control for Business is on, including the Sandbox on such a PC. The test reports this as an App Control failure.
- Engine detection is static. For an unknown EXE the `/S` default is a guess; the Sandbox test confirms it.

## Build from source

Needs the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```powershell
.\build.ps1 -Test                # self-checks, then dist\PackPilot.exe (self-contained)
.\build.ps1 -FrameworkDependent  # small exe; needs the .NET 8 Desktop Runtime
```

The self-checks (`dotnet run --project tests\PackPilot.Tests`) build a real package, decrypt it and compare the bytes, reject a tampered package, and run the full Intune upload against a simulated Graph service. They run on any OS.

**Releases** are built by GitHub Actions on Windows (`.github/workflows/release.yml`): push a tag like `v0.2.0` and it runs the self-checks, builds the exe, and publishes the release with notes from `docs/release-notes/<tag>.md`.
The workflow can also sign the exe through SignPath if the `SIGNPATH_*` repo variables and secret are set; without them it skips signing.

**Layout:** `src/PackPilot.Core` (analysis, `.intunewin` build and verify, scripts, Sandbox, report; `Intune/` for sign-in and Graph upload), `src/PackPilot` (the window), `tests/PackPilot.Tests`, `tools/Test-SandboxHost.ps1` (diagnoses Sandbox start-up failures), `test-installers/` (sample installers and scripts to try).
