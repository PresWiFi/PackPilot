using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PackPilot.Core.Intune;

public enum AssignIntent { None, Required, Available, Uninstall }
public enum AssignTarget { Group, AllDevices, AllUsers }

public class UploadOptions
{
    /// <summary>Update this existing Win32 app (new content version) instead of creating one.</summary>
    public string? ExistingAppId { get; set; }
    public AssignIntent Intent { get; set; }
    public AssignTarget Target { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
}

/// <summary>A record of one upload, kept in project.json and shown in the report.</summary>
public class UploadRecord
{
    public DateTime At { get; set; }
    public string Cloud { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string User { get; set; } = "";
    public string AppId { get; set; } = "";
    public bool UpdatedExisting { get; set; }
    public string Assignment { get; set; } = "";
    public string? PortalUrl { get; set; }
    public string PackageSha256 { get; set; } = "";
}

public record ExistingApp(string Id, string DisplayName, string? DisplayVersion, DateTime? LastModified);
public record GroupInfo(string Id, string DisplayName, string? Description);

public class GraphException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Creates or updates a Win32 app in Intune through Microsoft Graph (beta, like the Intune admin center),
/// uploads the encrypted package to the Azure blob Intune hands out, commits it, and optionally assigns it.
/// Request shapes follow the IntuneWin32App module (MSEndpointMgr), which is what Deploy-ToIntune.ps1 uses.
/// </summary>
public class IntuneUploader
{
    /// <summary>Blob block size. 6 MB like the Intune admin center; the self-checks lower it to exercise several blocks.</summary>
    public static int ChunkSize { get; set; } = 6 * 1024 * 1024;
    static readonly TimeSpan SasRenewAfter = TimeSpan.FromMinutes(7.5);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    readonly Cloud _cloud;
    readonly Func<CancellationToken, Task<string>> _token;

    public IntuneUploader(Cloud cloud, Func<CancellationToken, Task<string>> token)
    {
        _cloud = cloud;
        _token = token;
    }

    string GraphUrl(string path) => $"{_cloud.GraphHost}/beta/{path}";

    // ── Graph plumbing ────────────────────────────────────────────────────────
    async Task<JsonNode?> GraphAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, GraphUrl(path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

            // Throttling and transient service errors: wait and retry a few times.
            if ((res.StatusCode == (HttpStatusCode)429 || (int)res.StatusCode >= 500) && attempt < 5)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                await Task.Delay(wait, ct);
                continue;
            }
            throw new GraphException(Explain(res.StatusCode, text, method, path), res.StatusCode);
        }
    }

