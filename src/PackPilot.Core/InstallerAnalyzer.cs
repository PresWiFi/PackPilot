using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PackPilot.Core;

/// <summary>
/// Works out what kind of installer a file is and fills in sensible defaults: silent switches,
/// uninstall approach and detection rule. Static only: it never runs the installer.
/// Engine signatures are the ones SwitchHunt uses.
/// </summary>
public static class InstallerAnalyzer
{
    const int HeadBytes = 24 * 1024 * 1024;

    record Engine(string Id, string Label, string Confidence, string InstallArgs, string UninstallSilentArgs, string Notes);

    public static AppDefinition Analyze(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException(path);
        var ext = fi.Extension.ToLowerInvariant();
        var app = new AppDefinition
        {
            SourcePath = fi.FullName,
            SourceSize = fi.Length,
            SourceSha256 = Util.Sha256File(path),
        };
        app.Signature = ReadSignature(path);

        switch (ext)
        {
            case ".msi": AnalyzeMsi(app); break;
            case ".exe": AnalyzeExe(app); break;
            case ".ps1": AnalyzePs1(app); break;
            default: throw new NotSupportedException($"{fi.Name}: PackPilot handles .exe, .msi and .ps1 files.");
        }
        if (string.IsNullOrWhiteSpace(app.Name)) app.Name = Path.GetFileNameWithoutExtension(path);
        // No product name in the EXE: guess the Programs and Features name from the file name
        // ("npp.8.9.Installer.x64" -> "npp*"), so the package can be built and the Sandbox test can learn the real one.
        if (app.Kind == InstallerKind.Exe && string.IsNullOrWhiteSpace(app.DisplayNamePattern))
        {
            var stem = System.Text.RegularExpressions.Regex.Replace(app.Name,
                @"[\s._-]*(v?\d[\d.]*|x64|x86|amd64|arm64|win(32|64)|setup|installer|install)\b.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            app.DisplayNamePattern = (stem.Length >= 2 ? stem : app.Name) + "*";
            app.AnalyzerNotes += " The file has no product name, so the detection name is a guess from the file name.";
        }
        if (app.Kind != InstallerKind.PowerShell && !app.Signature.StartsWith("Valid") && !app.Signature.StartsWith("not checked"))
            app.AnalyzerNotes += $" Warning: the file is not validly signed ({app.Signature}). Devices (and this PC's Sandbox) that enforce Smart App Control or App Control for Business will block it.";
        if (string.IsNullOrWhiteSpace(app.Version)) app.Version = "1.0.0";
        if (string.IsNullOrWhiteSpace(app.Publisher)) app.Publisher = "Unknown publisher";
        if (string.IsNullOrWhiteSpace(app.Description)) app.Description = $"{app.Name} {app.Version}, packaged with PackPilot.";
        return app;
    }

    // ── MSI ────────────────────────────────────────────────────────────────────
    static void AnalyzeMsi(AppDefinition app)
    {
        app.Kind = InstallerKind.Msi;
        app.Engine = "msi";
        app.EngineLabel = "Windows Installer (MSI)";
        app.Confidence = "high";
        app.InstallArgs = "/qn /norestart";
        app.Detection = DetectionMethod.MsiProductCode;
        try
        {
            var p = Msi.ReadProperties(app.SourcePath, "ProductCode", "ProductName", "ProductVersion", "Manufacturer", "UpgradeCode", "ALLUSERS");
            app.ProductCode = p.GetValueOrDefault("ProductCode", "");
            app.Name = p.GetValueOrDefault("ProductName", "");
            app.Version = p.GetValueOrDefault("ProductVersion", "");
            app.Publisher = p.GetValueOrDefault("Manufacturer", "");
            app.UpgradeCode = p.GetValueOrDefault("UpgradeCode", "");
            app.MsiAllUsers = p.GetValueOrDefault("ALLUSERS", "");
            var template = Msi.ReadTemplate(app.SourcePath).ToLowerInvariant();
            if (template.StartsWith("x64") || template.StartsWith("intel64") || template.StartsWith("amd64"))
            { app.Architecture = "x64"; app.ArchitectureNote = "64-bit MSI: it will not install on 32-bit Windows."; }
            else if (template.StartsWith("arm64")) { app.Architecture = "arm64"; app.ArchitectureNote = "ARM64 MSI."; }
            else { app.Architecture = "x86"; app.ArchitectureNote = "32-bit MSI (installs to Program Files (x86)); some are wrappers around a 64-bit setup."; }
            app.AnalyzerNotes = "Silent switches for MSI are always /qn. Install.ps1 also writes a verbose MSI log to the Intune logs folder.";
            if (app.MsiAllUsers == "2" || app.MsiAllUsers == "")
                app.AnalyzerNotes += $" ALLUSERS is {(app.MsiAllUsers == "" ? "not set" : "2")}: the MSI may install per-user. Installing as SYSTEM usually makes it per-machine, but test it.";
        }
        catch (Exception ex)
        {
            app.Confidence = "medium";
            app.AnalyzerNotes = $"Could not read the MSI's properties ({ex.Message}). Fill in the product code by hand, or run the Sandbox test to learn it.";
            app.Detection = DetectionMethod.Registry;
        }
        if (string.IsNullOrEmpty(app.ProductCode)) app.Detection = DetectionMethod.Registry;
        app.DisplayNamePattern = Util.NamePattern(app.Name, app.Version);
        app.DetectMinVersion = Util.CleanVersion(app.Version);
    }

    // ── EXE ────────────────────────────────────────────────────────────────────
    static void AnalyzeExe(AppDefinition app)
    {
        app.Kind = InstallerKind.Exe;
        var bytes = ReadHead(app.SourcePath);
        var e = DetectEngine(bytes);
        app.Engine = e.Id; app.EngineLabel = e.Label; app.Confidence = e.Confidence;
        app.InstallArgs = e.InstallArgs; app.UninstallSilentArgs = e.UninstallSilentArgs; app.AnalyzerNotes = e.Notes;

        try
        {
            var v = FileVersionInfo.GetVersionInfo(app.SourcePath);
            app.Name = (v.ProductName ?? "").Trim();
            app.Publisher = (v.CompanyName ?? "").Trim();
            app.Version = Util.CleanVersion(v.ProductVersion) is { Length: > 0 } pv ? pv : Util.CleanVersion(v.FileVersion);
            app.Description = (v.FileDescription ?? "").Trim();
            // Setup programs often describe themselves ("Setup", "Installer"), which makes a poor description.
            if (app.Description.Contains("setup", StringComparison.OrdinalIgnoreCase) || app.Description.Contains("install", StringComparison.OrdinalIgnoreCase))
                app.Description = "";
        }
        catch { /* no version resource */ }

        ushort machine = PeMachine(bytes);
        var nameHint = (app.SetupFileName + " " + app.Name).ToLowerInvariant();
        if (machine == 0x8664) { app.Architecture = "x64"; app.ArchitectureNote = "64-bit setup program: it only runs on 64-bit Windows."; }
        else if (machine == 0xAA64) { app.Architecture = "arm64"; app.ArchitectureNote = "ARM64 setup program."; }
        else if (nameHint.Contains("x64") || nameHint.Contains("win64") || nameHint.Contains("amd64") || nameHint.Contains("64-bit"))
        { app.Architecture = "x64"; app.ArchitectureNote = "32-bit setup program, but the name says x64. Most setup engines are 32-bit whatever they install."; }
        else { app.Architecture = "unknown"; app.ArchitectureNote = "The setup program is 32-bit, which says nothing certain about the app. The Sandbox test shows where it lands."; }

        // Before the Sandbox test the only clue to the Programs and Features name is the product name.
        app.Detection = DetectionMethod.Registry;
        app.DisplayNamePattern = string.IsNullOrWhiteSpace(app.Name) ? "" : app.Name + "*";
        app.DetectMinVersion = "";
        app.AnalyzerNotes += " Detection and uninstall are a first guess from the file's product name: run the Sandbox test, then \"Use what the test learned\".";
    }

    static Engine DetectEngine(byte[] b)
    {
        bool Has(string s) => IndexOf(b, Encoding.ASCII.GetBytes(s)) >= 0;
        bool HasW(string s) => IndexOf(b, Encoding.Unicode.GetBytes(s)) >= 0;

        if (PeHasSection(b, ".wixburn") || Has(".wixburn"))
            return new("wix-burn", "WiX Burn bundle", "high", "/install /quiet /norestart", "/quiet /norestart",
                "Burn bundles use /quiet. The uninstall entry usually has a QuietUninstallString.");
        if (Has("Inno Setup Setup Data") || Has("Inno Setup"))
            return new("inno", "Inno Setup", "high", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                "Inno Setup switches are built into the engine. Add /ALLUSERS if the installer asks per-user vs all users.");
        if (Has("NullsoftInst"))
            return new("nsis", "NSIS (Nullsoft)", "medium", "/S", "/S",
                "NSIS uses /S (capital, case-sensitive). Silent support depends on the author; the Sandbox test confirms it.");
        if (Has("InstallShield"))
            return new("installshield", "InstallShield", "medium", "/s /v\"/qn REBOOT=ReallySuppress\"", "",
                "Basic MSI InstallShield projects take /s /v\"/qn\". InstallScript projects need a recorded response file (setup.exe /r, then /s /f1\"setup.iss\").");
        if (Has("Advanced Installer") || HasW("Advanced Installer"))
            return new("advanced-installer", "Advanced Installer", "high", "/exenoui /qn /norestart", "/qn",
                "Advanced Installer bootstrapper: /exenoui hides its UI and /qn is passed to the MSI inside.");
        if (Has("Squirrel"))
            return new("squirrel", "Squirrel.Windows", "medium", "--silent", "--uninstall -s",
                "Squirrel installs per-user into %LocalAppData%. Installed as SYSTEM it lands in the system profile, so consider deploying it in the user context or using the vendor's machine-wide MSI.");
        if (Has("InstallAware"))
            return new("installaware", "InstallAware", "medium", "/s", "/s", "InstallAware uses /s.");
        if (Has("InstallBuilder") || Has("BitRock"))
            return new("installbuilder", "InstallBuilder", "medium", "--mode unattended", "--mode unattended", "InstallBuilder uses --mode unattended.");
        if (Has("WiseMain") || Has("Wise Installation"))
            return new("wise", "Wise Installation System", "low", "/s", "/s", "Wise uses /s. These installers are old; test carefully.");
        if (Has(";!@Install@!UTF-8!"))
            return new("sfx-7z", "7-Zip self-extractor", "low", "", "",
                "A 7-Zip self-extracting archive with a run-after-extract config. It has no universal silent switch: extract it with 7-Zip to find the setup program inside, and package that instead.");
        // A 7z payload without the SFX config block is a custom setup program that carries its files as a
        // 7z archive (7-Zip's own installer is one). These almost always take /S.
        if (HasBytes(b, new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }))
            return new("custom-7z", "Custom setup (7-Zip payload)", "low", "/S", "/S",
                "A custom setup program carrying a 7-Zip archive. /S is the usual silent switch for these (7-Zip's own installer uses it); the Sandbox test confirms it.");
        return new("unknown-exe", "Unknown EXE installer", "low", "/S", "",
            "The engine is not recognised. /S is a guess: common alternatives are /silent, /quiet, /qn, /verysilent. Check the vendor's docs, then confirm with the Sandbox test.");
    }

