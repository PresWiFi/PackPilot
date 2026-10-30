using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PackPilot.Core;

namespace PackPilot;

public partial class MainWindow : Window
{
    Project? _project;
    bool _loading;
    bool _busy;
    CancellationTokenSource? _cts;
    readonly string _settingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackPilot", "settings.json");

    record StepRow(string Step, string Detail, bool Ok)
    {
        public string Mark => Ok ? "✓" : "✗";
        /// <summary>The detail, cut short for the list; the full text is in the tooltip and the report.</summary>
        public string Short => Detail.Length > 80 ? Detail[..77].TrimEnd() + "..." : Detail;
        public Brush Color => Ok ? Brushes.ForestGreen : Brushes.Firebrick;
    }

    public MainWindow()
    {
        InitializeComponent();
        OutputBox.Text = LoadOutputRoot();
        ReuseBox.IsChecked = !(LoadSettings().TryGetValue("ReuseSandbox", out var reuse) && reuse == "false");
        CloseSandboxButton.Visibility = ReuseBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // Every edit refreshes the derived displays (commands, stale marker).
        foreach (var tb in new[] { NameBox, VersionBox, PublisherBox, DescriptionBox, InstallArgsBox, UninstallFileBox, UninstallArgsBox,
                                   ProductCodeBox, SilentArgsBox, DisplayNameBox, DetectFileBox, MinVersionBox, OutputBox, TimeoutBox })
            tb.TextChanged += (_, _) => OnFormChanged();
        foreach (var cb in new[] { IncludeFolderBox, NoNetworkBox, SkipUninstallBox })
            cb.Click += (_, _) => OnFormChanged();
        Log($"PackPilot {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} ready. " +
            (Sandbox.IsInstalled ? "Windows Sandbox is available." : "Windows Sandbox is not turned on: you can build packages, but not test them."));

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += async (_, _) => await ImportAsync(args[1]);
    }

