using System.Text;
using System.Text.Json;

namespace PackPilot.Core;

/// <summary>Everything an admin needs to put the package into Intune, by hand or by script.</summary>
public static class DeploymentKit
{
    public static void Write(Project p)
    {
        var a = p.App;
        Directory.CreateDirectory(p.DeployDir);
        Util.WritePs1(Path.Combine(p.DeployDir, "Detect.ps1"), Scripts.Detect(a));
        Util.WritePs1(Path.Combine(p.DeployDir, "Deploy-ToIntune.ps1"), DeployScript(p));
        Util.WriteText(Path.Combine(p.DeployDir, "DEPLOYMENT.md"), Guide(p));
        File.WriteAllText(Path.Combine(p.DeployDir, "intune-app.json"), JsonSerializer.Serialize(Settings(p), Project.JsonOpts));
    }

    /// <summary>Requirement architecture for the IntuneWin32App module (1.5.0: x64, x86, arm64, x64x86, AllWithARM64).</summary>
    static string ModuleArch(AppDefinition a) => a.Architecture switch { "x64" => "x64", "arm64" => "arm64", _ => "x64x86" };

    public static string ArchText(AppDefinition a) => a.Architecture switch
    {
        "x64" => "x64 only",
        "arm64" => "ARM64 only",
        _ => "x64 and x86 (add ARM64 if the app supports it)",
    };

    public static object Settings(Project p)
    {
        var a = p.App;
        return new
        {
            appInformation = new { name = a.Name, description = a.Description, publisher = a.Publisher, appVersion = a.Version },
            program = new
            {
                installCommand = AppDefinition.InstallCommand,
                uninstallCommand = AppDefinition.UninstallCommand,
                installBehavior = "System",
                deviceRestartBehavior = "Determine behavior based on return codes",
                installTimeMinutes = 60,
                allowAvailableUninstall = true,
                returnCodes = ReturnCodes.Select(r => new { code = r.Code, type = r.Type }),
                rawInstallCommand = a.RawInstallCommand,
                rawUninstallCommand = a.RawUninstallCommand,
            },
            requirements = new { architecture = ArchText(a), minimumOs = "Windows 10 1809" },
            detection = new
            {
                method = a.Detection.ToString(),
                summary = a.DetectionSummary,
                productCode = a.ProductCode,
                displayNamePattern = a.DisplayNamePattern,
                minimumVersion = a.DetectMinVersion,
                filePath = a.DetectFilePath,
                script = "Detect.ps1",
            },
            package = p.Package == null ? null : new
            {
                file = Path.GetFileName(p.PackageFile),
                size = p.Package.PackageSize,
                sha256 = p.Package.PackageSha256,
                setupFile = "Install.ps1",
                contents = p.Package.Files,
            },
            source = new { file = a.SetupFileName, sha256 = a.SourceSha256, size = a.SourceSize, signature = a.Signature, engine = a.EngineLabel },
        };
    }

    public static readonly (int Code, string Type)[] ReturnCodes =
    {
        (0, "Success"), (1707, "Success"), (3010, "Soft reboot"), (1641, "Hard reboot"), (1618, "Retry"),
    };