    static string Explain(HttpStatusCode status, string body, HttpMethod method, string path)
    {
        string msg = body;
        try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; } catch { }
        if (msg.Length > 600) msg = msg[..600] + "...";
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " Sign in again.",
            HttpStatusCode.Forbidden => " Your account needs an Intune role that can manage apps (for example Intune Administrator or Application Manager), " +
                                        "and the DeviceManagementApps.ReadWrite.All permission must be consented for the sign-in app.",
            _ => "",
        };
        return $"Graph {method} {path.Split('?')[0]} failed ({(int)status} {status}): {msg}{hint}";
    }

    // ── Lookups ───────────────────────────────────────────────────────────────
    /// <summary>Win32 apps in the tenant with exactly this display name (to offer "update" instead of a duplicate).</summary>
    public async Task<List<ExistingApp>> FindAppsAsync(string displayName, CancellationToken ct)
    {
        var name = displayName.Replace("'", "''");
        var res = await GraphAsync(HttpMethod.Get,
            $"deviceAppManagement/mobileApps?$filter=displayName eq '{Uri.EscapeDataString(name)}'", null, ct);
        var list = new List<ExistingApp>();
        foreach (var a in res?["value"]?.AsArray() ?? new JsonArray())
        {
            if (a?["@odata.type"]?.GetValue<string>() != "#microsoft.graph.win32LobApp") continue;
            DateTime? modified = DateTime.TryParse(a["lastModifiedDateTime"]?.GetValue<string>(), out var d) ? d : null;
            list.Add(new ExistingApp(a["id"]!.GetValue<string>(), a["displayName"]!.GetValue<string>(), a["displayVersion"]?.GetValue<string>(), modified));
        }
        return list;
    }

    public async Task<List<GroupInfo>> SearchGroupsAsync(string text, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(text.Replace("'", "''"));
        var res = await GraphAsync(HttpMethod.Get,
            $"groups?$filter=startswith(displayName,'{q}')&$select=id,displayName,description&$top=25", null, ct);
        return (res?["value"]?.AsArray() ?? new JsonArray())
            .Select(g => new GroupInfo(g!["id"]!.GetValue<string>(), g["displayName"]!.GetValue<string>(), g["description"]?.GetValue<string>()))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── The app body ──────────────────────────────────────────────────────────
    public static JsonObject BuildAppBody(Project p)
    {
        var a = p.App;
        var returnCodes = new JsonArray(DeploymentKit.ReturnCodes.Select(r => (JsonNode)new JsonObject
        {
            ["returnCode"] = r.Code,
            ["type"] = r.Type switch { "Success" => "success", "Soft reboot" => "softReboot", "Hard reboot" => "hardReboot", _ => "retry" },
        }).ToArray());
        return new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["displayName"] = a.Name,
            ["description"] = a.Description,
            ["publisher"] = a.Publisher,
            ["displayVersion"] = a.Version,
            ["developer"] = "",
            ["owner"] = "",
            ["notes"] = $"Packaged and tested with PackPilot. Source: {a.SetupFileName} (SHA-256 {a.SourceSha256}).",
            ["isFeatured"] = false,
            ["fileName"] = Path.GetFileName(p.PackageFile),
            ["setupFilePath"] = "Install.ps1",
            ["installCommandLine"] = AppDefinition.InstallCommand,
            ["uninstallCommandLine"] = AppDefinition.UninstallCommand,
            ["allowAvailableUninstall"] = true,
            ["applicableArchitectures"] = "none",
            ["allowedArchitectures"] = a.Architecture switch { "x64" => "x64", "arm64" => "arm64", _ => "x64,x86" },
            ["minimumSupportedWindowsRelease"] = "1809",
            ["installExperience"] = new JsonObject
            {
                ["runAsAccount"] = "system",
                ["deviceRestartBehavior"] = "basedOnReturnCode",
                ["maxRunTimeInMinutes"] = 60,
            },
            ["returnCodes"] = returnCodes,
            ["detectionRules"] = new JsonArray(DetectionRule(p)),
            ["msiInformation"] = null,
        };
    }

    static JsonObject DetectionRule(Project p)
    {
        var a = p.App;
        var min = Util.CleanVersion(a.DetectMinVersion);
        switch (a.Detection)
        {
            case DetectionMethod.MsiProductCode:
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppProductCodeDetection",
                    ["productCode"] = a.ProductCode,
                    ["productVersionOperator"] = min.Length > 0 ? "greaterThanOrEqual" : "notConfigured",
                    ["productVersion"] = min.Length > 0 ? min : null,
                };
            case DetectionMethod.File:
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemDetection",
                    ["path"] = Util.SplitWindowsPath(a.DetectFilePath).Folder,
                    ["fileOrFolderName"] = Util.SplitWindowsPath(a.DetectFilePath).Name,
                    ["check32BitOn64System"] = false,
                    ["detectionType"] = min.Length > 0 ? "version" : "exists",
                    ["operator"] = min.Length > 0 ? "greaterThanOrEqual" : "notConfigured",
                    ["detectionValue"] = min.Length > 0 ? min : null,
                };
            case DetectionMethod.Marker:
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryDetection",
                    ["keyPath"] = $@"HKEY_LOCAL_MACHINE\SOFTWARE\PackPilot\Apps\{a.AppId}",
                    ["valueName"] = "Version",
                    ["check32BitOn64System"] = false,
                    ["detectionType"] = "string",
                    ["operator"] = "equal",
                    ["detectionValue"] = a.Version,
                };
            default:
                // Programs and Features name match: the generated Detect.ps1, exactly as tested in the Sandbox.
                var script = File.ReadAllBytes(Path.Combine(p.DeployDir, "Detect.ps1"));
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptDetection",
                    ["enforceSignatureCheck"] = false,
                    ["runAs32Bit"] = false,
                    ["scriptContent"] = Convert.ToBase64String(script),
                };
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────
    public async Task<string> UploadAsync(Project p, UploadOptions opt, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        if (p.Package == null || !File.Exists(p.PackageFile)) throw new InvalidOperationException("Build the package first.");
        var pkg = ReadPackage(p.PackageFile);
        var body = BuildAppBody(p);

        string appId;
        if (opt.ExistingAppId != null)
        {
            appId = opt.ExistingAppId;
            log($"Updating the existing app's settings ({appId})...");
            await GraphAsync(HttpMethod.Patch, $"deviceAppManagement/mobileApps/{appId}", body, ct);
        }
        else
        {
            log("Creating the Win32 app...");
            var created = await GraphAsync(HttpMethod.Post, "deviceAppManagement/mobileApps", body, ct);
            appId = created!["id"]!.GetValue<string>();
            log($"Created app {appId}.");
        }

        var appPath = $"deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp";
        var cv = await GraphAsync(HttpMethod.Post, $"{appPath}/contentVersions", new JsonObject(), ct);
        var cvId = cv!["id"]!.GetValue<string>();
        var file = await GraphAsync(HttpMethod.Post, $"{appPath}/contentVersions/{cvId}/files", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppContentFile",
            ["name"] = Path.GetFileName(p.PackageFile),
            ["size"] = pkg.UnencryptedSize,
            ["sizeEncrypted"] = pkg.EncryptedSize,
            ["manifest"] = null,
            ["isDependency"] = false,
        }, ct);
        var filePath = $"{appPath}/contentVersions/{cvId}/files/{file!["id"]!.GetValue<string>()}";

        log("Waiting for Intune to prepare the upload location...");
        var ready = await WaitForStateAsync(filePath, "azureStorageUriRequest", TimeSpan.FromMinutes(5), ct);
        var sas = ready["azureStorageUri"]!.GetValue<string>();

        log($"Uploading {Util.FormatSize(pkg.EncryptedSize)}...");
        await UploadBlobAsync(p.PackageFile, pkg.EncryptedSize, sas, filePath, log, progress, ct);

        log("Committing the file (Intune checks the encryption details)...");
        await GraphAsync(HttpMethod.Post, $"{filePath}/commit", new JsonObject { ["fileEncryptionInfo"] = pkg.EncryptionInfo }, ct);
        await WaitForStateAsync(filePath, "commitFile", TimeSpan.FromMinutes(15), ct);

        await GraphAsync(HttpMethod.Patch, $"deviceAppManagement/mobileApps/{appId}", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["committedContentVersion"] = cvId,
        }, ct);
        log("Upload committed.");

        if (opt.Intent != AssignIntent.None)
        {
            await AssignAsync(appId, opt, ct);
            log($"Assigned: {Describe(opt)}.");
        }
        return appId;
    }

    public static string Describe(UploadOptions o) => o.Intent == AssignIntent.None ? "not assigned"
        : $"{o.Intent.ToString().ToLowerInvariant()} for " + o.Target switch
        {
            AssignTarget.AllDevices => "all devices",
            AssignTarget.AllUsers => "all users",
            _ => $"group {o.GroupName ?? o.GroupId}",
        };

    async Task AssignAsync(string appId, UploadOptions o, CancellationToken ct)
    {
        JsonObject target = o.Target switch
        {
            AssignTarget.AllDevices => new JsonObject { ["@odata.type"] = "#microsoft.graph.allDevicesAssignmentTarget" },
            AssignTarget.AllUsers => new JsonObject { ["@odata.type"] = "#microsoft.graph.allLicensedUsersAssignmentTarget" },
            _ => new JsonObject { ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget", ["groupId"] = o.GroupId },
        };
        target["deviceAndAppManagementAssignmentFilterId"] = null;
        target["deviceAndAppManagementAssignmentFilterType"] = "none";
        await GraphAsync(HttpMethod.Post, $"deviceAppManagement/mobileApps/{appId}/assignments", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppAssignment",
            ["intent"] = o.Intent.ToString().ToLowerInvariant(),
            ["source"] = "direct",
            ["target"] = target,
            ["settings"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.win32LobAppAssignmentSettings",
                ["notifications"] = "showAll",
                ["deliveryOptimizationPriority"] = "notConfigured",
                ["restartSettings"] = null,
                ["installTimeSettings"] = null,
            },
        }, ct);
    }

    /// <summary>Polls a content file until its uploadState is "{stage}Success"; fails on "...Failed" or "...TimedOut".</summary>
    async Task<JsonNode> WaitForStateAsync(string filePath, string stage, TimeSpan limit, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            var f = await GraphAsync(HttpMethod.Get, filePath, null, ct);
            var state = f?["uploadState"]?.GetValue<string>() ?? "";
            if (state.Equals(stage + "Success", StringComparison.OrdinalIgnoreCase)) return f!;
            if (state.EndsWith("Failed", StringComparison.OrdinalIgnoreCase) || state.EndsWith("TimedOut", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Intune reported {state}." +
                    (stage == "commitFile" ? " The package's encryption details were rejected; rebuild it and upload again." : ""));
            if (DateTime.UtcNow > until) throw new TimeoutException($"Intune did not finish '{stage}' within {limit.TotalMinutes:0} minutes (last state: {state}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    async Task UploadBlobAsync(string packagePath, long size, string sas, string filePath, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        int count = (int)Math.Ceiling(size / (double)ChunkSize);
        var ids = new List<string>(count);
        var renewTimer = System.Diagnostics.Stopwatch.StartNew();
        using var zip = ZipFile.OpenRead(packagePath);
        await using var stream = EncryptedEntry(zip).Open();
        var buffer = new byte[ChunkSize];
        for (int i = 0; i < count; i++)
        {
            int n = 0, r;
            while (n < buffer.Length && (r = await stream.ReadAsync(buffer.AsMemory(n), ct)) > 0) n += r;
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(i.ToString("000000")));
            ids.Add(id);
            await RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, $"{sas}&comp=block&blockid={Uri.EscapeDataString(id)}")
                {
                    Content = new ByteArrayContent(buffer, 0, n),
                };
                req.Headers.Add("x-ms-blob-type", "BlockBlob");
                using var res = await Http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Blob block upload failed ({(int)res.StatusCode}).");
            }, $"block {i + 1} of {count}", log, ct);
            progress((i + 1) / (double)count);

            // The upload link is only valid for a few minutes: renew it during long uploads.
            if (i < count - 1 && renewTimer.Elapsed > SasRenewAfter)
            {
                log("Renewing the upload link...");
                await GraphAsync(HttpMethod.Post, $"{filePath}/renewUpload", new JsonObject(), ct);
                var renewed = await WaitForStateAsync(filePath, "azureStorageUriRenewal", TimeSpan.FromMinutes(3), ct);
                sas = renewed["azureStorageUri"]!.GetValue<string>();
                renewTimer.Restart();
            }
        }

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var id in ids) xml.Append("<Latest>").Append(id).Append("</Latest>");
        xml.Append("</BlockList>");
        await RetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{sas}&comp=blocklist")
            {
                Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/plain"),
            };
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Blob finalize failed ({(int)res.StatusCode}).");
        }, "finalize", log, ct);
    }

    static async Task RetryAsync(Func<Task> action, string what, Action<string> log, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { await action(); return; }
            catch (Exception ex) when (attempt < 6 && ex is not OperationCanceledException)
            {
                log($"Retrying {what} ({ex.Message})");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }
    }

    // ── Package details Intune needs ─────────────────────────────────────────
    public record PackageDetails(long UnencryptedSize, long EncryptedSize, JsonObject EncryptionInfo);

    static ZipArchiveEntry EncryptedEntry(ZipArchive zip) =>
        zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("IntuneWinPackage/Contents/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".intunewin"))
        ?? throw new InvalidDataException("The package has no encrypted content.");

    public static PackageDetails ReadPackage(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var xmlEntry = zip.GetEntry("IntuneWinPackage/Metadata/Detection.xml") ?? throw new InvalidDataException("Detection.xml is missing.");
        XDocument doc;
        using (var s = xmlEntry.Open()) doc = XDocument.Load(s);
        var info = doc.Root!.Element("EncryptionInfo")!;
        string V(string n) => info.Element(n)?.Value ?? throw new InvalidDataException($"Detection.xml has no {n}.");
        return new PackageDetails(
            long.Parse(doc.Root.Element("UnencryptedContentSize")!.Value),
            EncryptedEntry(zip).Length,
            new JsonObject
            {
                ["encryptionKey"] = V("EncryptionKey"),
                ["macKey"] = V("MacKey"),
                ["initializationVector"] = V("InitializationVector"),
                ["mac"] = V("Mac"),
                ["profileIdentifier"] = "ProfileVersion1",
                ["fileDigest"] = V("FileDigest"),
                ["fileDigestAlgorithm"] = V("FileDigestAlgorithm"),
            });
    }
}
