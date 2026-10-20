using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private WorkspaceStore _workspaceStore = null!;
    private WorkspaceConfiguration _configuration = new();
    private bool _runtimeFeatures;
    private bool _restoringWorkspace;
    private string _dataDirectory = "";
    private LocalWebServer? _webServer;
    private AlertActionDispatcher? _actions;
    private readonly DispatcherTimer _maintenanceClock = new() { Interval = TimeSpan.FromMinutes(30) };
    private Task? _maintenanceTask;
    private readonly SemaphoreSlim _webLifecycle = new(1, 1);
    private WindowNotificationManager? _notifications;
    private readonly ObservableCollection<MonitoringEvent> _monitoringEvents = [];
    private readonly DispatcherTimer _workspaceSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private CancellationTokenSource? _historyQuery;
    private ProbeOptions _draftOptions = new();

    private void InitializeProfessional(string directory, bool runtime)
    {
        _runtimeFeatures = runtime;
        _dataDirectory = directory;
        _workspaceStore = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
        _configuration = new WorkspaceConfiguration
        {
            Profiles = [new("ICMP default", new()), new("ICMP fast", new() { Interval = TimeSpan.FromSeconds(1) }),
                new("TCP 443", new() { Protocol = ProbeProtocol.Tcp, DestinationPort = 443 }),
                new("UDP 33434", new() { Protocol = ProbeProtocol.Udp, DestinationPort = 33434 })]
        };
        ProtocolInput.ItemsSource = Enum.GetValues<ProbeProtocol>();
        ProtocolInput.SelectedIndex = 0;
        var sources = new List<SourceChoice> { new("System route", null, null) };
        try
        {
            foreach (var network in NetworkDiscovery.GetInterfaces())
                foreach (var address in network.Addresses)
                    sources.Add(new($"{network.Name} · {address}", network.Name, address));
        }
        catch (Exception ex) { StatusText.Text = "Interface information unavailable: " + ex.Message; }
        SourceInput.ItemsSource = sources;
        SourceInput.SelectedIndex = 0;
        ReloadProfiles();
        ProtocolInput.SelectionChanged += (_, _) => ProtocolChanged();
        UpdateProtocolControls();
        PortInput.ValueChanged += (_, _) => ApplySelectedOptions();
        UdpModeInput.SelectionChanged += (_, _) => { UpdateProtocolControls(); ApplySelectedOptions(); };
        UdpEndPortInput.ValueChanged += (_, _) => ApplySelectedOptions();
        SourceInput.SelectionChanged += (_, _) => ApplySelectedOptions();
        GroupInput.TextChanged += (_, _) =>
        {
            if (_settingOptions || _selected?.Session is not { } session) return;
            _selected.Group = string.IsNullOrWhiteSpace(GroupInput.Text) ? "Default" : GroupInput.Text.Trim();
            session.Group = _selected.Group;
            _configuration = _configuration with { Groups = TargetGroupCatalog.List(GroupWorkspace()) };
            _selected.Refresh();
            AssignAlerts(_selected); ScheduleWorkspaceSave();
        };
        _workspaceSaveTimer.Tick += async (_, _) =>
        {
            _workspaceSaveTimer.Stop();
            try { await SaveWorkspaceNowAsync(); }
            catch (Exception ex) { StatusText.Text = "Workspace save failed: " + ex.Message; }
        };
        _engine.MonitoringEventOccurred += (_, item) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            _actions?.TryEnqueue(item);
            _monitoringEvents.Insert(0, item);
            while (_monitoringEvents.Count > 500) _monitoringEvents.RemoveAt(_monitoringEvents.Count - 1);
            StatusText.Text = item.Message;
            if (_configuration.DesktopNotifications)
                _notifications?.Show(new Notification("Pngr · " + item.Kind, item.Message,
                    item.Kind == "Recovery" ? NotificationType.Success : NotificationType.Warning, TimeSpan.FromSeconds(8)));
            if (_selected?.Id == item.SessionId) RefreshEventMarkers();
        });
        _maintenanceClock.Tick += (_, _) =>
        {
            if (_closing || _maintenanceTask is { IsCompleted: false }) return;
            _maintenanceTask = MaintainHistoryAsync();
        };
        if (_runtimeFeatures) _maintenanceClock.Start();
    }

    private async Task InitializeProfessionalOnOpenAsync()
    {
        await _engine.Storage.InitializeProfessionalAsync();
        if (!_runtimeFeatures) return;
        _notifications = new WindowNotificationManager(this) { MaxItems = 3, Position = NotificationPosition.TopRight };
        try
        {
            var saved = await _workspaceStore.LoadAsync();
            if (saved is not null)
            {
                _configuration = saved with { Groups = TargetGroupCatalog.List(saved) };
                RestoreMainWindowLayout(saved.WindowLayout);
                ApplyHistoryPanelFraction(saved.HistoryLayout.PanelFraction);
                ApplyPreferenceControls();
                ReloadProfiles();
                if (saved.RestoreOnStartup && !_args.Contains("--target")) await ApplyWorkspaceAsync(saved);
            }
            await Task.Run(() => _engine.Storage.RetainAsync(TimeSpan.FromDays(_configuration.RetentionDays)));
            if (_closing) return;
            if (_configuration.WebEnabled) await EnableWebAsync();
            ConfigureActions();
            if (_workspaceStore.CredentialWarning is { } warning) StatusText.Text = warning;
        }
        catch (Exception ex) { StatusText.Text = "Workspace could not be restored: " + ex.Message; }
    }

    private WorkspaceConfiguration CaptureWorkspace() => _configuration with
    {
        Groups = TargetGroupCatalog.List(GroupWorkspace()),
        HistoryLayout = CaptureHistoryLayout(),
        WindowLayout = CaptureMainWindowLayout(),
        Targets = _targets.Where(x => x.Session?.State is SessionState.Running or SessionState.Paused)
            .Select(x => new WorkspaceTarget(x.Target, x.Session!.Options, x.Group, x.Session.State == SessionState.Paused, x.TcpPort, x.UdpPort, x.ProfileName)).ToArray()
    };
    private async Task SaveWorkspaceNowAsync()
    {
        var saved = await _workspaceStore.SaveAsync(CaptureWorkspace());
        if (_configuration.Actions.Webhook != saved.Actions.Webhook)
        {
            _configuration = _configuration with { Actions = saved.Actions };
            ConfigureActions();
        }
    }
    private void ScheduleWorkspaceSave()
    {
        if (!_runtimeFeatures || _restoringWorkspace || _closing) return;
        _workspaceSaveTimer.Stop(); _workspaceSaveTimer.Start();
    }

    private void AssignAlerts(TargetItem item)
    {
        if (item.Session is null) return;
        var rules = _configuration.Alerts.Where(r =>
            (string.IsNullOrWhiteSpace(r.Target) || r.Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(r.Group) || r.Group == item.Group)).ToArray();
        _engine.SetAlertRules(item.Id, rules);
    }

    private async Task ApplyWorkspaceAsync(WorkspaceConfiguration workspace)
    {
        _applicationCancellation.Token.ThrowIfCancellationRequested();
        workspace.Validate();
        _restoringWorkspace = true;
        try
        {
            foreach (var item in _targets.ToArray()) if (item.Session is not null)
            { await _engine.StopAsync(item.Id); _engine.ForgetSession(item.Id); }
            _targets.Clear(); TargetList.SelectedItem = null; TargetCount.Text = "0";
            _configuration = workspace with { Groups = TargetGroupCatalog.List(workspace) }; RestoreMainWindowLayout(workspace.WindowLayout); _historyViews.Clear(); ApplyHistoryPanelFraction(workspace.HistoryLayout.PanelFraction); ApplyPreferenceControls(); ReloadProfiles();
            foreach (var target in workspace.Targets)
            {
                try
                {
                    var previousCount = _targets.Count;
                    await AddTarget(target.Target, target.Options, target.Group, target.Paused);
                    var restored = _targets.Count > previousCount ? _targets.Last()
                        : _targets.FirstOrDefault(t => t.Target.Equals(target.Target, StringComparison.OrdinalIgnoreCase));
                    if (restored is not null)
                    {
                        restored.TcpPort = target.TcpPort; restored.UdpPort = target.UdpPort;
                        restored.ProfileName = target.ProfileName;
                        RememberTargetPort(restored, target.Options);
                        if (ReferenceEquals(restored, _selected)) UpdateProfessionalSelection();
                    }
                }
                catch (Exception ex) { StatusText.Text = $"Could not restore {target.Target}: {ex.Message}"; }
            }
        }
        finally { _restoringWorkspace = false; }
        if (_closing) return;
        await StopWebAsync();
        if (_configuration.WebEnabled && _runtimeFeatures) await EnableWebAsync();
        ConfigureActions();
        await SaveWorkspaceNowAsync();
    }

    private void UpdateProfessionalSelection()
    {
        CurrentHistoryView.LoadedKey = null;
        _historyQuery?.Cancel();
        if (_selected is null) return;
        SynchronizeProbeControls(_selected.Session?.Options ?? _draftOptions);
        _settingOptions = true;
        try
        {
            GroupInput.Text = _selected.Group;
            ProfileInput.SelectedItem = _selected.ProfileName;
        }
        finally { _settingOptions = false; }
        RefreshEventMarkers();
    }

    private void RefreshEventMarkers()
    {
        if (_selected is null) { Timeline.Markers = []; return; }
        Timeline.Markers = _monitoringEvents.Where(e => e.SessionId == _selected.Id)
            .Select(e => new GraphEvent(e.Timestamp, e.Kind, e.Message)).ToArray();
    }

    private void ReloadProfiles()
    {
        _settingOptions = true;
        try { ProfileInput.ItemsSource = _configuration.Profiles.Select(p => p.Name).ToArray(); ProfileInput.SelectedItem = !_creatingTarget && _selected is not null ? _selected.ProfileName : _configuration.LastUsedProfile; }
        finally { _settingOptions = false; }
    }

    private void ApplyProfile(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingOptions || ProfileInput.SelectedItem is not string name) return;
        var profile = _configuration.Profiles.FirstOrDefault(p => p.Name == name);
        if (profile is null) return;
        if (!_creatingTarget && _selected is not null) _selected.ProfileName = name;
        _draftOptions = profile.Options;
        SynchronizeProbeControls(profile.Options);
        if (!_creatingTarget && _selected?.Session is { } session) _engine.UpdateOptions(session.Id, profile.Options);
        ApplySelectedOptions();
    }

    private async void SaveProfile(object? sender, RoutedEventArgs e)
    {
        var name = new TextBox { Watermark = "Profile name", Text = "My profile", MaxLength = 80 };
        var dialog = ToolWindow("Save probing profile", 430, 170);
        var save = new Button { Content = "Save profile" };
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            try
            {
                var profiles = _configuration.Profiles.Where(p => p.Name != name.Text.Trim()).Append(new MonitoringProfile(name.Text.Trim(), ReadOptions())).ToArray();
                var configuration = _configuration with { Profiles = profiles }; configuration.Validate();
                _configuration = configuration;
                ReloadProfiles(); await SaveWorkspaceNowAsync(); dialog.Close();
            }
            catch (Exception ex) { StatusText.Text = "Could not save profile: " + ex.Message; }
        };
        dialog.Content = ToolPanel(new TextBlock { Text = "Store the current protocol, port, source and timing settings." }, name, save);
        await dialog.ShowDialog(this);
    }

    private void OpenSummary(object? sender, RoutedEventArgs e)
    {
        var summary = new SummaryWindow(() => _targets.ToArray(), item => TargetList.SelectedItem = item,
            _configuration.SummaryViews, definitions =>
            { _configuration = _configuration with { SummaryViews = definitions }; ScheduleWorkspaceSave(); },
            () => TargetGroupCatalog.List(GroupWorkspace()));
        summary.Show(this);
    }

    private async void OpenNetwork(object? sender, RoutedEventArgs e)
        => await new NetworkInfoWindow(target => AddTarget(target)).ShowDialog(this);

    private Window? _workspaceWindow;

    private async void OpenWorkspace(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_workspaceWindow is { IsVisible: true }) { _workspaceWindow.Activate(); return; }
        var dialog = ToolWindow("Workspaces and Data", 510, 400);
        _workspaceWindow = dialog;
        dialog.MinWidth = 450; dialog.MinHeight = 400;
        SettingsUi.Apply(dialog);
        var save = new Button { Content = "Save workspace and preferences" };
        var compact = new Button { Content = "Compact database…" };
        var export = new Button { Content = "Export configuration…" };
        var import = new Button { Content = "Import configuration…" };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        save.Click += async (_, _) =>
        {
            try
            {
                await SaveWorkspaceNowAsync();
                var deleted = await Task.Run(() => _engine.Storage.RetainAsync(TimeSpan.FromDays(_configuration.RetentionDays)));
                CurrentHistoryView.LoadedKey = null;
                status.Text = $"Workspace saved. Removed {deleted:N0} samples older than retention.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        export.Click += async (_, _) =>
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export Pngr workspace",
                    SuggestedFileName = "Pngr.workspace.json", DefaultExtension = "json" });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync(); if (stream.CanSeek) stream.SetLength(0);
                await WorkspaceStore.ExportAsync(stream, CaptureWorkspace());
                status.Text = "Configuration exported.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        import.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Pngr workspace", AllowMultiple = false });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                var workspace = await WorkspaceStore.ImportAsync(stream);
                // Imported files may name arbitrary executables or remote recipients. Only saved,
                // locally enabled actions resume automatically; imports require explicit re-enabling.
                workspace = workspace with { Actions = workspace.Actions with
                { NativeNotificationsEnabled = false, Smtp = workspace.Actions.Smtp with { Enabled = false },
                    Webhook = workspace.Actions.Webhook with { Enabled = false }, Script = workspace.Actions.Script with { Enabled = false } } };
                await ApplyWorkspaceAsync(workspace); status.Text = $"Loaded {workspace.Targets.Count} targets. Imported actions are disabled; enable them explicitly in Alerts.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        compact.Click += async (_, _) =>
        {
            compact.IsEnabled = false;
            try
            {
                var path = _engine.Storage.DatabasePath;
                var before = new FileInfo(path).Length;
                status.Text = "Compacting stored history; measurements resume writing when finished…";
                await Task.Run(() => _engine.Storage.CompactAsync(_applicationCancellation.Token));
                status.Text = $"Database compacted: {before / 1048576d:0.0} → {new FileInfo(path).Length / 1048576d:0.0} MiB.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { compact.IsEnabled = true; }
        };
        save.Name = "WorkspaceSave"; export.Name = "WorkspaceExport"; import.Name = "WorkspaceImport";
        compact.Name = "WorkspaceCompact"; status.Name = "WorkspaceStatus";
        save.HorizontalAlignment = compact.HorizontalAlignment = HorizontalAlignment.Left;
        var preferences = new Button { Name = "WorkspaceSettings", Content = "Startup & history settings…", HorizontalAlignment = HorizontalAlignment.Left };
        preferences.Click += (_, _) => { dialog.Close(); OpenSettings(null, e); _settingsWindow?.SelectPage("History"); };
        var close = new Button { Name = "WorkspaceClose", Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        var body = SettingsUi.Page(
            SettingsUi.Section("Workspace", save, SettingsUi.Actions(export, import)), SettingsUi.Divider(),
            SettingsUi.Section("History & storage", compact), SettingsUi.Divider(),
            SettingsUi.Section("Preferences", SettingsUi.Note("Startup restoration is in General. Retention and graph aggregation are in History."), preferences));
        var layout = new Grid { Margin = new Thickness(20, 16), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10 };
        layout.Children.Add(new ScrollViewer { Content = body });
        Grid.SetRow(status, 1); layout.Children.Add(status); Grid.SetRow(close, 2); layout.Children.Add(close);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
        if (ReferenceEquals(_workspaceWindow, dialog)) _workspaceWindow = null;
    }

    private void OpenAlerts(object? sender, RoutedEventArgs e)
    {
        OpenSettings(sender, e);
        _settingsWindow?.SelectPage("Alerts & notifications");
    }

    private async Task EnableWebAsync()
    {
        await _webLifecycle.WaitAsync(_applicationCancellation.Token);
        try { await StartWebServerAsync(); }
        finally { _webLifecycle.Release(); }
    }

    private async Task StartWebServerAsync()
    {
        _applicationCancellation.Token.ThrowIfCancellationRequested();
        if (_webServer?.Url is not null) return;
        if (_webServer is not null) { await _webServer.DisposeAsync(); _webServer = null; }
        var configuration = await ResolveWebInterfaceAsync(_configuration);
        var server = new LocalWebServer(_engine);
        try
        {
            await server.StartAsync(WebOptions(configuration), _applicationCancellation.Token);
            _webServer = server; _configuration = configuration;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private async Task StopWebAsync()
    {
        await _webLifecycle.WaitAsync();
        try
        {
            if (_webServer is not null)
            {
                var server = _webServer; _webServer = null;
                await server.DisposeAsync();
            }
        }
        finally { _webLifecycle.Release(); }
    }

    private async Task ConfigureWebAsync(bool enabled, int port)
    {
        await CommitSettingsAsync(_configuration with { WebPort = port, WebEnabled = enabled });
    }

    private async void OpenWeb(object? sender, RoutedEventArgs e)
    {
        var dialog = new WebDashboardWindow(() => (_webServer?.Url, _webServer?.LastError, _configuration.WebLanEnabled), () =>
        {
            OpenSettings(null, e);
            _settingsWindow?.SelectPage("Web Server");
        });
        await dialog.ShowDialog(this);
    }

    private static Window ToolWindow(string title, double width, double height) => new()
    { Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    private static StackPanel ToolPanel(params Control[] controls)
    {
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static Grid FormRow(string title, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    private sealed record SourceChoice(string Label, string? Interface, string? Source)
    { public override string ToString() => Label; }
}
