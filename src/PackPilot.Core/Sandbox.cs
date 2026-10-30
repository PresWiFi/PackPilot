using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace PackPilot.Core;

public static class Sandbox
{
    public static string ExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsSandbox.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    public const string EnableCommand =
        "Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All";

    static readonly string[] SandboxProcesses =
        { "WindowsSandboxClient", "WindowsSandboxRemoteSession", "WindowsSandboxServer", "vmmemWindowsSandbox" };

    /// <summary>Only one Windows Sandbox can run at a time.</summary>
    public static bool IsRunning() => SandboxProcesses.Any(n => Process.GetProcessesByName(n).Length > 0);

    /// <summary>
    /// Where a run is staged. Windows Sandbox maps this folder instead of the project folder: a local path
    /// with no sync client (OneDrive, Syncthing) or network share in the way, which mapped folders handle badly.
    /// </summary>
    public static string StageDir(string runId) => Path.Combine(StageRoot, runId);

    static string StageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackPilot", "SandboxRuns");

    /// <summary>Stages the package and test kit, writes the .wsb, and returns the run id.</summary>
    public static string Prepare(Project p)
    {
        if (p.Package == null || !File.Exists(p.PackageFile)) throw new InvalidOperationException("Build the package first.");
        var a = p.App;
        CleanOldStages();
        var runId = NewRunId();
        var stage = StageDir(runId);
        var (pkgDir, testDir) = WriteKit(p, stage, runId);

        // vGPU off: installers don't need it, and GPU-driver problems are a common cause of the Sandbox
        // VM dying at start (0x80370106). Memory is left at the Sandbox default.
        string E(string s) => SecurityElement.Escape(s) ?? "";
        var wsb = $"""
            <Configuration>
              <vGPU>Disable</vGPU>
              <Networking>{(a.SandboxNoNetwork ? "Disable" : "Default")}</Networking>
              <MappedFolders>
                <MappedFolder>
                  <HostFolder>{E(pkgDir)}</HostFolder>
                  <SandboxFolder>C:\PackPilot\Package</SandboxFolder>
                  <ReadOnly>true</ReadOnly>
                </MappedFolder>
                <MappedFolder>
                  <HostFolder>{E(testDir)}</HostFolder>
                  <SandboxFolder>C:\PackPilot\Test</SandboxFolder>
                  <ReadOnly>false</ReadOnly>
                </MappedFolder>
              </MappedFolders>
              <LogonCommand>
                <Command>powershell.exe -ExecutionPolicy Bypass -NoProfile -File C:\PackPilot\Test\Test-InSandbox.ps1</Command>
              </LogonCommand>
            </Configuration>
            """;
        Util.WriteText(WsbPath(runId), wsb);
        // A copy in the project, so the run can be repeated by double-clicking it (while the stage exists).
        Directory.CreateDirectory(p.SandboxDir);
        File.Copy(WsbPath(runId), Path.Combine(p.SandboxDir, "PackPilot.wsb"), true);
        p.Note($"Staged the Sandbox test in {stage} (run {runId}, networking {(a.SandboxNoNetwork ? "off" : "on")}, vGPU off).");
        return runId;
    }

    static string NewRunId()
    {
        // Two tests in the same second would share a folder: add a suffix if needed.
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (int i = 2; Directory.Exists(StageDir(id)) || Directory.Exists(Path.Combine(SessionRoot, "Jobs", id)); i++)
            id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + i;
        return id;
    }

