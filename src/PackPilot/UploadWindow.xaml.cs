using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackPilot.Core;
using PackPilot.Core.Intune;

namespace PackPilot;

/// <summary>Signs in to a tenant in the chosen cloud and uploads (and optionally assigns) the current package.</summary>
public partial class UploadWindow : Window
{
    readonly Project _p;
    readonly Func<string, string?> _getSetting;
    readonly Action<string, string> _setSetting;
    GraphSession? _session;
    IntuneUploader? _uploader;
    CancellationTokenSource? _cts;
    string? _portalUrl;
    bool _busy;

    // One signed-in session per app run, reused while cloud, tenant and client stay the same.
    static GraphSession? _lastSession;

    public UploadWindow(Project p, Func<string, string?> getSetting, Action<string, string> setSetting)
    {
        InitializeComponent();
        _p = p;
        _getSetting = getSetting;
        _setSetting = setSetting;

        foreach (var c in Cloud.All) CloudBox.Items.Add(c);
        CloudBox.SelectedItem = Cloud.ById(getSetting("IntuneCloud"));
        TenantBox.Text = getSetting("IntuneTenant") ?? "";
        ClientIdBox.Text = getSetting("IntuneClientId") ?? Cloud.DefaultClientId;

        var a = p.App;
        Heading.Text = $"Upload {a.Name} {a.Version} to Intune";
        AppSummary.Text = $"{System.IO.Path.GetFileName(p.PackageFile)} ({Util.FormatSize(p.Package?.PackageSize ?? 0)}) · detection: {a.DetectionSummary}";
        var t = p.LastTest;
        var warn = new List<string>();
        if (t == null || t.PackageSha256 != p.Package?.PackageSha256) warn.Add("This build has not been tested in Windows Sandbox.");
        else if (t.Verdict != "PASS") warn.Add($"The last Sandbox test of this build was {t.Verdict}.");
        if (warn.Count > 0) { Warning.Text = string.Join(" ", warn) + " You can still upload it; assign it to a pilot group first."; Warning.Visibility = Visibility.Visible; }

        if (_lastSession is { SignedIn: true } s) Adopt(s);
    }