    public static string DeployScript(Project p)
    {
        var a = p.App;
        string Q(string s) => Util.PsQuote(s);
        var detection = a.Detection == DetectionMethod.MsiProductCode
            ? $"New-IntuneWin32AppDetectionRuleMSI -ProductCode {Q(a.ProductCode)}" +
              (string.IsNullOrWhiteSpace(a.DetectMinVersion) ? "" : $" -ProductVersionOperator 'greaterThanOrEqual' -ProductVersion {Q(a.DetectMinVersion)}")
            : "New-IntuneWin32AppDetectionRuleScript -ScriptFile (Join-Path $PSScriptRoot 'Detect.ps1') -EnforceSignatureCheck $false -RunAs32Bit $false";

        return $$"""
            <#
                Deploy-ToIntune.ps1 - written by PackPilot for {{a.Name}} {{a.Version}}.
                Uploads the .intunewin to Intune as a Win32 app with the tested settings, and can assign it.
                Uses the community IntuneWin32App module (github.com/MSEndpointMgr/IntuneWin32App).

                You need an Entra app registration with the delegated Microsoft Graph permission
                DeviceManagementApps.ReadWrite.All (admin consented) and "Allow public client flows" on,
                with redirect URI http://localhost. Pass its Application (client) ID as -ClientId.

                Examples:
                  .\Deploy-ToIntune.ps1 -TenantId contoso.onmicrosoft.com -ClientId <app id>
                  .\Deploy-ToIntune.ps1 -TenantId contoso.onmicrosoft.com -ClientId <app id> -GroupId <Entra group object id> -Intent required
            #>
            param(
                [Parameter(Mandatory)][string]$TenantId,
                [Parameter(Mandatory)][string]$ClientId,
                [string]$GroupId,
                [ValidateSet('required', 'available', 'uninstall')][string]$Intent = 'available',
                [string]$PackagePath = (Join-Path $PSScriptRoot '..\Package\{{Path.GetFileName(p.PackageFile)}}')
            )
            $ErrorActionPreference = 'Stop'

            if (-not (Get-Module -ListAvailable -Name IntuneWin32App)) {
                Write-Host 'Installing the IntuneWin32App module for the current user...'
                Install-Module -Name IntuneWin32App -Scope CurrentUser -Force -AllowClobber
            }
            Import-Module IntuneWin32App
            Connect-MSIntuneGraph -TenantID $TenantId -ClientID $ClientId -Interactive | Out-Null

            $detection   = {{detection}}
            $requirement = New-IntuneWin32AppRequirementRule -Architecture {{Q(ModuleArch(a))}} -MinimumSupportedWindowsRelease 'W10_1809'

            $app = Add-IntuneWin32App -FilePath (Resolve-Path $PackagePath).Path `
                -DisplayName {{Q(a.Name)}} `
                -Description {{Q(a.Description)}} `
                -Publisher {{Q(a.Publisher)}} `
                -AppVersion {{Q(a.Version)}} `
                -InstallExperience 'system' `
                -RestartBehavior 'basedOnReturnCode' `
                -InstallCommandLine {{Q(AppDefinition.InstallCommand)}} `
                -UninstallCommandLine {{Q(AppDefinition.UninstallCommand)}} `
                -DetectionRule $detection `
                -RequirementRule $requirement `
                -Verbose
            Write-Host "Created Win32 app '$($app.displayName)' with id $($app.id)" -ForegroundColor Green

            if ($GroupId) {
                Add-IntuneWin32AppAssignmentGroup -Include -ID $app.id -GroupID $GroupId -Intent $Intent -Notification 'showAll' -Verbose
                Write-Host "Assigned as $Intent to group $GroupId" -ForegroundColor Green
            } else {
                Write-Host 'Not assigned. Assign it in the Intune admin center, or run again with -GroupId.'
            }

            """;
    }

