using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PackPilot.Core;

/// <summary>Report.html: a self-contained record of what was packaged, how it was tested, and how to deploy it.</summary>
public static class Report
{
    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>`code` spans and **bold** from the Markdown-ish guide lines.</summary>
    static string Md(string s)
    {
        var h = H(s);
        h = Regex.Replace(h, "`([^`]+)`", "<code>$1</code>");
        h = Regex.Replace(h, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        return h.TrimStart('-', ' ');
    }

    public static void Write(Project p) => File.WriteAllText(p.ReportPath, Build(p), new UTF8Encoding(false));

    public static string Build(Project p)
    {
        var a = p.App;
        var t = p.LastTest;
        var sb = new StringBuilder();
        void A(string s) => sb.Append(s).Append('\n');

        string verdictClass = t == null ? "none" : t.Verdict == "PASS" ? "pass" : t.Verdict.StartsWith("PASS") ? "warn" : "fail";
        string verdictText = t == null ? "Not tested" : t.Verdict;

        A("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        A($"<title>{H(a.Name)} {H(a.Version)} - PackPilot report</title>");
        A("<style>" + Css + "</style></head><body><main>");

        A("<header>");
        A($"<div><p class=\"eyebrow\">PackPilot report</p><h1>{H(a.Name)} <span class=\"ver\">{H(a.Version)}</span></h1>");
        A($"<p class=\"muted\">{H(a.Publisher)} &middot; {H(a.EngineLabel)} &middot; generated {DateTime.Now:yyyy-MM-dd HH:mm}</p></div>");
        A($"<div class=\"badge {verdictClass}\">{H(verdictText)}</div>");
        A("</header>");

        // Summary cards
        A("<section class=\"cards\">");
        Card("Source", $"{H(a.SetupFileName)}<br><span class=\"muted\">{Util.FormatSize(a.SourceSize)} &middot; {H(a.Signature)}</span>");
        Card("Package", p.Package == null ? "<span class=\"muted\">Not built</span>"
            : $"{H(Path.GetFileName(p.PackageFile))}<br><span class=\"muted\">{Util.FormatSize(p.Package.PackageSize)} &middot; {(p.Package.Verified ? "verified" : "NOT verified")}{(p.PackageStale ? " &middot; <b>settings changed since build</b>" : "")}</span>");
        Card("Sandbox test", t == null ? "<span class=\"muted\">Not run</span>"
            : $"{H(t.Verdict)}<br><span class=\"muted\">{H(t.RunAt)} &middot; install exit {t.InstallExitCode} in {t.InstallSeconds}s</span>");
        A("</section>");
        void Card(string title, string body) => A($"<div class=\"card\"><h3>{title}</h3><p>{body}</p></div>");

        // Intune settings
        A("<section><h2>Intune settings</h2><table>");
        Row("Name", a.Name); Row("Description", a.Description); Row("Publisher", a.Publisher); Row("App version", a.Version);
        Row("Install command", AppDefinition.InstallCommand, copy: true);
        Row("Uninstall command", AppDefinition.UninstallCommand, copy: true);
        Row("Install behavior", "System");
        Row("Device restart behavior", "Determine behavior based on return codes");
        Row("Return codes", string.Join(", ", DeploymentKit.ReturnCodes.Select(r => $"{r.Code} {r.Type}")));
        Row("Architecture", DeploymentKit.ArchText(a));
        Row("Detection", a.DetectionSummary);
        A("</table>");
        A("<h3>Detection rule in the admin center</h3><ul>");
        foreach (var s in DeploymentKit.DetectionSteps(a)) A($"<li>{Md(s)}</li>");
        A("</ul>");
        A("<h3>Upload with PowerShell</h3>");
        var cmd = $".\\Deploy-ToIntune.ps1 -TenantId <tenant>.onmicrosoft.com -ClientId <app id> -GroupId <group id> -Intent required";
        A($"<div class=\"code\"><pre>cd \"{H(p.DeployDir)}\"\n{H(cmd)}</pre><button class=\"copy\" data-copy=\"{H($"cd \"{p.DeployDir}\"\n{cmd}")}\">Copy</button></div>");
        A($"<p class=\"muted\">Full steps, pilot-device checks and log locations: <code>Deploy\\DEPLOYMENT.md</code>.</p>");
        A("</section>");
        void Row(string k, string v, bool copy = false) =>
            A($"<tr><th>{H(k)}</th><td><span class=\"{(copy ? "mono" : "")}\">{H(v)}</span>{(copy ? $" <button class=\"copy\" data-copy=\"{H(v)}\">Copy</button>" : "")}</td></tr>");

