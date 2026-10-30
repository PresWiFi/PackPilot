// Self-checks that run anywhere (no Windows needed): package round trip, format details,
// script generation and the "learned" logic. Run: dotnet run --project tests/PackPilot.Tests
using System.IO.Compression;
using System.Security.Cryptography;
using PackPilot.Core;
using PackPilot.Core.Intune;
using System.Net;
using System.Text.Json.Nodes;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

var root = Path.Combine(Path.GetTempPath(), "packpilot-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
try
{
    // A fake installer with random (incompressible) bytes, big enough to need several cipher blocks.
    var srcDir = Path.Combine(root, "src");
    Directory.CreateDirectory(srcDir);
    var setup = Path.Combine(srcDir, "setup.exe");
    File.WriteAllBytes(setup, RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 7));
    File.WriteAllText(Path.Combine(srcDir, "Install.ps1"), "user's own script with a clashing name");

    // 1. Raw IntuneWin round trip
    var pkgSrc = Path.Combine(root, "pkgsrc");
    Directory.CreateDirectory(Path.Combine(pkgSrc, "sub"));
    File.Copy(setup, Path.Combine(pkgSrc, "setup.exe"));
    File.WriteAllText(Path.Combine(pkgSrc, "sub", "a.txt"), "hello");
    var outFile = Path.Combine(root, "out", "test.intunewin");
    var info = IntuneWin.Build(pkgSrc, "setup.exe", outFile, "Test & <App>");
    Check(info.Verified, "package verifies after build");
    Check(info.Files.Count == 2, $"payload has 2 files ({string.Join(", ", info.Files)})");

    using (var z = ZipFile.OpenRead(outFile))
    {
        Check(z.Entries.Select(e => e.FullName).SequenceEqual(new[] { "IntuneWinPackage/Metadata/Detection.xml", "IntuneWinPackage/Contents/IntunePackage.intunewin" }),
            "outer zip has exactly Detection.xml then Contents");
        var xml = new StreamReader(z.Entries[0].Open()).ReadToEnd();
        Check(xml.Contains("<Name>Test &amp; &lt;App&gt;</Name>"), "app name is XML-escaped");
        Check(xml.Contains("<SetupFile>setup.exe</SetupFile>") && xml.Contains("ProfileVersion1") && xml.Contains("<FileDigestAlgorithm>SHA256"), "Detection.xml fields present");
        Check(z.Entries[1].Length == z.Entries[1].CompressedLength, "encrypted entry stored uncompressed");
    }
    // Compression method bytes in the local header (offset 8): 0 = stored
    var bytes = File.ReadAllBytes(outFile);
    Check(bytes[8] == 0 && bytes[9] == 0, "outer zip uses STORE (method 0)");

    var extract = Path.Combine(root, "extract");
    IntuneWin.Verify(outFile, "setup.exe", extractTo: extract);
    Check(File.ReadAllBytes(Path.Combine(extract, "setup.exe")).SequenceEqual(File.ReadAllBytes(setup)), "decrypted setup.exe is byte-identical");
    Check(File.ReadAllText(Path.Combine(extract, "sub", "a.txt")) == "hello", "subfolder file survives");

    // Tamper: flip one byte of ciphertext inside the Contents entry -> HMAC must fail
    var tampered = Path.Combine(root, "tampered.intunewin");
    File.Copy(outFile, tampered);
    using (var fs = new FileStream(tampered, FileMode.Open))
    {
        fs.Position = fs.Length - 2000; int b = fs.ReadByte(); fs.Position--; fs.WriteByte((byte)(b ^ 0xff));
    }
    bool threw = false;
    try { IntuneWin.Verify(tampered); } catch (InvalidDataException ex) { threw = ex.Message.Contains("HMAC"); }
    Check(threw, "tampered package is rejected by HMAC check");

    // 2. Full project build (PackageBuilder) with a clashing Install.ps1 in the source folder
    var app = new AppDefinition
    {
        SourcePath = setup, Kind = InstallerKind.Exe, Name = "Contoso Tool", Publisher = "Contoso", Version = "2.4.1",
        Description = "test", InstallArgs = "/S", UninstallSilentArgs = "/S", Detection = DetectionMethod.Registry,
        DisplayNamePattern = "Contoso Tool*", IncludeSourceFolder = true, EngineLabel = "NSIS", SourceSize = new FileInfo(setup).Length,
    };
    var proj = new Project { App = app, OutputRoot = Path.Combine(root, "output") };
    PackageBuilder.Build(proj);
    Check(app.PayloadFolder == "Files", "clashing Install.ps1 moves payload to Files\\");
    Check(proj.Package!.Files.Any(f => f.StartsWith("Install.ps1")) && proj.Package.Files.Any(f => f.StartsWith("Files/setup.exe")), "package holds wrapper + Files/setup.exe");
    foreach (var f in new[] { "Deploy/Detect.ps1", "Deploy/Deploy-ToIntune.ps1", "Deploy/DEPLOYMENT.md", "Deploy/intune-app.json", "Report.html", "project.json" })
        Check(File.Exists(Path.Combine(proj.Folder, f)), $"wrote {f}");
    var installPs1 = File.ReadAllText(Path.Combine(proj.SourceDir, "Install.ps1"));
    Check(installPs1.Contains("$PayloadFolder = 'Files'") && installPs1.Contains("$InstallArgs = '/S'"), "Install.ps1 settings block");
    Check(File.ReadAllBytes(Path.Combine(proj.SourceDir, "Install.ps1")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "scripts have a UTF-8 BOM for PowerShell 5.1");
    Check(!proj.PackageStale, "package not stale right after build");
    app.InstallArgs = "/S /D=C:\\x";
    Check(proj.PackageStale, "package stale after a settings change");

    Check(app.RawInstallCommand.StartsWith("\"Files\\setup.exe\""), $"raw command points into Files\\ ({app.RawInstallCommand})");
    var deploy = File.ReadAllText(Path.Combine(proj.DeployDir, "Deploy-ToIntune.ps1"));
    Check(deploy.Contains("-Architecture 'x64x86'"), "Deploy script uses a valid IntuneWin32App architecture value");

    // Reload
    var re = Project.Load(Path.Combine(proj.Folder, "project.json"));
    Check(re.App.Name == "Contoso Tool" && re.Package?.Verified == true && re.Log.Count > 3, "project.json round trip");

    // Sandbox kit
    var runId = Sandbox.Prepare(proj);
    var wsb = File.ReadAllText(Sandbox.WsbPath(runId));
    Check(wsb.Contains("<ReadOnly>true</ReadOnly>") && wsb.Contains("Test-InSandbox.ps1"), ".wsb maps the package read-only and runs the test");
    Check(wsb.Contains("<vGPU>Disable</vGPU>") && !wsb.Contains("MemoryInMB"), ".wsb turns vGPU off and leaves memory at the default");
    Check(wsb.Contains(Sandbox.StageDir(runId)) && !wsb.Contains(proj.Folder), ".wsb maps the local stage, not the project folder");
    var stageTest = Path.Combine(Sandbox.StageDir(runId), "Test");
    Check(File.Exists(Path.Combine(stageTest, "Detect.ps1")) && File.Exists(Path.Combine(stageTest, "test-config.json")) && File.Exists(Path.Combine(stageTest, "Test-InSandbox.ps1")), "test kit staged");
    Check(File.Exists(Path.Combine(Sandbox.StageDir(runId), "Package", Path.GetFileName(proj.PackageFile))), "package staged");
    Directory.Delete(Sandbox.StageDir(runId), true);

    // Session mode: a queued job has the same kit layout plus job.ready, and the agent script parses.
    var jobId = Sandbox.QueueJob(proj);
    var jobDir = Path.Combine(Sandbox.SessionRoot, "Jobs", jobId);
    Check(File.Exists(Path.Combine(jobDir, "job.ready")) && File.Exists(Path.Combine(jobDir, "Test", "Test-InSandbox.ps1"))
          && File.Exists(Path.Combine(jobDir, "Package", Path.GetFileName(proj.PackageFile))), "session job queued with package, kit and job.ready");
    var jobId2 = Sandbox.QueueJob(proj);
    Check(jobId2 != jobId, $"run ids stay unique within the same second ({jobId}, {jobId2})");
    Directory.Delete(Path.Combine(Sandbox.SessionRoot, "Jobs", jobId2), true);
    Sandbox.SessionDirty = true; Check(Sandbox.SessionDirty, "session dirty flag set");
    Sandbox.SessionDirty = false; Check(!Sandbox.SessionDirty, "session dirty flag cleared");
    Check(!Sandbox.LeftClean(new SandboxResult { Steps = { new SandboxStep { Step = "Uninstall", Ok = true, Detail = "skipped" } } }), "a skipped uninstall leaves the session dirty");
    Check(Sandbox.LeftClean(new SandboxResult { Steps = { new SandboxStep { Step = "Detect after uninstall", Ok = true } } }), "a clean uninstall leaves the session clean");
    Directory.Delete(jobDir, true);

    // 3. Learned values
    var a2 = new AppDefinition { Name = "7-Zip", Version = "1.0.0", Publisher = "Unknown publisher", Detection = DetectionMethod.Registry, DisplayNamePattern = "7-Zip*" };
    var ch = Sandbox.ApplyLearned(a2, new LearnedValues { DisplayName = "7-Zip 24.08 (x64)", DisplayVersion = "24.08", Publisher = "Igor Pavlov" });
    Check(a2.DisplayNamePattern == "7-Zip*" && a2.DetectMinVersion == "24.08" && a2.Version == "24.08" && a2.Publisher == "Igor Pavlov", $"learned registry values ({string.Join("; ", ch)})");
    var a3 = new AppDefinition { Name = "X", Version = "3.0", Detection = DetectionMethod.Registry, DisplayNamePattern = "X*" };
    Sandbox.ApplyLearned(a3, new LearnedValues { DisplayName = "X App", DisplayVersion = "3.0.1", ProductCode = "{11111111-2222-3333-4444-555555555555}" });
    Check(a3.Detection == DetectionMethod.MsiProductCode && a3.ProductCode.StartsWith("{1111"), "learned MSI product code switches detection");

    // Failure hints for the 7-Zip case: quick exit code 1 from a custom-7z installer
    var zapp = new AppDefinition { Kind = InstallerKind.Exe, Engine = "custom-7z", InstallArgs = "/S" };
    var zr = new SandboxResult { Verdict = "FAIL", InstallExitCode = 1, InstallSeconds = 6, InstallOutput = "2026-10-29 20:42:50  Exit code 1 (failure)",
        Steps = { new SandboxStep { Step = "Install", Ok = false, Detail = "exit code 1 (failure) after 6 s" }, new SandboxStep { Step = "Detect after install", Ok = false } } };
    var zh = Sandbox.FailureHints(zapp, zr);
    Check(zh.Any(h => h.Contains("silent switch is wrong")) && zh.Any(h => h.Contains("Others to try") && h.Contains("/VERYSILENT") && !h.Contains("type: /S   ")), $"failure hints suggest other switches ({zh.Count} hints)");
    var html = Report.Build(new Project { App = zapp, Tests = { zr } });
    Check(html.Contains("What went wrong") && html.Contains("Install.ps1 output"), "report shows hints and install output for a failed test");
    Check(Sandbox.FailureHints(zapp, new SandboxResult { Verdict = "PASS", Steps = { new SandboxStep { Step = "Install", Ok = true } } }).Count == 0, "no hints for a pass");

    var blocked = new SandboxResult { Verdict = "FAIL", InstallExitCode = 1, InstallSeconds = 1,
        InstallOutput = "Could not run the installer: This command cannot be run due to the error: An Application Control policy has blocked this file.",
        Steps = { new SandboxStep { Step = "Install", Ok = false, Detail = "exit code 1 (failure) after 1 s" } } };
    var bh = Sandbox.FailureHints(zapp, blocked);
    Check(bh.Count == 2 && bh[0].Contains("App Control blocked") && !bh.Any(h => h.Contains("Others to try")), "App Control block explained, no pointless switch suggestions");

    // 5. Intune upload against a fake Graph + blob server
    await FakeGraphUploadTest(proj, Check);

    // 4. Validation
    var bad = new AppDefinition { SourcePath = setup, Name = "A", Version = "1", Detection = DetectionMethod.Registry, DisplayNamePattern = "*" };
    Check(PackageBuilder.Validate(bad).Count == 1, "rejects a * display name");
    Check(Util.NamePattern("Notepad++ (64-bit x64)", "8.6.9") == "Notepad++ (64-bit x64)", "NamePattern leaves names without the version alone");
    Check(Util.CleanVersion("v1.2.3-beta") == "1.2.3", "CleanVersion");

    // Dump generated scripts for a PowerShell syntax check
    var dump = Environment.GetEnvironmentVariable("PACKPILOT_DUMP");
    if (!string.IsNullOrEmpty(dump))
    {
        Directory.CreateDirectory(dump);
        foreach (var kind in new[] { DetectionMethod.MsiProductCode, DetectionMethod.Registry, DetectionMethod.File, DetectionMethod.Marker })
        {
            var d = new AppDefinition { Name = "It's \"quoted\"", Version = "1.0", Detection = kind, ProductCode = "{11111111-2222-3333-4444-555555555555}", DisplayNamePattern = "A*", DetectFilePath = "%ProgramFiles%\\A\\a.exe", SourcePath = "x.msi", Kind = InstallerKind.Msi };
            Util.WritePs1(Path.Combine(dump, $"Detect-{kind}.ps1"), Scripts.Detect(d));
            Util.WritePs1(Path.Combine(dump, $"Install-{kind}.ps1"), Scripts.Install(d));
            Util.WritePs1(Path.Combine(dump, $"Uninstall-{kind}.ps1"), Scripts.Uninstall(d));
        }
        Util.WritePs1(Path.Combine(dump, "Test-InSandbox.ps1"), Scripts.SandboxTest);
        Util.WritePs1(Path.Combine(dump, "PackPilot-Agent.ps1"), Scripts.SandboxAgent);
        Util.WritePs1(Path.Combine(dump, "Deploy-ToIntune.ps1"), DeploymentKit.DeployScript(proj));
        File.Copy(proj.ReportPath, Path.Combine(dump, "Report.html"), true);
        File.Copy(Path.Combine(proj.DeployDir, "DEPLOYMENT.md"), Path.Combine(dump, "DEPLOYMENT.md"), true);
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;


static async Task FakeGraphUploadTest(Project proj, Action<bool, string> Check)
{
    int port = Random.Shared.Next(20000, 40000);
    var prefix = $"http://localhost:{port}/";
    using var listener = new HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();
    var calls = new List<string>();
    var blocks = new Dictionary<string, byte[]>();
    byte[]? blob = null;
    JsonNode? appBody = null, commitBody = null, assignBody = null, fileBody = null;
    string state = "azureStorageUriRequestPending";
    int polls = 0;

    var server = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            var req = ctx.Request; var path = req.Url!.AbsolutePath; var q = req.Url.Query;
            bool isBlock = req.HttpMethod == "PUT" && q.Contains("comp=block&");
            string body = isBlock ? "" : await new StreamReader(req.InputStream).ReadToEndAsync();
            calls.Add($"{req.HttpMethod} {path}{(q.Contains("comp=") ? "?" + q.Split("comp=")[1].Split('&')[0] : "")}");
            string reply = "{}"; int code = 200;
            if (req.HttpMethod == "PUT" && q.Contains("comp=block&"))
            {
                var id = Uri.UnescapeDataString(q.Split("blockid=")[1]);
                using var ms = new MemoryStream(); await req.InputStream.CopyToAsync(ms); blocks[id] = ms.ToArray(); code = 201; reply = "";
            }
            else if (req.HttpMethod == "PUT" && q.Contains("comp=blocklist"))
            {
                var ids = System.Text.RegularExpressions.Regex.Matches(body, "<Latest>(.*?)</Latest>").Select(m => m.Groups[1].Value);
                blob = ids.SelectMany(i => blocks[i]).ToArray(); code = 201; reply = "";
            }
            else if (req.HttpMethod == "GET" && path.EndsWith("/mobileApps")) reply = """{"value":[{"@odata.type":"#microsoft.graph.win32LobApp","id":"old1","displayName":"Contoso Tool","displayVersion":"2.4.0"},{"@odata.type":"#microsoft.graph.webApp","id":"web1","displayName":"Contoso Tool"}]}""";
            else if (req.HttpMethod == "POST" && path.EndsWith("/mobileApps")) { appBody = JsonNode.Parse(body); reply = """{"id":"app1"}"""; code = 201; }
            else if (req.HttpMethod == "POST" && path.EndsWith("/contentVersions")) { reply = """{"id":"1"}"""; code = 201; }
            else if (req.HttpMethod == "POST" && path.EndsWith("/files")) { fileBody = JsonNode.Parse(body); reply = """{"id":"f1"}"""; code = 201; }
            else if (req.HttpMethod == "GET" && path.EndsWith("/files/f1"))
            {
                if (++polls == 2 && state.EndsWith("Pending")) state = state.Replace("Pending", "Success");
                reply = $$"""{"uploadState":"{{state}}","azureStorageUri":"{{prefix}}blob?sig=abc"}""";
            }
            else if (req.HttpMethod == "POST" && path.EndsWith("/commit")) { commitBody = JsonNode.Parse(body); state = "commitFilePending"; polls = 0; }
            else if (req.HttpMethod == "PATCH") reply = "";
            else if (req.HttpMethod == "POST" && path.EndsWith("/assignments")) { assignBody = JsonNode.Parse(body); code = 201; }
            else { code = 404; reply = """{"error":{"message":"unexpected"}}"""; }
            ctx.Response.StatusCode = code;
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });

    var cloud = new Cloud("Test", "Test cloud", "http://login", prefix.TrimEnd('/'), "https://portal.test", "");
    var up = new IntuneUploader(cloud, _ => Task.FromResult("token"));
    var found = await up.FindAppsAsync("Contoso Tool", CancellationToken.None);
    Check(found.Count == 1 && found[0].Id == "old1", "existing-app lookup keeps only Win32 apps");

    var logs = new List<string>();
    IntuneUploader.ChunkSize = 1024 * 1024;   // several blocks for a 3 MB package
    var id = await up.UploadAsync(proj, new UploadOptions { Intent = AssignIntent.Required, Target = AssignTarget.Group, GroupId = "g1", GroupName = "Pilot" },
        logs.Add, _ => { }, CancellationToken.None);
    listener.Stop();

    Check(id == "app1", "upload returns the new app id");
    var pkg = IntuneUploader.ReadPackage(proj.PackageFile);
    byte[] expected;
    using (var z = System.IO.Compression.ZipFile.OpenRead(proj.PackageFile))
    using (var ms = new MemoryStream()) { z.GetEntry("IntuneWinPackage/Contents/IntunePackage.intunewin")!.Open().CopyTo(ms); expected = ms.ToArray(); }
    Check(blob != null && blob.SequenceEqual(expected), $"uploaded blob is byte-identical to the encrypted content ({blob?.Length} bytes, {blocks.Count} block(s))");
    Check(blocks.Count == 4, "a 3 MB package goes up in 4 blocks of 1 MB");
    Check(fileBody?["size"]?.GetValue<long>() == pkg.UnencryptedSize && fileBody?["sizeEncrypted"]?.GetValue<long>() == expected.Length, "file entry sizes match Detection.xml and the encrypted content");
    Check(commitBody?["fileEncryptionInfo"]?["mac"]?.GetValue<string>() == pkg.EncryptionInfo["mac"]!.GetValue<string>() &&
          commitBody?["fileEncryptionInfo"]?["profileIdentifier"]?.GetValue<string>() == "ProfileVersion1", "commit sends the package's encryption info");
    Check(appBody?["@odata.type"]?.GetValue<string>() == "#microsoft.graph.win32LobApp" && appBody?["setupFilePath"]?.GetValue<string>() == "Install.ps1"
          && appBody?["installExperience"]?["runAsAccount"]?.GetValue<string>() == "system", "app body: win32LobApp, Install.ps1, runs as system");
    var rule = appBody?["detectionRules"]?[0];
    Check(rule?["@odata.type"]?.GetValue<string>() == "#microsoft.graph.win32LobAppPowerShellScriptDetection" && (rule?["scriptContent"]?.GetValue<string>().Length ?? 0) > 100,
          "registry-name detection is uploaded as the tested Detect.ps1");
    Check(assignBody?["intent"]?.GetValue<string>() == "required" && assignBody?["target"]?["groupId"]?.GetValue<string>() == "g1", "group assignment sent");
    var order = string.Join(" > ", calls.Where(c => !c.StartsWith("GET")).Select(c => c.Split('/').Last()));
    Check(order == $"mobileApps > contentVersions > files > {string.Join(" > ", Enumerable.Repeat("blob?block", blocks.Count))} > blob?blocklist > commit > app1 > assignments", $"request order ({order})");

    // Each detection method maps to the right Graph rule type.
    var a = proj.App; var keep = (a.Detection, a.ProductCode, a.DetectFilePath, a.DetectMinVersion);
    string RuleType() => IntuneUploader.BuildAppBody(proj)["detectionRules"]![0]!["@odata.type"]!.GetValue<string>();
    a.Detection = DetectionMethod.MsiProductCode; a.ProductCode = "{11111111-2222-3333-4444-555555555555}"; a.DetectMinVersion = "2.0";
    var msi = IntuneUploader.BuildAppBody(proj)["detectionRules"]![0]!;
    Check(RuleType().EndsWith("ProductCodeDetection") && msi["productVersionOperator"]!.GetValue<string>() == "greaterThanOrEqual", "MSI rule with version check");
    a.Detection = DetectionMethod.File; a.DetectFilePath = @"%ProgramFiles%\Contoso\tool.exe"; a.DetectMinVersion = "";
    var file = IntuneUploader.BuildAppBody(proj)["detectionRules"]![0]!;
    Check(RuleType().EndsWith("FileSystemDetection") && file["path"]!.GetValue<string>() == @"%ProgramFiles%\Contoso" && file["detectionType"]!.GetValue<string>() == "exists", "file rule");
    a.Detection = DetectionMethod.Marker;
    Check(RuleType().EndsWith("RegistryDetection"), "marker rule is a registry rule");
    (a.Detection, a.ProductCode, a.DetectFilePath, a.DetectMinVersion) = keep;
    Check(Cloud.ById("USGovHigh").Scopes[0] == "https://graph.microsoft.us/DeviceManagementApps.ReadWrite.All" && Cloud.ById("USGovDoD").GraphHost == "https://dod-graph.microsoft.us", "GCC High and DoD endpoints");
}