    public static string Guide(Project p)
    {
        var a = p.App;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        var pkg = Path.GetFileName(p.PackageFile);
        var test = p.LastTest;

        L($"# Deploy {a.Name} {a.Version} with Intune");
        L();
        L($"Packaged by PackPilot on {DateTime.Now:yyyy-MM-dd HH:mm}. " +
          (test == null ? "**Not tested in Windows Sandbox yet.**" : $"Last Sandbox test: **{test.Verdict}** ({test.RunAt})."));
        L();
        L("## Files");
        L();
        L("| File | What it is |");
        L("|---|---|");
        L($"| `Package\\{pkg}` | The package to upload to Intune |");
        L("| `Deploy\\Detect.ps1` | Custom detection script (if you use script detection) |");
        L("| `Deploy\\Deploy-ToIntune.ps1` | Uploads and assigns the app with PowerShell |");
        L("| `Deploy\\intune-app.json` | Every setting below, machine-readable |");
        L("| `Source\\` | What is inside the package (installer, Install.ps1, Uninstall.ps1) |");
        L("| `Report.html` | What PackPilot did, and the Sandbox results |");
        L();
        L("## Quickest: Upload to Intune from PackPilot");
        L();
        L("Click **4 Upload to Intune**, pick the cloud (Commercial/GCC, GCC High, DoD, China) and sign in. PackPilot creates the app with every setting below, " +
          "uploads the package, and can assign it. Run it again later to update the same app with a new build.");
        L();
        L("## Option A: Intune admin center");
        L();
        L("Apps > Windows > Add > App type **Windows app (Win32)** > Select.");
        L();
        L($"1. **App package file**: select `{pkg}`.");
        L("2. **App information**");
        L($"   - Name: `{a.Name}`");
        L($"   - Description: `{a.Description}`");
        L($"   - Publisher: `{a.Publisher}`");
        L($"   - App version: `{a.Version}`");
        L("3. **Program**");
        L($"   - Install command: `{AppDefinition.InstallCommand}`");
        L($"   - Uninstall command: `{AppDefinition.UninstallCommand}`");
        L("   - Installation time required (mins): `60`");
        L("   - Allow available uninstall: `Yes`");
        L("   - Install behavior: `System`");
        L("   - Device restart behavior: `Determine behavior based on return codes`");
        L("   - Return codes: keep the defaults (" + string.Join(", ", ReturnCodes.Select(r => $"{r.Code} {r.Type}")) + ")");
        L("4. **Requirements**");
        L($"   - Operating system architecture: {ArchText(a)}");
        L("   - Minimum operating system: Windows 10 1809 (or your baseline)");
        L("5. **Detection rules**");
        foreach (var line in DetectionSteps(a)) L("   " + line);
        L("6. **Dependencies** and **Supersedence**: none needed. To replace an older version, add it under Supersedence.");
        L("7. **Assignments**: assign to a small pilot group first (Required, or Available for enrolled devices).");
        L("8. **Review + create**. The upload takes a minute or two after you click Create.");
        L();
        L("## Option B: PowerShell");
        L();
        L("Needs an Entra app registration with delegated `DeviceManagementApps.ReadWrite.All` (see the top of the script).");
        L();
        L("```powershell");
        L("cd \"" + p.DeployDir + "\"");
        L(".\\Deploy-ToIntune.ps1 -TenantId <tenant>.onmicrosoft.com -ClientId <app id>");
        L("# and assign it to a group in one go:");
        L(".\\Deploy-ToIntune.ps1 -TenantId <tenant>.onmicrosoft.com -ClientId <app id> -GroupId <group object id> -Intent required");
        L("```");
        L();
        L("## Check it on a pilot device");
        L();
        L("```powershell");
        L("# Ask the Intune agent to check for new apps now");
        L("Restart-Service IntuneManagementExtension");
        L();
        L("# Watch the agent and this app's own logs");
        L("Get-Content \"$env:ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\AppWorkload.log\" -Tail 50 -Wait");
        L($"Get-Content \"$env:ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\PackPilot-{a.AppId}-Install.log\"");
        L();
        L("# Run the detection script by hand (exit 0 + output = installed)");
        L($"powershell.exe -ExecutionPolicy Bypass -File \"{Path.Combine(p.DeployDir, "Detect.ps1")}\"; $LASTEXITCODE");
        L("```");
        L();
        L("To run the install by hand as SYSTEM (the way Intune does), from an elevated prompt with Sysinternals PsExec:");
        L();
        L("```powershell");
        L("psexec.exe -s -i powershell.exe");
        L("# then, in the new window:");
        L($"cd \"{p.SourceDir}\"");
        L(".\\Install.ps1; $LASTEXITCODE");
        L(".\\Uninstall.ps1; $LASTEXITCODE");
        L("```");
        L();
        L("## Command reference");
        L();
        L("| | Command |");
        L("|---|---|");
        L($"| Intune install | `{AppDefinition.InstallCommand}` |");
        L($"| Intune uninstall | `{AppDefinition.UninstallCommand}` |");
        L($"| Installer's own silent install | `{a.RawInstallCommand}` |");
        L($"| Installer's own silent uninstall | `{a.RawUninstallCommand}` |");
        L($"| Detection | {a.DetectionSummary} |");
        L();
        L("## Logs on the device");
        L();
        L("- `C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\PackPilot-" + a.AppId + "-Install.log` and `-Uninstall.log`: what Install.ps1/Uninstall.ps1 did");
        if (a.Kind == InstallerKind.Msi || !string.IsNullOrEmpty(a.ProductCode))
            L("- `...\\Logs\\PackPilot-" + a.AppId + "-MSI-Install.log`: the verbose MSI log");
        L("- `...\\Logs\\AppWorkload.log` and `IntuneManagementExtension.log`: the Intune agent's view (download, install, detection)");
        L("- These are in the Logs folder that **Collect diagnostics** in the Intune admin center uploads.");
        L();
        L("## To update the app later");
        L();
        L("Import the new installer in PackPilot, build, test, then in Intune either edit this app and upload the new `.intunewin` " +
          "(bump App version), or add a new app that supersedes this one.");
        return sb.ToString();
    }