        // Sandbox
        A("<section><h2>Windows Sandbox test</h2>");
        if (t == null)
        {
            A("<p class=\"muted\">Not run yet. In PackPilot, click <b>Test in Sandbox</b>.</p>");
        }
        else
        {
            A($"<p>Run <code>{H(t.RunId)}</code> at {H(t.RunAt)}. Results and logs: <code>{H(t.ResultsFolder)}</code></p>");
            A(t.SessionRun > 1
                ? $"<p class=\"muted\">Reused Sandbox: this was test {t.SessionRun} in it. Earlier tests could have left files or settings behind; for a final sign-off, run once in a fresh Sandbox.</p>"
                : "<p class=\"muted\">Fresh Sandbox: nothing else had been installed in it.</p>");
            A("<table class=\"steps\"><tr><th>Step</th><th>Result</th><th>Detail</th></tr>");
            foreach (var s in t.Steps)
                A($"<tr><td>{H(s.Step)}</td><td><span class=\"pill {(s.Ok ? "pass" : "fail")}\">{(s.Ok ? "PASS" : "FAIL")}</span></td><td>{H(s.Detail)}</td></tr>");
            A("</table>");
            var hints = t.Verdict == "PASS" ? new List<string>() : Sandbox.FailureHints(a, t);
            if (hints.Count > 0)
            {
                A("<h3>What went wrong and what to try</h3><ul>");
                foreach (var hint in hints) A($"<li>{H(hint)}</li>");
                A("</ul>");
            }
            if (t.Verdict != "PASS" && !string.IsNullOrWhiteSpace(t.InstallOutput))
                A($"<h3>Install.ps1 output</h3><div class=\"code\"><pre>{H(t.InstallOutput.Trim())}</pre></div>");
            if (t.Verdict != "PASS" && !string.IsNullOrWhiteSpace(t.UninstallOutput) && t.Steps.Any(s => s.Step == "Uninstall" && !s.Ok))
                A($"<h3>Uninstall.ps1 output</h3><div class=\"code\"><pre>{H(t.UninstallOutput.Trim())}</pre></div>");
            if (t.Learned != null)
            {
                A("<h3>What the install added to Programs and Features</h3><table>");
                var l = t.Learned;
                foreach (var (k, v) in new[] {
                    ("Display name", l.DisplayName), ("Version", l.DisplayVersion), ("Publisher", l.Publisher), ("Product code", l.ProductCode),
                    ("Registry key", l.RegistryKey), ("Install location", l.InstallLocation), ("Main program", l.DetectFile),
                    ("Quiet uninstall", l.QuietUninstallString), ("Uninstall string", l.UninstallString) })
                    if (!string.IsNullOrWhiteSpace(v)) Row(k, v);
                A("</table>");
            }
            if (p.Tests.Count > 1)
            {
                A("<h3>Earlier runs</h3><ul>");
                foreach (var r in p.Tests.Take(p.Tests.Count - 1).Reverse()) A($"<li>{H(r.RunAt)}: {H(r.Verdict)}</li>");
                A("</ul>");
            }
        }
        A("</section>");

        // Intune uploads
        if (p.Uploads.Count > 0)
        {
            A("<section><h2>Uploaded to Intune</h2><table class=\"steps\"><tr><th>When</th><th>Tenant</th><th>App</th><th>Assignment</th></tr>");
            foreach (var u in Enumerable.Reverse(p.Uploads))
            {
                var app = u.PortalUrl != null ? $"<a href=\"{H(u.PortalUrl)}\">{H(u.AppId)}</a>" : H(u.AppId);
                var stale = p.Package != null && u.PackageSha256 != p.Package.PackageSha256 ? " <span class=\"muted\">(an older build)</span>" : "";
                A($"<tr><td>{u.At:yyyy-MM-dd HH:mm}</td><td>{H(u.Cloud)}: {H(u.Tenant)}<br><span class=\"muted\">{H(u.User)}</span></td>" +
                  $"<td>{app}{stale}<br><span class=\"muted\">{(u.UpdatedExisting ? "updated existing app" : "new app")}</span></td><td>{H(u.Assignment)}</td></tr>");
            }
            A("</table></section>");
        }

        // Package
        A("<section><h2>Package</h2>");
        if (p.Package == null) A("<p class=\"muted\">Not built.</p>");
        else
        {
            A("<table>");
            Row("File", p.PackageFile); Row("Size", Util.FormatSize(p.Package.PackageSize)); Row("SHA-256", p.Package.PackageSha256);
            Row("Content size", Util.FormatSize(p.Package.UnencryptedContentSize)); Row("Built", p.Package.BuiltAt.ToString("yyyy-MM-dd HH:mm:ss"));
            Row("Setup file", "Install.ps1"); Row("Encryption", "AES-256-CBC, HMAC-SHA256, keys in Detection.xml (IntuneWinAppUtil 1.8 format)");
            A("</table><h3>Verification</h3><ul class=\"checks\">");
            foreach (var c in p.Package.VerifyChecks) A($"<li>{H(c)}</li>");
            A("</ul><h3>Contents</h3><ul class=\"mono\">");
            foreach (var f in p.Package.Files) A($"<li>{H(f)}</li>");
            A("</ul>");
        }
        A("</section>");

