# PackPilot

Turn an `.exe`, `.msi` or `.ps1` into a tested Intune Win32 app.

> **Status:** building and Sandbox testing are ready to use. **Upload to Intune is still in testing**: try it with a test tenant or pilot group first, and [open an issue](https://github.com/PresWiFi/PackPilot/issues) if anything goes wrong.

![PackPilot after a passing Sandbox test](docs/screenshots/workspace.png)

## What it does

- **Reads the installer:** engine (MSI, Inno Setup, NSIS, WiX Burn, InstallShield and more), silent switches, version, product code, signature.
- **Builds the `.intunewin`** without IntuneWinAppUtil, and verifies it the way Intune does.
- **Tests it in Windows Sandbox** as SYSTEM: detect, install, detect, uninstall, detect.
- **Uploads it to Intune** through Microsoft Graph: Commercial/GCC, GCC High, DoD or China. *(In testing.)*
- **Writes a deployment kit:** portal settings, detection script, upload script and an HTML report.

## Download

Get `PackPilot.exe` from the [latest release](https://github.com/PresWiFi/PackPilot/releases/latest). It's one file, with no install, no admin rights and no .NET needed (Windows 10/11 x64).

Releases aren't code-signed yet, so SmartScreen may warn: click **More info** > **Run anyway**. Each release lists the file's SHA-256.

Testing needs **Windows Sandbox** (Windows Pro, Enterprise or Education). Turn it on from an admin PowerShell, then restart:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

## How to use it

1. Drop an installer on the window and check what it filled in.
2. **Build.**
3. **Test in Sandbox.** The test runs by itself; leave the Sandbox window open until PackPilot shows the result.
4. If the test learned something (like the exact app name for detection), click the red button to rebuild and retest.
5. **Upload to Intune**, or follow the deployment guide to add it by hand.

| | |
|---|---|
| ![Start screen](docs/screenshots/start.png) | ![Detection tab](docs/screenshots/detection.png) |
| ![Upload to Intune](docs/screenshots/upload.png) | ![Report](docs/screenshots/report.png) |

Permissions, output files, Intune settings, limits and building from source are in the **[guide](docs/GUIDE.md)**.

## Your PC

PackPilot installs nothing and changes no system settings. Installers you test only run inside Windows Sandbox, which is discarded when it closes. To remove PackPilot, delete `PackPilot.exe` and `%LOCALAPPDATA%\PackPilot`.

## Code signing policy

Code signing through [SignPath.io](https://about.signpath.io), with a certificate from [SignPath Foundation](https://signpath.org), has been applied for. Until it's approved, releases are unsigned.

- Committers and reviewers: [PresWiFi](https://github.com/PresWiFi)
- Approvers: [PresWiFi](https://github.com/PresWiFi)

Every release is built from this repository by GitHub Actions.

**Privacy:** this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. The only connections it makes are ones you start: **Upload to Intune** talks to Microsoft sign-in and Microsoft Graph ([Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement)), and Sandbox tests give the disposable Sandbox network access unless you turn it off.

## License

MIT, see [LICENSE](LICENSE). PackPilot builds on [SwitchHunt](https://github.com/deadarcher/SwitchHunt); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