    public static List<string> DetectionSteps(AppDefinition a)
    {
        var s = new List<string>();
        switch (a.Detection)
        {
            case DetectionMethod.MsiProductCode:
                s.Add("- Rules format: `Manually configure detection rules` > Add > Rule type `MSI`");
                s.Add($"- MSI product code: `{a.ProductCode}`");
                s.Add(string.IsNullOrWhiteSpace(a.DetectMinVersion)
                    ? "- MSI product version check: `No`"
                    : $"- MSI product version check: `Yes`, operator `Greater than or equal to`, value `{a.DetectMinVersion}`");
                s.Add("- (Or use `Deploy\\Detect.ps1` as a custom script: it checks the same thing.)");
                break;
            case DetectionMethod.Registry:
                s.Add("- Rules format: `Use a custom detection script`");
                s.Add("- Script file: `Deploy\\Detect.ps1`");
                s.Add("- Run script as 32-bit process on 64-bit clients: `No`");
                s.Add("- Enforce script signature check and run script silently: `No`");
                s.Add($"- It looks in Programs and Features for `{a.DisplayNamePattern}`" + (string.IsNullOrWhiteSpace(a.DetectMinVersion) ? "" : $" with version {a.DetectMinVersion} or later") + ". A script is used because the built-in registry rule cannot match a display name with wildcards.");
                break;
            case DetectionMethod.File:
                var (dir, fileName) = Util.SplitWindowsPath(a.DetectFilePath);
                s.Add("- Rules format: `Manually configure detection rules` > Add > Rule type `File`");
                s.Add($"- Path: `{dir}`");
                s.Add($"- File or folder: `{fileName}`");
                s.Add(string.IsNullOrWhiteSpace(a.DetectMinVersion)
                    ? "- Detection method: `File or folder exists`"
                    : $"- Detection method: `Version`, operator `Greater than or equal to`, value `{a.DetectMinVersion}`");
                s.Add("- Associated with a 32-bit app on 64-bit clients: `No`");
                break;
            default:
                s.Add("- Rules format: `Manually configure detection rules` > Add > Rule type `Registry`");
                s.Add($"- Key path: `HKEY_LOCAL_MACHINE\\SOFTWARE\\PackPilot\\Apps\\{a.AppId}`");
                s.Add("- Value name: `Version`");
                s.Add($"- Detection method: `String comparison`, operator `Equals`, value `{a.Version}`");
                s.Add("- Associated with a 32-bit app on 64-bit clients: `No`");
                s.Add("- (Or use `Deploy\\Detect.ps1` as a custom script.) Install.ps1 writes this key after the script exits 0.");
                break;
        }
        return s;
    }
}
