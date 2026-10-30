using System.Text.Json;
using System.Text.Json.Serialization;
using PackPilot.Core.Intune;

namespace PackPilot.Core;

/// <summary>
/// One app being packaged, and everything done to it. Lives in its own folder:
///   Source\      what goes into the .intunewin (installer + Install.ps1 + Uninstall.ps1)
///   Package\     the .intunewin
///   Deploy\      Detect.ps1, Deploy-ToIntune.ps1, DEPLOYMENT.md, intune-app.json
///   SandboxTest\ the Windows Sandbox test kit and its results
///   Report.html  what was done
///   project.json this file, so the project can be reopened
/// </summary>
public class Project
{
    public AppDefinition App { get; set; } = new();
    public string OutputRoot { get; set; } = "";
    public IntuneWinInfo? Package { get; set; }
    public List<SandboxResult> Tests { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public List<UploadRecord> Uploads { get; set; } = new();
    /// <summary>App settings the current package was built with; null when it has not been built.</summary>
    public string? BuiltFingerprint { get; set; }

    [JsonIgnore] public Action<string>? Logged { get; set; }

    /// <summary>Set on the first build and kept, so renaming the app or changing its version does not move the files.</summary>
    public string? FolderName { get; set; }

    [JsonIgnore] public string Folder => Path.Combine(OutputRoot, FolderName ?? $"{App.AppId}_{Util.SafeId(App.Version)}");
    [JsonIgnore] public string SourceDir => Path.Combine(Folder, "Source");
    [JsonIgnore] public string PackageDir => Path.Combine(Folder, "Package");
    [JsonIgnore] public string DeployDir => Path.Combine(Folder, "Deploy");
    [JsonIgnore] public string SandboxDir => Path.Combine(Folder, "SandboxTest");
    [JsonIgnore] public string ReportPath => Path.Combine(Folder, "Report.html");
    [JsonIgnore] public string PackageFile => Path.Combine(PackageDir, $"{App.AppId}.intunewin");
    [JsonIgnore] public SandboxResult? LastTest => Tests.LastOrDefault();

    /// <summary>True when settings changed since the package was built, so the package is out of date.</summary>
    [JsonIgnore] public bool PackageStale => Package == null || BuiltFingerprint != Fingerprint();

    public string Fingerprint() => JsonSerializer.Serialize(App, JsonOpts);

    public void Note(string text)
    {
        Log.Add(new LogEntry(DateTime.Now, text));
        Logged?.Invoke(text);
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "project.json"), JsonSerializer.Serialize(this, JsonOpts));
    }

    public static Project Load(string projectJson)
    {
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(projectJson), JsonOpts)
                ?? throw new InvalidDataException("project.json is empty.");
        return p;
    }
}