    static bool HasBytes(byte[] b, byte[] sig) => IndexOf(b, sig) >= 0;

    static byte[] ReadHead(string path)
    {
        using var fs = File.OpenRead(path);
        var len = (int)Math.Min(fs.Length, HeadBytes);
        var buf = new byte[len];
        fs.ReadExactly(buf, 0, len);
        return buf;
    }

    static int IndexOf(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle);

    static ushort PeMachine(byte[] b)
    {
        try
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return 0;
            int pe = BitConverter.ToInt32(b, 0x3c);
            if (pe <= 0 || pe + 6 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550) return 0;
            return BitConverter.ToUInt16(b, pe + 4);
        }
        catch { return 0; }
    }

    static bool PeHasSection(byte[] b, string name)
    {
        try
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return false;
            int pe = BitConverter.ToInt32(b, 0x3c);
            if (pe <= 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550) return false;
            int n = BitConverter.ToUInt16(b, pe + 6), opt = BitConverter.ToUInt16(b, pe + 20);
            int start = pe + 24 + opt;
            for (int i = 0; i < Math.Min(n, 96); i++)
            {
                int off = start + i * 40;
                if (off + 8 > b.Length) return false;
                var s = Encoding.ASCII.GetString(b, off, 8).TrimEnd('\0');
                if (s == name) return true;
            }
        }
        catch { }
        return false;
    }

    // ── PS1 ────────────────────────────────────────────────────────────────────
    static void AnalyzePs1(AppDefinition app)
    {
        app.Kind = InstallerKind.PowerShell;
        app.Engine = "powershell";
        app.EngineLabel = "PowerShell script";
        app.Confidence = "high";
        app.InstallArgs = "";
        app.Detection = DetectionMethod.Marker;
        app.Architecture = "unknown";
        app.ArchitectureNote = "Install.ps1 always runs the script in 64-bit PowerShell, even though the Intune agent starts 32-bit.";
        app.AnalyzerNotes = "Scripts often leave nothing in Programs and Features, so detection defaults to a registry marker that Install.ps1 writes when the script exits 0. " +
                            "Pick an uninstall script (or command) if the app needs one; otherwise Uninstall.ps1 only removes the marker.";
        // A comment-based help .SYNOPSIS makes a better description than nothing.
        try
        {
            var text = File.ReadAllText(app.SourcePath);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\.SYNOPSIS\s*\r?\n\s*(.+)");
            if (m.Success) app.Description = m.Groups[1].Value.Trim();
            var dir = Path.GetDirectoryName(app.SourcePath)!;
            var guess = Directory.GetFiles(dir, "*.ps1").FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f, app.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (guess != null)
            {
                app.IncludeSourceFolder = true;
                app.UninstallFile = Path.GetFileName(guess);
                app.AnalyzerNotes += $" Found {Path.GetFileName(guess)} next to the script: it is used for uninstall, and the whole folder is included.";
            }
        }
        catch { }
    }

    // ── Signature (Get-AuthenticodeSignature checks the chain, unlike a bare certificate read) ──
    static string ReadSignature(string path)
    {
        if (!OperatingSystem.IsWindows()) return "not checked (not running on Windows)";
        try
        {
            var cmd = "$s = Get-AuthenticodeSignature -LiteralPath $env:PP_FILE; " +
                      "[pscustomobject]@{ Status = \"$($s.Status)\"; Signer = \"$($s.SignerCertificate.Subject)\" } | ConvertTo-Json -Compress";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.Environment["PP_FILE"] = path;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return "not checked (timed out)"; }
            using var doc = JsonDocument.Parse(output);
            var status = doc.RootElement.GetProperty("Status").GetString();
            var signer = doc.RootElement.GetProperty("Signer").GetString();
            return string.IsNullOrWhiteSpace(signer) ? $"{status}" : $"{status}: {signer}";
        }
        catch (Exception ex) { return $"not checked ({ex.Message})"; }
    }
}