    // ── Import ────────────────────────────────────────────────────────────────
    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (files[0].EndsWith("project.json", StringComparison.OrdinalIgnoreCase)) OpenProject(files[0]);
            else await ImportAsync(files[0]);
        }
    }

    async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Installers (*.exe;*.msi;*.ps1)|*.exe;*.msi;*.ps1|All files (*.*)|*.*", Title = "Choose an installer" };
        if (dlg.ShowDialog(this) == true) await ImportAsync(dlg.FileName);
    }

    async Task ImportAsync(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".exe" or ".msi" or ".ps1"))
        {
            MessageBox.Show(this, "PackPilot takes .exe, .msi and .ps1 files.", "PackPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmDiscard()) return;
        SetBusy(true, $"Reading {System.IO.Path.GetFileName(path)}...");
        try
        {
            var app = await Task.Run(() => InstallerAnalyzer.Analyze(path));
            _project = new Project { App = app, OutputRoot = OutputBox.Text.Trim() };
            _project.Logged = text => Dispatcher.BeginInvoke(() => Log(text));
            _project.Note($"Imported {app.SetupFileName} ({Util.FormatSize(app.SourceSize)}, SHA-256 {app.SourceSha256[..16]}...).");
            _project.Note($"Identified it as {app.EngineLabel} (confidence {app.Confidence}). Silent install: {app.RawInstallCommand}");
            if (!string.IsNullOrEmpty(app.ProductCode)) _project.Note($"MSI product code {app.ProductCode}, version {app.Version}.");
            _project.Note($"Signature: {app.Signature}.");
            LoadForm();
        }
        catch (Exception ex) { Error("Could not read that file", ex); }
        finally { SetBusy(false); }
    }

    void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _project = null;
        Form.IsEnabled = false;
        Workspace.Visibility = Visibility.Collapsed;
        DropZone.Visibility = Visibility.Visible;
        StepsList.ItemsSource = null;
        RefreshStatus();
    }

    void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PackPilot project (project.json)|project.json", Title = "Open a PackPilot project", InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : "" };
        if (dlg.ShowDialog(this) == true) OpenProject(dlg.FileName);
    }

    void OpenProject(string file)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            var p = Project.Load(file);
            // The project may have been moved: trust where project.json is now.
            var folder = System.IO.Path.GetDirectoryName(file)!;
            p.OutputRoot = System.IO.Path.GetDirectoryName(folder)!;
            p.FolderName = System.IO.Path.GetFileName(folder);
            p.Logged = text => Dispatcher.BeginInvoke(() => Log(text));
            _project = p;
            OutputBox.Text = p.OutputRoot;
            LoadForm();
            Log($"Opened {p.App.Name} {p.App.Version} from {folder}.");
            if (!File.Exists(p.App.SourcePath)) Log($"Note: the original installer is no longer at {p.App.SourcePath}. Rebuilding needs it.");
        }
        catch (Exception ex) { Error("Could not open that project", ex); }
    }

    bool ConfirmDiscard() => _project == null || _project.Package != null ||
        MessageBox.Show(this, "The current app has not been built. Discard it?", "PackPilot", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ── Form <-> model ────────────────────────────────────────────────────────
    void LoadForm()
    {
        var a = _project!.App;
        _loading = true;
        try
        {
            Form.IsEnabled = true;
            DropZone.Visibility = Visibility.Collapsed;
            Workspace.Visibility = Visibility.Visible;
            DropSub.Text = $"{a.SetupFileName} · {Util.FormatSize(a.SourceSize)}";
            DropSub.ToolTip = a.SourcePath;
            EngineText.Text = a.EngineLabel;
            ConfidenceText.Text = $"{a.Confidence} confidence";
            bool signed = a.Signature.StartsWith("Valid") || a.Kind == InstallerKind.PowerShell || a.Signature.StartsWith("not checked");
            SignatureChip.Visibility = a.Kind == InstallerKind.PowerShell ? Visibility.Collapsed : Visibility.Visible;
            SignatureChipText.Text = a.Signature.StartsWith("Valid") ? "Signed" : a.Signature.StartsWith("not checked") ? "Signature not checked" : "Not signed";
            SignatureChip.Background = new SolidColorBrush(signed ? Color.FromRgb(0xDC, 0xFC, 0xE7) : Color.FromRgb(0xFE, 0xF3, 0xC7));
            SignatureChip.ToolTip = a.Signature;
            ConfidenceChip.Background = new SolidColorBrush(a.Confidence switch
            {
                "high" => Color.FromRgb(0xDC, 0xFC, 0xE7), "medium" => Color.FromRgb(0xFE, 0xF3, 0xC7), _ => Color.FromRgb(0xFE, 0xE2, 0xE2),
            });
            AnalyzerNotes.Text = a.AnalyzerNotes;
            ArchText.Text = $"Architecture: {a.Architecture}. {a.ArchitectureNote}";
            SignatureText.Text = $"Signature: {a.Signature}";

            NameBox.Text = a.Name; VersionBox.Text = a.Version; PublisherBox.Text = a.Publisher; DescriptionBox.Text = a.Description;
            InstallArgsBox.Text = a.InstallArgs;
            InstallArgsLabel.Text = a.Kind == InstallerKind.PowerShell ? "Script parameters (optional)" : "Silent install arguments";
            IncludeFolderBox.IsChecked = a.IncludeSourceFolder;
            UninstallFileBox.Text = a.UninstallFile; UninstallArgsBox.Text = a.UninstallArgs;
            ProductCodeBox.Text = a.ProductCode; SilentArgsBox.Text = a.UninstallSilentArgs;
            DisplayNameBox.Text = a.DisplayNamePattern; DetectFileBox.Text = a.DetectFilePath; MinVersionBox.Text = a.DetectMinVersion;
            foreach (ComboBoxItem item in DetectionBox.Items)
                if ((string)item.Tag == a.Detection.ToString()) DetectionBox.SelectedItem = item;
            NoNetworkBox.IsChecked = a.SandboxNoNetwork; SkipUninstallBox.IsChecked = a.SandboxSkipUninstall;
            TimeoutBox.Text = a.SandboxTimeoutMinutes.ToString();
            UpdateFolderStats();
        }
        finally { _loading = false; }
        UpdateDetectionPanels();
        RefreshStatus();
    }

    void ReadForm()
    {
        if (_project == null) return;
        var a = _project.App;
        a.Name = NameBox.Text.Trim(); a.Version = VersionBox.Text.Trim(); a.Publisher = PublisherBox.Text.Trim(); a.Description = DescriptionBox.Text.Trim();
        a.InstallArgs = InstallArgsBox.Text.Trim();
        a.IncludeSourceFolder = IncludeFolderBox.IsChecked == true;
        a.UninstallFile = UninstallFileBox.Text.Trim(); a.UninstallArgs = UninstallArgsBox.Text.Trim();
        a.ProductCode = ProductCodeBox.Text.Trim(); a.UninstallSilentArgs = SilentArgsBox.Text.Trim();
        a.DisplayNamePattern = DisplayNameBox.Text.Trim(); a.DetectFilePath = DetectFileBox.Text.Trim(); a.DetectMinVersion = MinVersionBox.Text.Trim();
        if (DetectionBox.SelectedItem is ComboBoxItem item) a.Detection = Enum.Parse<DetectionMethod>((string)item.Tag);
        a.SandboxNoNetwork = NoNetworkBox.IsChecked == true; a.SandboxSkipUninstall = SkipUninstallBox.IsChecked == true;
        if (int.TryParse(TimeoutBox.Text, out var t)) a.SandboxTimeoutMinutes = Math.Clamp(t, 5, 240);
        // The output folder can only change before the first build; after that the project lives where it is.
        if (_project.FolderName == null) _project.OutputRoot = OutputBox.Text.Trim();
    }

    void OnFormChanged()
    {
        if (_loading || _project == null) return;
        ReadForm();
        UpdateFolderStats();
        RefreshStatus();
    }

    void OnDetectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetectionPanels();
        OnFormChanged();
    }

    void UpdateDetectionPanels()
    {
        var tag = (DetectionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        NamePanel.Visibility = tag == "Registry" ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = tag == "File" ? Visibility.Visible : Visibility.Collapsed;
        VersionPanel.Visibility = tag == "Marker" ? Visibility.Collapsed : Visibility.Visible;
        // Uninstall can still look the name up when detection uses something else, so keep it editable there.
        if (tag != "Registry" && _project?.App.Kind == InstallerKind.Exe) NamePanel.Visibility = Visibility.Visible;
    }

    void UpdateFolderStats()
    {
        if (_project == null) return;
        if (IncludeFolderBox.IsChecked != true) { FolderStatsText.Text = ""; return; }
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_project.App.SourcePath)!;
            var (bytes, files) = Util.FolderStats(dir);
            FolderStatsText.Text = $"{files} file(s), {Util.FormatSize(bytes)} in {dir}" +
                (files > 300 || bytes > 4L * 1024 * 1024 * 1024 ? "  ·  that is a lot: is this a Downloads folder? Put the installer in its own folder." : "");
        }
        catch (Exception ex) { FolderStatsText.Text = ex.Message; }
    }

    void RefreshStatus()
    {
        var p = _project;
        BuildButton.IsEnabled = p != null;
        TestButton.IsEnabled = p != null;
        OpenReportButton.IsEnabled = OpenGuideButton.IsEnabled = OpenFolderButton.IsEnabled = p?.Package != null;
        UploadButton.IsEnabled = p?.Package != null && !_busy;
        UploadSub.Text = p?.Uploads.LastOrDefault() is { } last
            ? $"Last uploaded {last.At:yyyy-MM-dd HH:mm} to {last.Cloud}" + (p.Package != null && last.PackageSha256 != p.Package.PackageSha256 ? " (older build)" : "")
            : "Sign in with Graph: Commercial, GCC, GCC High, DoD";
        if (p == null)
        {
            PackageStatus.Text = "No package yet.";
            InstallCmdBox.Text = UninstallCmdBox.Text = DetectionCmdText.Text = "";
            SetVerdict(null);
            UpdateRebuildButton(null);
            return;
        }
        var a = p.App;
        RawInstallText.Text = $"Runs: {a.RawInstallCommand}";
        DetectionSummaryText.Text = a.DetectionSummary;
        AppTitle.Text = $"{a.Name} {a.Version}";
        UninstallSummary.Text = a.RawUninstallCommand;
        OptionsSummary.Text = string.Join(" · ", new[]
        {
            a.IncludeSourceFolder ? "whole folder" : "installer only",
            ReuseBox.IsChecked == true ? "reused Sandbox" : "fresh Sandbox each test",
            a.SandboxNoNetwork ? "no network" : null,
            a.SandboxSkipUninstall ? "no uninstall test" : null,
            $"{a.SandboxTimeoutMinutes} min limit",
        }.Where(x => x != null));
        InstallCmdBox.Text = AppDefinition.InstallCommand;
        UninstallCmdBox.Text = AppDefinition.UninstallCommand;
        DetectionCmdText.Text = a.DetectionSummary + (a.Detection == DetectionMethod.Registry ? "  (upload Deploy\\Detect.ps1)" : "");
        var errors = PackageBuilder.Validate(a);
        if (p.Package == null)
            PackageStatus.Text = errors.Count > 0 ? "Before building: " + string.Join(" ", errors) : "Ready to build.";
        else
            PackageStatus.Text = $"{System.IO.Path.GetFileName(p.PackageFile)}, {Util.FormatSize(p.Package.PackageSize)}, verified {p.Package.BuiltAt:HH:mm}." +
                                 (p.PackageStale ? "\nSettings changed since then: build again (Test does it for you)." : "");
        BuildSub.Text = p.Package == null ? "Package, encrypt and verify" : p.PackageStale ? "Settings changed: rebuild" : "Built and verified. Click to rebuild.";
        TestSub.Text = Sandbox.IsInstalled ? "Install, detect, uninstall as SYSTEM" : "Windows Sandbox is not turned on (click for how)";
        SetVerdict(p.LastTest);
        UpdateRebuildButton(p);
    }

    void SetVerdict(SandboxResult? t)
    {
        VerdictText.Text = t?.Verdict ?? "Not tested";
        VerdictChip.Background = new SolidColorBrush(t == null ? Color.FromRgb(0xE5, 0xE7, 0xEB)
            : t.Verdict == "PASS" ? Color.FromRgb(0xDC, 0xFC, 0xE7)
            : t.Verdict.StartsWith("PASS") ? Color.FromRgb(0xFE, 0xF3, 0xC7) : Color.FromRgb(0xFE, 0xE2, 0xE2));
        StepsList.ItemsSource = t?.Steps.Select(s => new StepRow(s.Step, s.Detail, s.Ok)).ToList();
    }

    /// <summary>
    /// The rebuild button turns red when the package is out of date: the Sandbox test learned better
    /// detection/uninstall values, or settings changed since the last build. Grey and disabled otherwise.
    /// </summary>
    void UpdateRebuildButton(Project? p)
    {
        int learned = p?.LastTest?.Learned != null ? PreviewLearned(p).Count : 0;
        bool stale = p?.Package != null && p.PackageStale;
        bool failed = p != null && LastTestFailedOnThisBuild(p);
        bool tested = p?.Tests.Count > 0;
        bool needed = learned > 0 || stale || failed;
        LearnButton.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        LearnButton.IsEnabled = !_busy;
        LearnButton.Content =
            learned > 0 ? $"Use what the test learned ({learned} change{(learned == 1 ? "" : "s")}): rebuild and retest"
            : stale ? (tested ? "Settings changed: rebuild and retest" : "Settings changed: rebuild now")
            : failed ? "Test failed: see why, then fix and retest"
            : "Rebuild (nothing to update)";
        LearnButton.ToolTip = learned > 0
            ? "The Sandbox test found better detection or uninstall values:\n" + string.Join("\n", PreviewLearned(p!))
            : stale ? "The package was built with older settings. Rebuild so it matches what you see, then test it."
            : failed ? "Shows what went wrong and what to change." : null;
    }

    static List<string> PreviewLearned(Project p)
    {
        var copy = JsonSerializer.Deserialize<AppDefinition>(JsonSerializer.Serialize(p.App, Project.JsonOpts), Project.JsonOpts)!;
        return Sandbox.ApplyLearned(copy, p.LastTest!.Learned!);
    }

    // ── Build ─────────────────────────────────────────────────────────────────
    async void OnBuild(object sender, RoutedEventArgs e) => await BuildAsync();

    async Task<bool> BuildAsync()
    {
        if (_project == null) return false;
        ReadForm();
        var errors = PackageBuilder.Validate(_project.App);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Fix these first", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_project.OutputRoot))
        {
            MessageBox.Show(this, "Choose an output folder.", "PackPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        SaveOutputRoot(_project.OutputRoot);
        SetBusy(true, "Building the package...");
        try
        {
            var p = _project;
            await Task.Run(() => PackageBuilder.Build(p));
            Log($"Package ready: {p.PackageFile}");
            return true;
        }
        catch (Exception ex) { Error("Build failed", ex); return false; }
        finally { SetBusy(false); RefreshStatus(); }
    }

    // ── Sandbox ───────────────────────────────────────────────────────────────
    async void OnTest(object sender, RoutedEventArgs e) => await RunTestAsync();

    async Task RunTestAsync()
    {
        if (_project == null) return;
        if (!Sandbox.IsInstalled)
        {
            var r = MessageBox.Show(this,
                "Windows Sandbox is not turned on for this PC. It needs Windows 10/11 Pro, Enterprise or Education, and virtualization enabled in the BIOS.\n\n" +
                "Turn it on from an admin PowerShell, then restart:\n\n" + Sandbox.EnableCommand + "\n\n" +
                "Copy that command to the clipboard?", "Windows Sandbox", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) Clipboard.SetText(Sandbox.EnableCommand);
            return;
        }
        ReadForm();
        var p = _project;
        bool reuse = ReuseBox.IsChecked == true;
        _cts = new CancellationTokenSource();
        SetBusy(true, "Checking for an open Sandbox...");
        try
        {
            var session = await Sandbox.GetSessionAsync();
            if (session == null && Sandbox.IsRunning())
            {
                MessageBox.Show(this, "A Windows Sandbox that PackPilot can't use is already open (only one can run at a time). Close it, then try again.",
                    "Windows Sandbox", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // A session that no longer fits: reuse is off, the network setting differs, or the last test left the app installed.
            if (session != null)
            {
                string? why = !reuse ? "Reuse is turned off, so this test needs a fresh Sandbox."
                    : session.NoNetwork != p.App.SandboxNoNetwork ? $"The open test Sandbox has networking {(session.NoNetwork ? "off" : "on")}, but this test wants it {(p.App.SandboxNoNetwork ? "off" : "on")}."
                    : session.Dirty ? "The last test in the open Sandbox did not end with the app cleanly uninstalled, so leftovers could affect this test."
                    : null;
                if (why != null)
                {
                    bool mustClose = !reuse || session.NoNetwork != p.App.SandboxNoNetwork;
                    var answer = MessageBox.Show(this, why + "\n\nClose the open test Sandbox and start a fresh one?" + (mustClose ? "" : "\n(No = reuse it anyway)"),
                        "Fresh Sandbox?", mustClose ? MessageBoxButton.OKCancel : MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel) return;
                    if (answer is MessageBoxResult.OK or MessageBoxResult.Yes)
                    {
                        Log("Closing the open test Sandbox...");
                        if (!await Sandbox.CloseSessionAsync(TimeSpan.FromSeconds(60)))
                        {
                            MessageBox.Show(this, "The test Sandbox did not close. Close its window, then try again.", "Windows Sandbox");
                            return;
                        }
                        session = null;
                    }
                }
            }

            if (p.PackageStale)
            {
                Log("Settings changed since the last build (or no build yet): building first.");
                SetBusy(false);
                if (!await BuildAsync()) return;
                SetBusy(true);
            }
            CancelButton.Visibility = Visibility.Visible;

            string runId;
            bool useSession = reuse;
            if (reuse && session != null)
            {
                runId = Sandbox.QueueJob(p);
                Log($"Reusing the open test Sandbox (test {session.TestsRun + 1} in it). Results follow below.");
            }
            else if (reuse)
            {
                Sandbox.StartSession(p, p.App.SandboxNoNetwork);
                runId = Sandbox.QueueJob(p);
                Log("Starting a reusable test Sandbox. The first test starts once it has booted; leave the Sandbox window open.");
            }
            else
            {
                runId = Sandbox.Prepare(p);
                Sandbox.Launch(p, runId);
                Log("Starting Windows Sandbox. The test runs by itself in the Sandbox window; leave it open.");
            }

            var result = await Sandbox.WaitForResultAsync(p, runId, useSession, line => Dispatcher.BeginInvoke(() => Log("  [sandbox] " + line)), _cts.Token);
            result.PackageSha256 = p.Package?.PackageSha256 ?? "";
            p.Tests.Add(result);
            if (useSession) Sandbox.SessionDirty = !Sandbox.LeftClean(result);
            p.Note($"Sandbox test {result.RunId}: {result.Verdict}" + (result.SessionRun > 1 ? $" (reused Sandbox, test {result.SessionRun} in it)" : "") + ". " +
                   string.Join("; ", result.Steps.Select(s => $"{s.Step} {(s.Ok ? "pass" : "FAIL")}")));
            DeploymentKit.Write(p);       // the guide records the latest test
            PackageBuilder.Finish(p);
            RefreshStatus();
            if (result.Verdict == "PASS") Log("Sandbox test passed.");
            else
            {
                if (!string.IsNullOrWhiteSpace(result.InstallOutput) && result.Steps.Any(s => s.Step == "Install" && !s.Ok))
                {
                    Log("Install output (last lines):");
                    foreach (var line in result.InstallOutput.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(12))
                        Log("    " + line.Trim());
                }
                foreach (var hint in Sandbox.FailureHints(p.App, result)) Log("Hint: " + hint);
            }
            if (!await OfferLearnedAsync() && result.Verdict != "PASS") ShowFailureHelp(p, result, offerRetest: false);
        }
        catch (OperationCanceledException) { Log("Stopped waiting for the Sandbox. Results may still appear in the SandboxTest\\Results folder."); }
        catch (SandboxStoppedException ex)
        {
            Log("Windows Sandbox stopped without a result.");
            p.Note("Windows Sandbox stopped before the test finished (no result).");
            PackageBuilder.Finish(p);
            MessageBox.Show(this, ex.Message, "Windows Sandbox stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Error("Sandbox test failed", ex); }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
            RefreshStatus();
        }
    }

    async void OnCloseSandbox(object sender, RoutedEventArgs e)
    {
        if (!Sandbox.IsRunning()) { Log("No test Sandbox is open."); return; }
        SetBusy(true, "Closing the test Sandbox...");
        try
        {
            if (await Sandbox.CloseSessionAsync(TimeSpan.FromSeconds(60))) Log("Test Sandbox closed. The next test starts a fresh one.");
            else Log("The Sandbox did not close by itself (it may not be PackPilot's, or it is busy). Close its window.");
        }
        finally { SetBusy(false); RefreshStatus(); }
    }

    void OnReuseChanged(object sender, RoutedEventArgs e)
    {
        SaveSetting("ReuseSandbox", ReuseBox.IsChecked == true ? "true" : "false");
        RefreshStatus();
        CloseSandboxButton.Visibility = ReuseBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_project?.Package == null) return;
        ReadForm();
        if (_project.PackageStale)
        {
            MessageBox.Show(this, "Settings changed since the package was built, so the package doesn't match what you see. Rebuild it first (the red button), then upload.",
                "Rebuild first", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new UploadWindow(_project,
            key => LoadSettings().TryGetValue(key, out var v) ? v : null,
            SaveSetting) { Owner = this };
        w.ShowDialog();
        RefreshStatus();
    }

    /// <summary>Offers the learned values after a test. True when there was something to offer.</summary>
    async Task<bool> OfferLearnedAsync()
    {
        var p = _project!;
        var t = p.LastTest!;
        if (t.Learned == null) return false;
        var changes = PreviewLearned(p);
        if (changes.Count == 0) return false;
        var msg = $"The test saw the app install as \"{t.Learned.DisplayName}\" {t.Learned.DisplayVersion}.\n\n" +
                  "Use that for detection and uninstall?\n\n  " + string.Join("\n  ", changes) +
                  "\n\nPackPilot rebuilds the package and tests it again.";
        if (MessageBox.Show(this, msg, "Use what the test learned?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ApplyLearnedAsync();
        return true;
    }

    /// <summary>The latest test was run on the package as it is now, and did not pass.</summary>
    static bool LastTestFailedOnThisBuild(Project p) =>
        p.LastTest is { } t && t.Verdict != "PASS" && p.Package != null && t.PackageSha256 == p.Package.PackageSha256;

    /// <summary>Why the test failed and what to change. Returns true when the user asked to test again as is.</summary>
    bool ShowFailureHelp(Project p, SandboxResult t, bool offerRetest)
    {
        var hints = Sandbox.FailureHints(p.App, t);
        var text = $"The Sandbox test {(t.Verdict.StartsWith("PASS") ? "passed with warnings" : "failed")}.\n\n" +
                   (hints.Count > 0 ? "• " + string.Join("\n\n• ", hints) : "See the report for each step.") +
                   "\n\nChange the settings on the left (for example the install arguments). The red button then rebuilds the package and tests it again.";
        if (!offerRetest)
        {
            MessageBox.Show(this, text, "Sandbox test: what went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return MessageBox.Show(this, text + "\n\nNothing has changed since that test, so it will probably fail the same way. Test this same package again anyway?",
            "Sandbox test: what went wrong", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    async void OnApplyLearned(object sender, RoutedEventArgs e) => await ApplyLearnedAsync();

    async Task ApplyLearnedAsync()
    {
        var p = _project;
        if (p == null) return;
        ReadForm();
        var changes = p.LastTest?.Learned != null ? Sandbox.ApplyLearned(p.App, p.LastTest.Learned) : new List<string>();
        if (changes.Count == 0 && !p.PackageStale && LastTestFailedOnThisBuild(p))
        {
            if (ShowFailureHelp(p, p.LastTest!, offerRetest: true)) await RunTestAsync();
            return;
        }
        if (changes.Count > 0)
        {
            p.Note("Applied what the Sandbox test learned: " + string.Join("; ", changes) + ".");
            LoadForm();
        }
        bool retest = p.Tests.Count > 0 && Sandbox.IsInstalled;
        if (!await BuildAsync()) return;
        if (retest)
        {
            Log("Rebuilt. Testing the new package in the Sandbox...");
            await RunTestAsync();
        }
    }

    // ── Deploy helpers ────────────────────────────────────────────────────────
    void OnOpenReport(object sender, RoutedEventArgs e) => Open(_project?.ReportPath);
    void OnOpenGuide(object sender, RoutedEventArgs e) => Open(_project == null ? null : System.IO.Path.Combine(_project.DeployDir, "DEPLOYMENT.md"), notepad: true);
    void OnOpenFolder(object sender, RoutedEventArgs e) => Open(_project?.Folder);

    void Open(string? path, bool notepad = false)
    {
        if (path == null || !(File.Exists(path) || Directory.Exists(path))) return;
        try
        {
            if (notepad) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Error("Could not open it", ex); }
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        ReadForm();
        var a = _project.App;
        var text = ((Button)sender).Tag switch
        {
            "install" => AppDefinition.InstallCommand,
            "uninstall" => AppDefinition.UninstallCommand,
            _ => $"Name: {a.Name}\nDescription: {a.Description}\nPublisher: {a.Publisher}\nApp version: {a.Version}\n" +
                 $"Install command: {AppDefinition.InstallCommand}\nUninstall command: {AppDefinition.UninstallCommand}\n" +
                 $"Install behavior: System\nDevice restart behavior: Determine behavior based on return codes\n" +
                 $"Architecture: {DeploymentKit.ArchText(a)}\nDetection:\n" + string.Join("\n", DeploymentKit.DetectionSteps(a)),
        };
        try { Clipboard.SetText(text); Log("Copied to the clipboard."); }
        catch (Exception ex) { Error("Could not copy", ex); }
    }

    void OnBrowseUninstall(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dir = System.IO.Path.GetDirectoryName(_project.App.SourcePath)!;
        var dlg = new OpenFileDialog { Filter = "Uninstallers (*.ps1;*.exe;*.cmd;*.bat)|*.ps1;*.exe;*.cmd;*.bat", InitialDirectory = dir, Title = "Choose the uninstall program or script" };
        if (dlg.ShowDialog(this) != true) return;
        var chosenDir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        if (!string.Equals(chosenDir, dir, StringComparison.OrdinalIgnoreCase) && !dlg.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The uninstaller has to be in the installer's folder (or a subfolder) so it goes into the package.", "PackPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UninstallFileBox.Text = System.IO.Path.GetRelativePath(dir, dlg.FileName);
        IncludeFolderBox.IsChecked = true;
        Log($"Uninstall uses {UninstallFileBox.Text}; the installer's whole folder is now included in the package.");
        OnFormChanged();
    }

    void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        if (_project?.FolderName != null)
        {
            MessageBox.Show(this, $"This app is already built in {_project.Folder}. The new output folder applies to the next app you import.", "PackPilot");
        }
        var dlg = new OpenFolderDialog { Title = "Choose where packages go" };
        if (Directory.Exists(OutputBox.Text)) dlg.InitialDirectory = OutputBox.Text;
        if (dlg.ShowDialog(this) == true) { OutputBox.Text = dlg.FolderName; SaveOutputRoot(dlg.FolderName); }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────
    void SetBusy(bool busy, string? message = null)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BuildButton.IsEnabled = TestButton.IsEnabled = !busy && _project != null;
        UploadButton.IsEnabled = !busy && _project?.Package != null;
        _busy = busy;
        LearnButton.IsEnabled = !busy;
        Form.IsEnabled = !busy && _project != null;
        CloseSandboxButton.IsEnabled = !busy;
        DropZone.IsEnabled = !busy;
        AllowDrop = !busy;
        if (message != null) Log(message);
    }

    void Log(string text)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    void Error(string title, Exception ex)
    {
        Log($"{title}: {ex.Message}");
        _project?.Note($"{title}: {ex.Message}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    Dictionary<string, string> LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsFile)) ?? new();
        }
        catch { /* fall back to defaults */ }
        return new();
    }

    void SaveSetting(string key, string value)
    {
        try
        {
            var s = LoadSettings();
            s[key] = value;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(s));
        }
        catch { /* not important */ }
    }

    string LoadOutputRoot() =>
        LoadSettings().TryGetValue("OutputRoot", out var v) && !string.IsNullOrWhiteSpace(v)
            ? v : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PackPilot");

    void SaveOutputRoot(string root) => SaveSetting("OutputRoot", root);
}
