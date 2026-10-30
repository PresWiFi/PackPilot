using System.Text.Json.Serialization;

namespace PackPilot.Core;

public enum InstallerKind { Msi, Exe, PowerShell }

public enum DetectionMethod
{
    /// <summary>Intune's built-in MSI rule: the product code is registered.</summary>
    MsiProductCode,
    /// <summary>An entry in Programs and Features whose name matches a pattern (and optional minimum version).</summary>
    Registry,
    /// <summary>A file exists (and optional minimum file version).</summary>
    File,
    /// <summary>A registry key that Install.ps1 writes after a successful install. For scripts that leave no trace.</summary>
    Marker,
}

/// <summary>Everything PackPilot knows about one app. Edited in the UI, saved as intune-app.json.</summary>
public class AppDefinition
{
    // Source
    public string SourcePath { get; set; } = "";
    public InstallerKind Kind { get; set; }
    public bool IncludeSourceFolder { get; set; }
    /// <summary>Subfolder of the package holding the installer files: "" or "Files" (when a name clashes with Install.ps1).</summary>
    public string PayloadFolder { get; set; } = "";

    // What the analyzer found
    public string Engine { get; set; } = "";
    public string EngineLabel { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string AnalyzerNotes { get; set; } = "";
    public string Architecture { get; set; } = "unknown";
    public string ArchitectureNote { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public long SourceSize { get; set; }
    public string Signature { get; set; } = "";
    public string UpgradeCode { get; set; } = "";
    public string MsiAllUsers { get; set; } = "";

    // App info (Intune "App information" page)
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";

    // Install
    public string InstallArgs { get; set; } = "";

    // Uninstall. Uninstall.ps1 tries these in order: UninstallFile, ProductCode, DisplayNamePattern lookup.
    public string ProductCode { get; set; } = "";
    public string UninstallFile { get; set; } = "";
    public string UninstallArgs { get; set; } = "";
    /// <summary>Added to a registry UninstallString when the app has no QuietUninstallString.</summary>
    public string UninstallSilentArgs { get; set; } = "";

    // Detection
    public DetectionMethod Detection { get; set; }
    public string DisplayNamePattern { get; set; } = "";
    public string DetectMinVersion { get; set; } = "";
    public string DetectFilePath { get; set; } = "";

    // Sandbox options
    public bool SandboxNoNetwork { get; set; }
    public bool SandboxSkipUninstall { get; set; }
    public int SandboxTimeoutMinutes { get; set; } = 60;

    [JsonIgnore]
    public string AppId => Util.SafeId(Name);

    [JsonIgnore]
    public string SetupFileName => Path.GetFileName(SourcePath);

    /// <summary>The installer's path inside the package.</summary>
    [JsonIgnore]
    public string SetupInPackage => PayloadFolder == "" ? SetupFileName : $"{PayloadFolder}\\{SetupFileName}";

    public string MarkerKey => $@"HKLM:\SOFTWARE\PackPilot\Apps\{AppId}";

    public const string InstallCommand =
        "powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\\Install.ps1";
    public const string UninstallCommand =
        "powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -WindowStyle Hidden -File .\\Uninstall.ps1";

    /// <summary>The installer's own command line, without the PowerShell wrapper. For reference and for other tools.</summary>
    public string RawInstallCommand => Kind switch
    {
        InstallerKind.Msi => $"msiexec.exe /i \"{SetupInPackage}\" {InstallArgs}".Trim(),
        InstallerKind.Exe => $"\"{SetupInPackage}\" {InstallArgs}".Trim(),
        _ => $"powershell.exe -ExecutionPolicy Bypass -NoProfile -File \".\\{SetupInPackage}\" {InstallArgs}".Trim(),
    };

    public string RawUninstallCommand
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UninstallFile)) return $"{Util.QuoteIfNeeded(UninstallFile)} {UninstallArgs}".Trim();
            if (!string.IsNullOrWhiteSpace(ProductCode)) return $"msiexec.exe /x {ProductCode} /qn /norestart";
            if (!string.IsNullOrWhiteSpace(DisplayNamePattern))
                return $"(looked up at uninstall time: the Programs and Features entry named '{DisplayNamePattern}'{(string.IsNullOrWhiteSpace(UninstallSilentArgs) ? "" : $", plus {UninstallSilentArgs}")})";
            return "(none)";
        }
    }

    public string DetectionSummary => Detection switch
    {
        DetectionMethod.MsiProductCode => $"MSI product code {ProductCode}" + (string.IsNullOrWhiteSpace(DetectMinVersion) ? "" : $", version >= {DetectMinVersion}"),
        DetectionMethod.Registry => $"Programs and Features entry named '{DisplayNamePattern}'" + (string.IsNullOrWhiteSpace(DetectMinVersion) ? "" : $", version >= {DetectMinVersion}"),
        DetectionMethod.File => $"File {DetectFilePath} exists" + (string.IsNullOrWhiteSpace(DetectMinVersion) ? "" : $", file version >= {DetectMinVersion}"),
        _ => $"Registry key {MarkerKey} (Version = {Version}), written by Install.ps1",
    };
}

public class IntuneWinInfo
{
    public string PackagePath { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageSha256 { get; set; } = "";
    public long UnencryptedContentSize { get; set; }
    public string FileDigest { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public bool Verified { get; set; }
    public List<string> VerifyChecks { get; set; } = new();
    public DateTime BuiltAt { get; set; }
}

public class SandboxStep
{
    public string Step { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

public class LearnedValues
{
    public string DisplayName { get; set; } = "";
    public string DisplayNameMatch { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string RegistryKey { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string DetectFile { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string UninstallString { get; set; } = "";
}

public class SandboxResult
{
    public string RunId { get; set; } = "";
    public string App { get; set; } = "";
    public string RunAt { get; set; } = "";
    public string Verdict { get; set; } = "";
    public List<SandboxStep> Steps { get; set; } = new();
    public LearnedValues? Learned { get; set; }
    public string ResultsFolder { get; set; } = "";
    public int InstallExitCode { get; set; } = -1;
    public int InstallSeconds { get; set; }
    /// <summary>0 = a fresh Sandbox for this test alone; N = the Nth test in a reused Sandbox.</summary>
    public int SessionRun { get; set; }
    /// <summary>What Install.ps1 / Uninstall.ps1 printed (their log lines), for diagnosing failures.</summary>
    public string InstallOutput { get; set; } = "";
    public string UninstallOutput { get; set; } = "";
    /// <summary>SHA-256 of the package that was tested, so PackPilot knows whether a result is for the current build.</summary>
    public string PackageSha256 { get; set; } = "";
}

/// <summary>A timestamped line for the "what was done" part of the report.</summary>
public record LogEntry(DateTime At, string Text);