/// <summary>Reads MSI properties through msi.dll (always present on Windows).</summary>
static class Msi
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder? value, ref uint len);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetSummaryInformation(IntPtr db, string? path, uint updateCount, out IntPtr summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, out uint dataType, out int intValue, ref long fileTime, StringBuilder? value, ref uint len);

    public static Dictionary<string, string> ReadProperties(string path, params string[] names)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MSI reading needs Windows");
        var result = new Dictionary<string, string>();
        Check(MsiOpenDatabase(path, IntPtr.Zero, out var db), "open the MSI");
        try
        {
            Check(MsiDatabaseOpenView(db, "SELECT `Property`, `Value` FROM `Property`", out var view), "query the Property table");
            try
            {
                Check(MsiViewExecute(view, IntPtr.Zero), "run the query");
                while (MsiViewFetch(view, out var rec) == 0)
                {
                    try
                    {
                        var name = GetString(rec, 1);
                        if (names.Contains(name)) result[name] = GetString(rec, 2);
                    }
                    finally { MsiCloseHandle(rec); }
                }
            }
            finally { MsiCloseHandle(view); }
        }
        finally { MsiCloseHandle(db); }
        return result;
    }

    /// <summary>The Summary Information "Template" (platform;languages), e.g. "x64;1033".</summary>
    public static string ReadTemplate(string path)
    {
        if (MsiGetSummaryInformation(IntPtr.Zero, path, 0, out var sum) != 0) return "";
        try
        {
            uint len = 256; long ft = 0;
            var sb = new StringBuilder((int)len);
            return MsiSummaryInfoGetProperty(sum, 7, out _, out _, ref ft, sb, ref len) == 0 ? sb.ToString() : "";
        }
        finally { MsiCloseHandle(sum); }
    }

    static string GetString(IntPtr rec, uint field)
    {
        uint len = 0;
        MsiRecordGetString(rec, field, new StringBuilder(1), ref len);
        len++;
        var sb = new StringBuilder((int)len);
        MsiRecordGetString(rec, field, sb, ref len);
        return sb.ToString();
    }

    static void Check(uint rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"could not {what} (error {rc})");
    }
}
