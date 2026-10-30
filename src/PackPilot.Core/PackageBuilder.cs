namespace PackPilot.Core;

public static class PackageBuilder
{
    static readonly string[] WrapperNames = { "Install.ps1", "Uninstall.ps1" };

    /// <summary>Problems that would make the package or its detection rule wrong. Empty when ready to build.</summary>
    public static List<string> Validate(AppDefinition a)
    {
        var errors = new List<string>();
        if (!File.Exists(a.SourcePath)) errors.Add($"The installer file is missing: {a.SourcePath}");
        if (string.IsNullOrWhiteSpace(a.Name)) errors.Add("Enter an app name.");
        if (string.IsNullOrWhiteSpace(a.Version)) errors.Add("Enter a version.");
        switch (a.Detection)
        {
            case DetectionMethod.MsiProductCode when !IsGuid(a.ProductCode):
                errors.Add("MSI product code detection needs a product code like {12345678-ABCD-...}."); break;
            case DetectionMethod.Registry when string.IsNullOrWhiteSpace(a.DisplayNamePattern):
                errors.Add("Registry detection needs a display name (wildcards * allowed)."); break;
            case DetectionMethod.Registry when a.DisplayNamePattern.Trim() == "*":
                errors.Add("A display name of just * matches every app. Make it more specific."); break;
            case DetectionMethod.File when string.IsNullOrWhiteSpace(a.DetectFilePath):
                errors.Add("File detection needs a file path."); break;
        }
        if (!string.IsNullOrWhiteSpace(a.ProductCode) && !IsGuid(a.ProductCode))
            errors.Add("The product code is not a GUID in braces.");
        return errors;
    }

    static bool IsGuid(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", @"^\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$");

    public static void Build(Project p, CancellationToken ct = default)
    {
        var a = p.App;
        var errors = Validate(a);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        p.FolderName ??= $"{a.AppId}_{Util.SafeId(a.Version)}";
        p.Note($"Building {a.Name} {a.Version} in {p.Folder}");
        Directory.CreateDirectory(p.Folder);

        // 1. Stage the source folder.
        if (Directory.Exists(p.SourceDir)) Directory.Delete(p.SourceDir, true);
        Directory.CreateDirectory(p.SourceDir);
        var srcDir = Path.GetDirectoryName(a.SourcePath)!;
        var topNames = a.IncludeSourceFolder
            ? Directory.EnumerateFileSystemEntries(srcDir).Select(Path.GetFileName).ToList()
            : new List<string?> { a.SetupFileName };
        a.PayloadFolder = topNames.Any(n => WrapperNames.Contains(n, StringComparer.OrdinalIgnoreCase)) ? "Files" : "";
        var payloadDir = a.PayloadFolder == "" ? p.SourceDir : Path.Combine(p.SourceDir, a.PayloadFolder);
        Directory.CreateDirectory(payloadDir);
        if (a.PayloadFolder != "")
            p.Note($"A source file is named Install.ps1 or Uninstall.ps1, so the installer files go in a Files subfolder.");

        if (a.IncludeSourceFolder)
        {
            // Never copy our own output into the package if it sits inside the source folder.
            var exclude = Path.GetFullPath(p.OutputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int n = 0;
            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFullPath(file).StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(payloadDir, Path.GetRelativePath(srcDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
                n++;
            }
            p.Note($"Copied {n} file(s) from {srcDir} into the package source.");
        }
        else
        {
            File.Copy(a.SourcePath, Path.Combine(payloadDir, a.SetupFileName), true);
            p.Note($"Copied {a.SetupFileName} ({Util.FormatSize(a.SourceSize)}) into the package source.");
        }

        // 2. Wrapper scripts.
        Util.WritePs1(Path.Combine(p.SourceDir, "Install.ps1"), Scripts.Install(a));
        Util.WritePs1(Path.Combine(p.SourceDir, "Uninstall.ps1"), Scripts.Uninstall(a));
        p.Note("Wrote Install.ps1 and Uninstall.ps1 (silent run, 64-bit relaunch, logging, exit code passthrough).");

        // 3. The .intunewin.
        Directory.CreateDirectory(p.PackageDir);
        foreach (var old in Directory.GetFiles(p.PackageDir, "*.intunewin")) File.Delete(old);
        p.Package = IntuneWin.Build(p.SourceDir, "Install.ps1", p.PackageFile, a.Name, p.Note, ct);
        p.Note($"Built {Path.GetFileName(p.PackageFile)} ({Util.FormatSize(p.Package.PackageSize)}).");
        p.Note("Verified the package: " + string.Join("; ", p.Package.VerifyChecks) + ".");

        // 4. Deployment kit and report.
        DeploymentKit.Write(p);
        p.Note("Wrote the deployment kit (Detect.ps1, Deploy-ToIntune.ps1, DEPLOYMENT.md, intune-app.json).");
        p.BuiltFingerprint = p.Fingerprint();
        Finish(p);
    }

    /// <summary>Refreshes the report and saves the project. Call after anything that changes them.</summary>
    public static void Finish(Project p)
    {
        Report.Write(p);
        p.Save();
    }
}