    void OnCloudChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudBox.SelectedItem is Cloud c) CloudNote.Text = c.Note;
    }

    void Log(string text)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        SignInButton.IsEnabled = !busy;
        UploadButton.IsEnabled = !busy && _session?.SignedIn == true;
        AppCard.IsEnabled = AssignCard.IsEnabled = !busy && _session?.SignedIn == true;
        CloudBox.IsEnabled = TenantBox.IsEnabled = ClientIdBox.IsEnabled = !busy;
        CloseButton.Content = busy ? "Cancel" : "Close";
    }

    // ── Sign in ───────────────────────────────────────────────────────────────
    async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var cloud = (Cloud)CloudBox.SelectedItem;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            if (_session != null) await _session.SignOutAsync();
            var session = new GraphSession(cloud, TenantBox.Text, ClientIdBox.Text);
            Log($"Signing in to {cloud.Name}{(string.IsNullOrWhiteSpace(TenantBox.Text) ? "" : $" ({TenantBox.Text.Trim()})")}. Finish in the browser window.");
            await session.SignInAsync(_cts.Token);
            _setSetting("IntuneCloud", cloud.Id);
            _setSetting("IntuneTenant", TenantBox.Text.Trim());
            _setSetting("IntuneClientId", ClientIdBox.Text.Trim());
            _lastSession = session;
            Adopt(session);
        }
        catch (OperationCanceledException) { Log("Sign-in cancelled."); }
        catch (Exception ex)
        {
            Log("Sign-in failed: " + ex.Message);
            var consent = ex.Message.Contains("AADSTS65001") || ex.Message.Contains("consent", StringComparison.OrdinalIgnoreCase);
            MessageBox.Show(this, ex.Message + (consent
                ? "\n\nAn administrator has to consent to the permissions once. Sign in with a Global Administrator or Privileged Role Administrator account and accept the consent prompt (tick 'Consent on behalf of your organization')."
                : ""), "Sign-in failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    async void Adopt(GraphSession s)
    {
        _session = s;
        _uploader = new IntuneUploader(s.Cloud, s.GetTokenAsync);
        CloudBox.SelectedItem = s.Cloud;
        SignInStatus.Text = $"Signed in as {s.UserName} · {s.Cloud.Name} · tenant {s.TenantId}";
        SignInButton.Content = "Switch account";
        SetBusy(false);
        await LoadExistingAsync();
    }

    async Task LoadExistingAsync()
    {
        ExistingBox.Items.Clear();
        UpdateRadio.IsEnabled = false;
        CreateNewRadio.IsChecked = true;
        try
        {
            var apps = await _uploader!.FindAppsAsync(_p.App.Name, CancellationToken.None);
            // The app this project uploaded last time comes first.
            var mine = _p.Uploads.LastOrDefault(u => u.Tenant == _session!.TenantId)?.AppId;
            foreach (var a in apps.OrderByDescending(a => a.Id == mine).ThenByDescending(a => a.LastModified))
                ExistingBox.Items.Add(new ComboBoxItem
                {
                    Tag = a.Id,
                    Content = $"{a.DisplayName} {a.DisplayVersion} · changed {a.LastModified:yyyy-MM-dd}{(a.Id == mine ? " · uploaded from this project" : "")}",
                });
            if (apps.Count > 0)
            {
                UpdateRadio.IsEnabled = true;
                ExistingBox.SelectedIndex = 0;
                UpdateRadio.IsChecked = true;
                ExistingHint.Text = $"{apps.Count} Win32 app{(apps.Count == 1 ? "" : "s")} named \"{_p.App.Name}\" already in this tenant. Updating keeps its assignments and replaces its package and settings.";
            }
            else ExistingHint.Text = $"No Win32 app named \"{_p.App.Name}\" in this tenant yet.";
        }
        catch (Exception ex) { ExistingHint.Text = "Could not check for existing apps: " + ex.Message; }
    }

    // ── Assignment ────────────────────────────────────────────────────────────
    void OnAssignChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupPanel == null) return;
        bool assign = (IntentBox.SelectedItem as ComboBoxItem)?.Tag as string != "None";
        TargetBox.IsEnabled = assign;
        GroupPanel.Visibility = assign && (TargetBox.SelectedItem as ComboBoxItem)?.Tag as string == "Group" ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnGroupSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearchGroups(sender, e);
    }

    async void OnSearchGroups(object sender, RoutedEventArgs e)
    {
        if (_uploader == null || GroupSearchBox.Text.Trim().Length == 0) return;
        try
        {
            var groups = await _uploader.SearchGroupsAsync(GroupSearchBox.Text.Trim(), CancellationToken.None);
            GroupList.ItemsSource = groups;
            if (groups.Count == 0) Log($"No groups start with \"{GroupSearchBox.Text.Trim()}\".");
            else GroupList.SelectedIndex = 0;
        }
        catch (Exception ex) { Log("Group search failed: " + ex.Message); }
    }

    UploadOptions ReadOptions()
    {
        var o = new UploadOptions
        {
            Intent = Enum.Parse<AssignIntent>((string)((ComboBoxItem)IntentBox.SelectedItem).Tag),
            Target = Enum.Parse<AssignTarget>((string)((ComboBoxItem)TargetBox.SelectedItem).Tag),
        };
        if (UpdateRadio.IsChecked == true && ExistingBox.SelectedItem is ComboBoxItem item) o.ExistingAppId = (string)item.Tag;
        if (o.Intent != AssignIntent.None && o.Target == AssignTarget.Group && GroupList.SelectedItem is GroupInfo g) { o.GroupId = g.Id; o.GroupName = g.DisplayName; }
        return o;
    }

    // ── Upload ────────────────────────────────────────────────────────────────
    async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_uploader == null || _session == null) return;
        var o = ReadOptions();
        if (o.Intent != AssignIntent.None && o.Target == AssignTarget.Group && o.GroupId == null)
        {
            MessageBox.Show(this, "Search for a group and select it, or choose \"Don't assign\".", "Assignment", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (o.Intent == AssignIntent.Required && o.Target != AssignTarget.Group &&
            MessageBox.Show(this, $"Make this app required for {(o.Target == AssignTarget.AllDevices ? "ALL devices" : "ALL users")}? It installs everywhere without asking.",
                "Broad assignment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Progress.Value = 0;
        OpenPortalButton.Visibility = Visibility.Collapsed;
        try
        {
            var appId = await _uploader.UploadAsync(_p, o,
                msg => Dispatcher.BeginInvoke(() => Log(msg)),
                frac => Dispatcher.BeginInvoke(() => Progress.Value = frac),
                _cts.Token);
            Progress.Value = 1;
            _portalUrl = _session.Cloud.AppPortalUrl(appId);
            _p.Uploads.Add(new UploadRecord
            {
                At = DateTime.Now, Cloud = _session.Cloud.Name, Tenant = _session.TenantId ?? _session.Tenant, User = _session.UserName ?? "",
                AppId = appId, UpdatedExisting = o.ExistingAppId != null, Assignment = IntuneUploader.Describe(o),
                PortalUrl = _portalUrl, PackageSha256 = _p.Package?.PackageSha256 ?? "",
            });
            _p.Note($"Uploaded to Intune ({_session.Cloud.Name}, tenant {_session.TenantId}, as {_session.UserName}): " +
                    $"{(o.ExistingAppId != null ? "updated" : "created")} app {appId}; {IntuneUploader.Describe(o)}.");
            PackageBuilder.Finish(_p);
            Log("Done. Intune can take a few minutes to show the new content as available.");
            if (_portalUrl != null) OpenPortalButton.Visibility = Visibility.Visible;
            UploadButtonText.Text = "Upload again";
            await LoadExistingAsync();
        }
        catch (OperationCanceledException) { Log("Upload cancelled. If the app was already created, it stays in Intune without content; delete it or upload again to it."); }
        catch (Exception ex)
        {
            Log("Upload failed: " + ex.Message);
            _p.Note("Intune upload failed: " + ex.Message);
            PackageBuilder.Finish(_p);
            MessageBox.Show(this, ex.Message, "Upload failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void OnOpenPortal(object sender, RoutedEventArgs e)
    {
        if (_portalUrl != null) Process.Start(new ProcessStartInfo(_portalUrl) { UseShellExecute = true });
    }

    void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            if (MessageBox.Show(this, "An upload or sign-in is in progress. Cancel it and close?", "Upload to Intune", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            _cts?.Cancel();
        }
        base.OnClosing(e);
    }
}