    /// <summary>Writes Package\ (the .intunewin) and Test\ (script, config, Detect.ps1) under <paramref name="dir"/>.</summary>
    static (string PkgDir, string TestDir) WriteKit(Project p, string dir, string runId)
    {
        var a = p.App;
        var pkgDir = Path.Combine(dir, "Package");
        var testDir = Path.Combine(dir, "Test");
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(testDir);

        File.Copy(p.PackageFile, Path.Combine(pkgDir, Path.GetFileName(p.PackageFile)), true);
        Util.WritePs1(Path.Combine(testDir, "Test-InSandbox.ps1"), Scripts.SandboxTest);
        File.Copy(Path.Combine(p.DeployDir, "Detect.ps1"), Path.Combine(testDir, "Detect.ps1"), true);
        var cfg = new
        {
            runId,
            appName = $"{a.Name} {a.Version}",
            timeoutMinutes = Math.Clamp(a.SandboxTimeoutMinutes, 5, 240),
            skipUninstall = a.SandboxSkipUninstall,
            displayNamePattern = a.DisplayNamePattern,
        };
        File.WriteAllText(Path.Combine(testDir, "test-config.json"), JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        return (pkgDir, testDir);
    }

    public static string WsbPath(string runId) => Path.Combine(StageDir(runId), "PackPilot.wsb");

    // ── Session mode: one Sandbox, many tests ────────────────────────────────

    /// <summary>Mapped into a session Sandbox as C:\PackPilot\Session. Jobs\<run id>\ holds each queued test.</summary>
    public static string SessionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackPilot", "Session");

    record SessionFile(string SessionId, bool NoNetwork);
    record Beat(string SessionId, long Seq, int Runs, string State);

    static SessionFile? ReadSessionFile()
    {
        try { return JsonSerializer.Deserialize<SessionFile>(ReadShared(Path.Combine(SessionRoot, "session.json")), Project.JsonOpts); }
        catch { return null; }
    }

    static Beat? ReadBeat()
    {
        try { return JsonSerializer.Deserialize<Beat>(ReadShared(Path.Combine(SessionRoot, "agent.json")).TrimStart('\uFEFF'), Project.JsonOpts); }
        catch { return null; }
    }

    /// <summary>What PackPilot knows about the open session Sandbox. Null when there isn't one.</summary>
    public record SessionState(bool NoNetwork, int TestsRun, bool Dirty);

    /// <summary>
    /// Is a PackPilot session Sandbox open and its agent alive? Checks that the heartbeat is recent, or,
    /// if the clocks disagree, that it is still counting (takes a few seconds then).
    /// </summary>
    public static async Task<SessionState?> GetSessionAsync()
    {
        if (!IsRunning()) return null;
        var session = ReadSessionFile();
        var beat = ReadBeat();
        if (session == null || beat == null || beat.SessionId != session.SessionId || beat.State == "closing") return null;
        var file = new FileInfo(Path.Combine(SessionRoot, "agent.json"));
        bool alive = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(20);
        if (!alive)
        {
            await Task.Delay(5000);
            var again = ReadBeat();
            alive = again != null && again.Seq != beat.Seq;
        }
        return alive ? new SessionState(session.NoNetwork, beat.Runs, SessionDirty) : null;
    }

    /// <summary>True when an earlier test in the session did not end with the app cleanly uninstalled.</summary>
    public static bool SessionDirty
    {
        get => File.Exists(Path.Combine(SessionRoot, "dirty.flag"));
        set
        {
            var f = Path.Combine(SessionRoot, "dirty.flag");
            try { if (value) File.WriteAllText(f, DateTime.Now.ToString("s")); else if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    /// <summary>A test left the Sandbox clean if the app was uninstalled and detection agreed.</summary>
    public static bool LeftClean(SandboxResult r) => r.Steps.Any(s => s.Step == "Detect after uninstall" && s.Ok);

    /// <summary>Opens a new session Sandbox. Any earlier session files are cleared first.</summary>
    public static void StartSession(Project p, bool noNetwork)
    {
        if (IsRunning()) throw new InvalidOperationException("A Windows Sandbox is already open. Close it first.");
        if (Directory.Exists(SessionRoot))
            try { Directory.Delete(SessionRoot, true); } catch { /* a locked log file is not worth failing over */ }
        Directory.CreateDirectory(Path.Combine(SessionRoot, "Jobs"));
        Util.WritePs1(Path.Combine(SessionRoot, "PackPilot-Agent.ps1"), Scripts.SandboxAgent);
        File.WriteAllText(Path.Combine(SessionRoot, "session.json"),
            JsonSerializer.Serialize(new SessionFile(Guid.NewGuid().ToString("N"), noNetwork), Project.JsonOpts));
        string E(string s) => SecurityElement.Escape(s) ?? "";
        var wsb = $"""
            <Configuration>
              <vGPU>Disable</vGPU>
              <Networking>{(noNetwork ? "Disable" : "Default")}</Networking>
              <MappedFolders>
                <MappedFolder>
                  <HostFolder>{E(SessionRoot)}</HostFolder>
                  <SandboxFolder>C:\PackPilot\Session</SandboxFolder>
                  <ReadOnly>false</ReadOnly>
                </MappedFolder>
              </MappedFolders>
              <LogonCommand>
                <Command>powershell.exe -ExecutionPolicy Bypass -NoProfile -File C:\PackPilot\Session\PackPilot-Agent.ps1</Command>
              </LogonCommand>
            </Configuration>
            """;
        var wsbPath = Path.Combine(SessionRoot, "PackPilot-Session.wsb");
        Util.WriteText(wsbPath, wsb);
        Process.Start(new ProcessStartInfo(ExePath, $"\"{wsbPath}\"") { UseShellExecute = true });
        p.Note($"Started a reusable test Sandbox (networking {(noNetwork ? "off" : "on")}, vGPU off).");
    }

    /// <summary>Hands a test to the session Sandbox. The agent inside picks it up within a couple of seconds.</summary>
    public static string QueueJob(Project p)
    {
        if (p.Package == null || !File.Exists(p.PackageFile)) throw new InvalidOperationException("Build the package first.");
        var runId = NewRunId();
        var jobDir = Path.Combine(SessionRoot, "Jobs", runId);
        WriteKit(p, jobDir, runId);
        File.WriteAllText(Path.Combine(jobDir, "job.ready"), "");   // last, so the agent never sees a half-copied job
        p.Note($"Sent the test to the open test Sandbox (run {runId}).");
        return runId;
    }

    /// <summary>Asks the agent to shut the session Sandbox down, and waits up to <paramref name="wait"/> for it to go.</summary>
    public static async Task<bool> CloseSessionAsync(TimeSpan wait)
    {
        try { File.WriteAllText(Path.Combine(SessionRoot, "shutdown.request"), ""); } catch { }
        var until = DateTime.Now + wait;
        while (DateTime.Now < until)
        {
            if (!IsRunning()) return true;
            await Task.Delay(1500);
        }
        return !IsRunning();
    }

    static void CleanOldStages()
    {
        try
        {
            if (!Directory.Exists(StageRoot)) return;
            foreach (var d in Directory.GetDirectories(StageRoot))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-2)) Directory.Delete(d, true);
        }
        catch { /* best effort */ }
    }

    public static void Launch(Project p, string runId)
    {
        Process.Start(new ProcessStartInfo(ExePath, $"\"{WsbPath(runId)}\"") { UseShellExecute = true });
        p.Note("Started Windows Sandbox.");
    }

    public const string CrashHelp =
        "Windows Sandbox stopped before the test could finish. If it showed \"The virtual machine or container exited unexpectedly (0x80370106)\", " +
        "the Sandbox VM itself crashed while starting; PackPilot's test had not run yet.\n\n" +
        "Try, in order:\n" +
        "1. Open Windows Sandbox from the Start menu. If that crashes too, the problem is the PC, not the package.\n" +
        "2. Run tools\\Test-SandboxHost.ps1 (in the PackPilot folder) in an admin PowerShell. It checks Hyper-V, the hypervisor, and the Hyper-V-Compute event log, which names the real cause.\n" +
        "3. Common fixes: update the GPU driver; turn Windows Sandbox off, restart, turn it on, restart; run 'bcdedit /set hypervisorlaunchtype auto' and restart; " +
        "check third-party antivirus or virtualization software (VirtualBox, VMware, some EDR) that hooks Hyper-V.";

    /// <summary>Waits for the test to finish, passing each new progress line to <paramref name="progress"/>.</summary>
    public static async Task<SandboxResult> WaitForResultAsync(Project p, string runId, bool session, Action<string> progress, CancellationToken ct)
    {
        var jobDir = session ? Path.Combine(SessionRoot, "Jobs", runId) : StageDir(runId);
        var stageResults = Path.Combine(jobDir, "Test", "Results", runId);
        bool finished = false;
        var progressFile = Path.Combine(stageResults, "progress.log");
        var resultFile = Path.Combine(stageResults, "result.json");
        var keep = Path.Combine(p.SandboxDir, "Results", runId);
        var deadline = DateTime.Now.AddMinutes(2 * p.App.SandboxTimeoutMinutes + 25);
        var begun = DateTime.Now;
        int shown = 0, goneCount = 0;
        bool started = false, sawRunning = false;
        try
        {
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(2000, ct);
                if (File.Exists(progressFile))
                {
                    started = true;
                    var lines = ReadShared(progressFile).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                    foreach (var l in lines.Skip(shown)) progress(l);
                    shown = lines.Count;
                }
                if (File.Exists(resultFile))
                {
                    var r = JsonSerializer.Deserialize<SandboxResult>(ReadShared(resultFile).TrimStart('\uFEFF'), Project.JsonOpts)
                            ?? throw new InvalidDataException("result.json is empty.");
                    r.ResultsFolder = keep;
                    if (string.IsNullOrEmpty(r.RunId)) r.RunId = runId;
                    finished = true;
                    return r;
                }
                // The Sandbox went away without a result: it crashed or was closed. Two polls in a row, to
                // ride out the hand-over between Sandbox processes during start-up.
                bool running = IsRunning();
                sawRunning |= running;
                goneCount = running ? 0 : goneCount + 1;
                bool gone = (sawRunning && goneCount >= 2) || (!sawRunning && DateTime.Now - begun > TimeSpan.FromMinutes(3));
                if (gone)
                    throw new SandboxStoppedException(started
                        ? "Windows Sandbox closed while the test was running, so there is no result. Leave the Sandbox open until PackPilot shows the result.\n\n" + CrashHelp
                        : CrashHelp);
            }
            throw new TimeoutException("The Sandbox test did not finish in time. Check the Sandbox window and progress.log.");
        }
        finally
        {
            // Keep whatever the run produced with the project, including partial logs from a failed run.
            try { if (Directory.Exists(stageResults)) Util.CopyDirectory(stageResults, keep); } catch { }
            // A finished session job is no longer needed; its package copy would just pile up.
            if (session && finished) try { Directory.Delete(jobDir, true); } catch { }
        }
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Other silent switches worth trying when an installer's default one fails, by engine.</summary>
    static readonly Dictionary<string, string[]> SwitchAlternatives = new()
    {
        ["custom-7z"] = new[] { "/S", "/S /D=\"C:\\Program Files\\<App>\"", "/SILENT", "/VERYSILENT", "/quiet", "/q" },
        ["unknown-exe"] = new[] { "/S", "/SILENT", "/VERYSILENT /NORESTART", "/quiet /norestart", "/qn", "-s", "--silent" },
        ["nsis"] = new[] { "/S (capital S)", "/S /D=C:\\Program Files\\<App> (/D must be last, no quotes)" },
        ["inno"] = new[] { "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS", "/SILENT /SUPPRESSMSGBOXES /NORESTART" },
        ["installshield"] = new[] { "/s /v\"/qn REBOOT=ReallySuppress\"", "/s /f1\"setup.iss\" (needs a recorded response file)" },
        ["wix-burn"] = new[] { "/install /quiet /norestart", "/quiet /norestart" },
        ["advanced-installer"] = new[] { "/exenoui /qn /norestart", "/exenoui /quiet" },
    };

    /// <summary>
    /// Plain-language reasons a test failed and what to change, from the failed steps and exit codes.
    /// Empty when the test passed.
    /// </summary>
    public static List<string> FailureHints(AppDefinition a, SandboxResult r)
    {
        var h = new List<string>();
        SandboxStep? Step(string name) => r.Steps.FirstOrDefault(s => s.Step == name);
        var install = Step("Install");
        var output = r.InstallOutput ?? "";

        if (Step("Error") is { Ok: false } err)
            h.Add($"The test itself hit an error: {err.Detail}");
        if (Step("Detect before install") is { Ok: false })
            h.Add("Detection found the app on a clean Sandbox, so the rule matches something else (or an earlier test in a reused Sandbox left it installed). " +
                  "Make the display name more specific, add a minimum version, or test in a fresh Sandbox.");

        if (install is { Ok: false })
        {
            if (output.Contains("Application Control policy has blocked") || Step("App Control") is { Ok: false })
            {
                h.Add("Windows App Control blocked the installer: it is not code-signed, or not trusted by the policy. " +
                      "This is a real result, not a packaging fault. Any device that enforces Smart App Control or App Control for Business would block it too. " +
                      "On this PC the Sandbox most likely shares Smart App Control with Windows; check with (Get-MpComputerStatus).SmartAppControlState.");
                h.Add("Options: use a signed installer from the vendor (or their MSI, if it is signed); or, where devices use App Control for Business, " +
                      "set the Intune Management Extension as a managed installer, or add an allow rule for this file's hash. " +
                      "Changing install arguments will not help.");
                return h;
            }
            if (install.Detail.Contains("still running"))
                h.Add("The installer never finished: it is almost certainly showing a window that nobody can click when it runs as SYSTEM. The install arguments are not fully silent.");
            else if (output.Contains("Could not run the installer"))
                h.Add("Install.ps1 could not start the installer: " + output.Split('\n').Last(l => l.Contains("Could not run")).Trim());
            else switch (r.InstallExitCode)
            {
                case 1603: h.Add("Exit code 1603 is a fatal MSI error. The verbose MSI log (PackPilot-*-MSI-Install.log in the results Logs folder) shows the cause; search it for \"Return value 3\"."); break;
                case 1618: h.Add("Exit code 1618: another installation was running. Test again."); break;
                case 1638: h.Add("Exit code 1638: another version of this product is already installed."); break;
                case 1625 or 1643: h.Add($"Exit code {r.InstallExitCode}: blocked by policy."); break;
                case 2 or 87 or 1639:
                    h.Add($"Exit code {r.InstallExitCode} usually means the installer did not understand its arguments ({a.InstallArgs})."); break;
                default:
                    h.Add($"The installer exited with code {r.InstallExitCode} after {r.InstallSeconds} s. " +
                          (a.Kind == InstallerKind.PowerShell ? "Your script returned that code: check its output below."
                           : r.InstallSeconds <= 15 ? "A quick non-zero exit usually means the silent switch is wrong, or the installer rejected running as SYSTEM or in this Windows version."
                           : "Check the installer's own log, if it writes one."));
                    break;
            }
            if (a.Kind != InstallerKind.PowerShell && !install.Detail.Contains("Could not run"))
            {
                var alts = SwitchAlternatives.TryGetValue(a.Engine, out var x) ? x : SwitchAlternatives["unknown-exe"];
                var others = alts.Where(s => !string.Equals(s, a.InstallArgs, StringComparison.OrdinalIgnoreCase)).ToList();
                h.Add($"Install arguments now: {(a.InstallArgs.Length > 0 ? a.InstallArgs : "(none)")}. " +
                      (others.Count > 0 ? "Others to try for this installer type: " + string.Join("   ", others) + ". " : "") +
                      "The vendor's documentation or the SwitchHunt catalog is the quickest way to the right one.");
            }
        }
        else if (Step("Detect after install") is { Ok: false })
        {
            h.Add(r.Learned != null
                ? "The install worked but detection missed it. Use what the test learned (the red button) to fix the rule."
                : "The install worked but left nothing in Programs and Features. Switch detection to File (a file the app installs) or Marker.");
        }

        if (Step("Uninstall") is { Ok: false } un && install is { Ok: true })
            h.Add($"Uninstall failed ({un.Detail}). Set a custom uninstall command, or the silent flags added to its UninstallString (now: {(a.UninstallSilentArgs.Length > 0 ? a.UninstallSilentArgs : "none")}).");
        if (Step("Detect after uninstall") is { Ok: false })
            h.Add("The app was still detected after the uninstall: the uninstall did not remove it, or detection matches something it leaves behind.");
        if (Step("Install scope") is { Ok: false } scope)
            h.Add(scope.Detail);
        return h;
    }

    /// <summary>
    /// Turns what the Sandbox saw into detection and uninstall settings. Returns a line per change,
    /// empty when there was nothing new to use.
    /// </summary>
    public static List<string> ApplyLearned(AppDefinition a, LearnedValues l)
    {
        var changes = new List<string>();
        void Set(string label, string oldV, string newV, Action<string> apply)
        {
            if (string.IsNullOrWhiteSpace(newV) || oldV == newV) return;
            apply(newV);
            changes.Add($"{label}: {(string.IsNullOrEmpty(oldV) ? "(empty)" : oldV)} -> {newV}");
        }
        var version = Util.CleanVersion(l.DisplayVersion);

        if (!string.IsNullOrWhiteSpace(l.ProductCode))
        {
            Set("Product code", a.ProductCode, l.ProductCode, v => a.ProductCode = v);
            Set("Detection", a.Detection.ToString(), nameof(DetectionMethod.MsiProductCode), _ => a.Detection = DetectionMethod.MsiProductCode);
        }
        else if (!string.IsNullOrWhiteSpace(l.DisplayName))
        {
            Set("Detection", a.Detection.ToString(), nameof(DetectionMethod.Registry), _ => a.Detection = DetectionMethod.Registry);
        }
        else if (!string.IsNullOrWhiteSpace(l.DetectFile))
        {
            Set("Detection", a.Detection.ToString(), nameof(DetectionMethod.File), _ => a.Detection = DetectionMethod.File);
            Set("Detection file", a.DetectFilePath, l.DetectFile, v => a.DetectFilePath = v);
        }
        if (!string.IsNullOrWhiteSpace(l.DisplayName))
            Set("Display name", a.DisplayNamePattern, Util.NamePattern(l.DisplayName, l.DisplayVersion), v => a.DisplayNamePattern = v);
        if (a.Detection != DetectionMethod.Marker)
            Set("Minimum version", a.DetectMinVersion, version, v => a.DetectMinVersion = v);
        if (string.IsNullOrWhiteSpace(a.DetectFilePath) && !string.IsNullOrWhiteSpace(l.DetectFile))
            Set("Detection file (for reference)", a.DetectFilePath, l.DetectFile, v => a.DetectFilePath = v);

        if (a.Version == "1.0.0" && version.Length > 0) Set("Version", a.Version, version, v => a.Version = v);
        if (a.Publisher == "Unknown publisher") Set("Publisher", a.Publisher, l.Publisher, v => a.Publisher = v);
        return changes;
    }
}

public class SandboxStoppedException(string message) : Exception(message);