        // Source analysis
        A("<section><h2>Source analysis</h2><table>");
        Row("File", a.SourcePath); Row("SHA-256", a.SourceSha256); Row("Size", Util.FormatSize(a.SourceSize));
        Row("Signature", a.Signature); Row("Installer type", $"{a.EngineLabel} (confidence: {a.Confidence})");
        Row("Architecture", $"{a.Architecture}: {a.ArchitectureNote}");
        if (!string.IsNullOrEmpty(a.ProductCode)) Row("Product code", a.ProductCode);
        if (!string.IsNullOrEmpty(a.UpgradeCode)) Row("Upgrade code", a.UpgradeCode);
        Row("Silent install", a.RawInstallCommand); Row("Silent uninstall", a.RawUninstallCommand);
        Row("Notes", a.AnalyzerNotes);
        A("</table></section>");

        // Timeline
        A("<section><h2>What was done</h2><ol class=\"log\">");
        foreach (var e in p.Log) A($"<li><time>{e.At:yyyy-MM-dd HH:mm:ss}</time> {H(e.Text)}</li>");
        A("</ol></section>");

        A("<footer class=\"muted\">PackPilot builds .intunewin packages without IntuneWinAppUtil and tests them in Windows Sandbox. " +
          "A Sandbox pass is strong evidence, not a guarantee: test on a pilot group before a broad rollout.</footer>");
        A("</main><script>" + Js + "</script></body></html>");
        return sb.ToString();
    }

    const string Css = """
        :root{--bg:#f6f7f9;--panel:#fff;--text:#1b1f24;--muted:#5d6670;--line:#dfe3e8;--accent:#2563eb;--pass:#15803d;--passbg:#dcfce7;--fail:#b91c1c;--failbg:#fee2e2;--warn:#a16207;--warnbg:#fef3c7;--code:#f1f3f5}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1115;--panel:#171a21;--text:#e6e8eb;--muted:#9aa3ad;--line:#2a2f38;--accent:#60a5fa;--pass:#4ade80;--passbg:#12301d;--fail:#f87171;--failbg:#3a1414;--warn:#facc15;--warnbg:#3a2f0b;--code:#1f232b}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1000px;margin:0 auto;padding:24px 16px 48px}
        header{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin-bottom:16px}
        h1{margin:0;font-size:28px}.ver{color:var(--muted);font-weight:400}h2{font-size:19px;margin:0 0 12px}h3{font-size:15px;margin:18px 0 8px}
        .eyebrow{margin:0;text-transform:uppercase;letter-spacing:.08em;font-size:12px;color:var(--accent);font-weight:600}
        .muted{color:var(--muted)}section{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:18px 20px;margin:14px 0}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px;background:none;border:0;padding:0}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}.card h3{margin:0 0 4px;color:var(--muted);font-size:13px;font-weight:600}.card p{margin:0;word-break:break-word}
        .badge{padding:8px 16px;border-radius:999px;font-weight:700}.pill{padding:2px 10px;border-radius:999px;font-size:12px;font-weight:700}
        .pass{background:var(--passbg);color:var(--pass)}.fail{background:var(--failbg);color:var(--fail)}.warn{background:var(--warnbg);color:var(--warn)}.none{background:var(--code);color:var(--muted)}
        table{width:100%;border-collapse:collapse}th,td{text-align:left;vertical-align:top;padding:7px 8px;border-top:1px solid var(--line)}
        th{width:200px;color:var(--muted);font-weight:600}.steps th{width:auto}td{word-break:break-word}
        code,.mono,pre{font-family:Consolas,"Cascadia Mono",monospace;font-size:13px}code{background:var(--code);padding:1px 5px;border-radius:4px}
        .code{position:relative;background:var(--code);border-radius:8px;padding:12px 14px}.code pre{margin:0;white-space:pre-wrap;word-break:break-all;padding-right:64px}.code .copy{position:absolute;top:10px;right:10px}
        .copy{font:12px "Segoe UI",sans-serif;border:1px solid var(--line);background:var(--panel);color:var(--text);border-radius:6px;padding:2px 8px;cursor:pointer}
        ul.checks li::marker{content:"\2713  ";color:var(--pass)}ol.log{padding-left:0;list-style:none;margin:0}ol.log li{padding:4px 0;border-top:1px solid var(--line)}
        time{color:var(--muted);font-family:Consolas,monospace;font-size:12px;margin-right:8px}footer{font-size:13px;margin-top:20px}
        @media (max-width:600px){th{width:120px}}
        """;

    const string Js = """
        document.querySelectorAll('button.copy').forEach(b=>b.addEventListener('click',()=>{navigator.clipboard.writeText(b.dataset.copy).then(()=>{const t=b.textContent;b.textContent='Copied';setTimeout(()=>b.textContent=t,1200)})}));
        """;
}
