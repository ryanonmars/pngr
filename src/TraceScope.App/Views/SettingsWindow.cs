using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed record ProbeSourceChoice(string Label, string? Interface, string? Address)
{
    public override string ToString() => Label;
}

public sealed record SettingsChange(WorkspaceConfiguration Configuration, ProbeOptions? SelectedTargetOptions,
    int? SelectedTcpPort = null, int? SelectedUdpPort = null);

/// <summary>Edits a private draft. Only Apply/OK can change running monitoring or saved preferences.</summary>
public sealed class SettingsWindow : Window
{
    private readonly WorkspaceConfiguration _original;
    private readonly Func<SettingsChange, Task> _apply;
    private readonly Func<Task<NotificationResult>> _permission;
    private readonly Action<string> _openTool;
    private readonly TextBlock _status = new() { Name = "SettingsStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _applyButton = new() { Name = "SettingsApply", Content = "Apply", MinWidth = 78 };
    private readonly Button _okButton = new() { Name = "SettingsOk", Content = "OK", MinWidth = 78, Classes = { "primary" } };
    private readonly Button _cancelButton = new() { Name = "SettingsCancel", Content = "Cancel", MinWidth = 78 };
    private readonly ContentControl _page = new();
    private readonly ListBox _navigation;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly ComboBox _scope = new() { Name = "ProbeScope", Width = 320 };
    private readonly ComboBox _protocol = new() { Name = "SettingsProtocol", Width = 160, ItemsSource = Enum.GetValues<ProbeProtocol>() };
    private readonly ComboBox _source = new() { Name = "SettingsSource", MinWidth = 310, MaxWidth = 440 };
    private readonly NumericUpDown _port = Number("SettingsDestinationPort", 1, 65535);
    private readonly NumericUpDown _interval = Number("SettingsInterval", .1m, 3600, .1m);
    private readonly NumericUpDown _timeout = Number("SettingsTimeout", .1m, 30, .1m);
    private readonly NumericUpDown _hops = Number("SettingsMaxHops", 1, 64);
    private readonly NumericUpDown _concurrency = Number("SettingsConcurrency", 1, 32);
    private readonly NumericUpDown _memory = Number("SettingsHistoryLimit", 10, 100000, 100);
    private readonly CheckBox _resolve = Toggle("SettingsResolveHostnames", "Resolve router hostnames");
    private readonly CheckBox _useDefaults = Toggle("SettingsUseDefaults", "Use saved defaults for every new target");
    private readonly CheckBox _restore = Toggle("SettingsRestore", "Restore monitored targets and paused states at startup");
    private readonly CheckBox _toasts = Toggle("SettingsInAppNotifications", "Show alerts inside TraceScope");
    private readonly CheckBox _native = Toggle("SettingsNativeNotifications", "Enable native desktop notifications");
    private readonly CheckBox _recoveries = Toggle("SettingsIncludeRecovery", "Notify when a connection recovers");
    private readonly NumericUpDown _spacing = Number("SettingsNotificationSpacing", 0, 86400);
    private readonly NumericUpDown _warning = Number("SettingsWarningLatency", 1, 59999);
    private readonly NumericUpDown _critical = Number("SettingsCriticalLatency", 2, 60000);
    private readonly NumericUpDown _maximum = Number("SettingsGraphMaximum", 0, 60000);
    private readonly CheckBox _jitter = Toggle("SettingsShowJitter", "Show jitter graph");
    private readonly CheckBox _loss = Toggle("SettingsShowPacketLoss", "Show packet-loss bars and scale");
    private readonly CheckBox _events = Toggle("SettingsShowEvents", "Show alert, recovery and route-change markers");
    private readonly ComboBox _range = new() { Name = "SettingsTimeRange", Width = 180,
        ItemsSource = new[] { "1 minute", "5 minutes", "10 minutes", "15 minutes", "1 hour", "6 hours", "1 day", "1 week" } };
    private readonly NumericUpDown _retention = Number("SettingsRetention", 1, 3650);
    private readonly NumericUpDown _aggregation = Number("SettingsAggregation", 1, 3600);
    private readonly CheckBox _web = Toggle("SettingsWebEnabled", "Enable local read-only dashboard and REST API");
    private readonly NumericUpDown _webPort = Number("SettingsWebPort", 1024, 65535);
    private readonly ComboBox _profiles = new() { Name = "SettingsProfiles", Width = 250 };
    private readonly TextBox _profileName = new() { Name = "SettingsProfileName", Watermark = "Profile name", Width = 220, MaxLength = 80 };
    private List<MonitoringProfile> _profileDraft;
    private ProbeOptions _defaults;
    private ProbeOptions? _selected;
    private readonly ProbeOptions? _initialSelected;
    private int _editingScope;
    private bool _loading;
    private bool _busy;
    private ProbeProtocol _portProtocol;
    private readonly Dictionary<int, (int Tcp, int Udp)> _ports = [];

    public SettingsWindow(WorkspaceConfiguration configuration, ProbeOptions? selectedOptions, string? selectedTarget,
        IReadOnlyList<ProbeSourceChoice> sources, string dataDirectory, Func<SettingsChange, Task> apply,
        Func<Task<NotificationResult>> permission, Action<string> openTool,
        int selectedTcpPort = 443, int selectedUdpPort = 33434)
    {
        _original = configuration;
        _defaults = configuration.DefaultProbeOptions;
        _ports[0] = (configuration.LastTcpPort, configuration.LastUdpPort);
        _ports[1] = (selectedTcpPort, selectedUdpPort);
        _selected = selectedOptions;
        _initialSelected = selectedOptions;
        _profileDraft = configuration.Profiles.ToList();
        _apply = apply; _permission = permission; _openTool = openTool;
        Title = "TraceScope Settings"; Width = 830; Height = 650; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _source.ItemsSource = sources.ToList();
        _scope.ItemsSource = selectedOptions is null ? new[] { "Defaults for new targets" }
            : new[] { "Defaults for new targets", $"Selected target: {selectedTarget}" };
        _scope.SelectedIndex = 0;
        _restore.IsChecked = configuration.RestoreOnStartup;
        _useDefaults.IsChecked = configuration.UseDefaultProbeOptionsForNewTargets;
        _toasts.IsChecked = configuration.DesktopNotifications;
        _native.IsChecked = configuration.Actions.NativeNotificationsEnabled;
        _recoveries.IsChecked = configuration.Actions.IncludeRecovery;
        _spacing.Value = configuration.Actions.MinimumNotificationSpacingSeconds;
        _warning.Value = (decimal)configuration.Display.WarningLatencyMs;
        _critical.Value = (decimal)configuration.Display.CriticalLatencyMs;
        _maximum.Value = (decimal)configuration.Display.FixedMaximumMs;
        _range.SelectedIndex = Array.IndexOf(TimeRanges, configuration.Display.DefaultTimeRangeMinutes);
        _jitter.IsChecked = configuration.Display.ShowJitter;
        _loss.IsChecked = configuration.Display.ShowPacketLoss;
        _events.IsChecked = configuration.Display.ShowEventMarkers;
        _retention.Value = configuration.RetentionDays; _aggregation.Value = configuration.AggregationSeconds;
        _web.IsChecked = configuration.WebEnabled; _webPort.Value = configuration.WebPort;
        LoadProbe(_defaults); ReloadProfiles();

        _pages["General"] = Page("General", "Preferences are saved locally with your workspace.", _restore,
            Note("Monitoring, storage, alerts and the optional API run inside this desktop application. Closing TraceScope stops them."),
            ToolButton("Apply & manage workspaces…", "workspace"),
            Heading("Local files"), Note(dataDirectory), Note("Settings: workspace.json   ·   Measurements: tracescope.db"));
        _pages["Monitoring"] = Page("Monitoring engine", "Edit defaults or the selected target. Existing targets keep their own settings.",
            _useDefaults, Note("When unchecked, new targets inherit the last-used probing configuration."),
            Row("Configuration scope", _scope), Row("Packet type", _protocol), Row("Destination port", _port),
            Row("Network source", _source), Row("Interval (seconds)", _interval), Row("Timeout (seconds)", _timeout),
            Row("Maximum hops", _hops), Row("Concurrent hop probes", _concurrency), Row("In-memory samples / hop", _memory), _resolve,
            Note("Port applies to TCP / UDP. macOS ICMP traceroute uses a minimum one-second timeout. Stored history is independent of the memory limit."),
            Heading("Saved monitoring profiles"), Row("Load profile", _profiles),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _profileName, ProfileButton("Save", "SettingsSaveProfile", SaveProfile), ProfileButton("Delete", "SettingsDeleteProfile", DeleteProfile) } },
            Note("Profiles are staged until Apply or OK. Loading a profile changes only the configuration scope above."));
        _pages["Display"] = Page("Display and graphs", "Settings affect the integrated hop plot and historical timeline.",
            Row("Warning latency (ms)", _warning), Row("Critical latency (ms)", _critical),
            Row("Graph maximum (ms)", _maximum), Note("Use 0 for automatic scaling. Warning must be lower than critical."),
            Row("Default time range", _range), _jitter, _loss, _events);
        _pages["History"] = Page("History and storage", "Real measurements are saved automatically to the local SQLite database.",
            Row("Retention (days)", _retention), Row("Graph aggregation (seconds)", _aggregation),
            Note("Retention is enforced at startup and every 30 minutes. Aggregation controls historical graph queries; raw retained samples remain available for CSV export."),
            ToolButton("Apply & open data maintenance…", "workspace"), Heading("Data location"), Note(dataDirectory));
        var permissionButton = new Button { Name = "SettingsNotificationPermission", Content = "Request OS notification permission…" };
        permissionButton.Click += async (_, _) =>
        {
            if (_busy) return;
            try { permissionButton.IsEnabled = false; _status.Text = (await _permission()).Message; }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { permissionButton.IsEnabled = true; }
        };
        _pages["Alerts & notifications"] = Page("Alerts and notifications", "Alerts use measured latency, loss, jitter, reachability and route changes.",
            _toasts, _native, _recoveries, Row("Notification spacing (seconds)", _spacing), permissionButton,
            Note("OS permission is requested only when you press the button. Native notifications also require the enable checkbox."),
            ToolButton("Apply & edit alert rules / history…", "alerts"),
            ToolButton("Apply & configure email, webhooks and scripts…", "actions"),
            Note("Email, webhooks and executable actions stay disabled until explicitly configured in the action editor."));
        _pages["Local web / API"] = Page("Local web interface and API", "Optional access from a browser on this computer. Disabled by default.",
            _web, Row("Local port", _webPort), Note("Address: http://127.0.0.1:<port>   ·   Read-only REST endpoints: /api/targets, /api/history/<session-id>"),
            Note("Apply starts or stops the listener immediately. No separate server is installed. The listener closes completely when TraceScope exits."),
            ToolButton("Apply & open local dashboard controls…", "web"));
        var nav = new ListBox { Name = "SettingsNavigation", ItemsSource = _pages.Keys.ToArray(), SelectedIndex = 0, Background = Brushes.Transparent };
        _navigation = nav;
        nav.SelectionChanged += (_, _) => { if (nav.SelectedItem is string name) _page.Content = _pages[name]; };
        _page.Content = _pages["General"];
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("175,*") };
        body.Children.Add(new Border { Background = Brush.Parse("#22242A"), Padding = new Thickness(7, 12), Child = nav });
        var content = new ScrollViewer { Content = _page, Margin = new Thickness(22, 16) };
        Grid.SetColumn(content, 1); body.Children.Add(content);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 10) };
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _okButton, _cancelButton, _applyButton } };
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = Brush.Parse("#191A1D") };
        layout.Children.Add(body); Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        _scope.SelectionChanged += (_, _) =>
        {
            if (_loading || _scope.SelectedIndex == _editingScope) return;
            try { CaptureProbe(); _editingScope = _scope.SelectedIndex; LoadProbe(_editingScope == 0 ? _defaults : _selected!); }
            catch (Exception ex) { _status.Text = ex.Message; _loading = true; _scope.SelectedIndex = _editingScope; _loading = false; }
        };
        _protocol.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            RememberPort();
            var ports = _ports[_editingScope];
            var next = (ProbeProtocol)_protocol.SelectedItem!;
            _port.Value = next == ProbeProtocol.Udp ? ports.Udp : next == ProbeProtocol.Tcp ? ports.Tcp : _port.Value;
            _portProtocol = next;
            _port.IsEnabled = next != ProbeProtocol.Icmp;
        };
        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is string name && _profileDraft.FirstOrDefault(p => p.Name == name) is { } profile)
            { LoadProbe(profile.Options); _profileName.Text = name; }
        };
        _applyButton.Click += async (_, _) => await SaveAsync(false);
        _okButton.Click += async (_, _) => await SaveAsync(true);
        _cancelButton.Click += (_, _) => Close();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) { Close(); e.Handled = true; } };
    }

    public static int[] TimeRanges { get; } = [1, 5, 10, 15, 60, 360, 1440, 10080];
    public void SelectPage(string name) => _navigation.SelectedItem = name;

    private void CaptureProbe()
    {
        RememberPort();
        var options = ReadProbe();
        // Reuse the same validation as workspace import and the monitoring engine.
        (_original with { DefaultProbeOptions = options }).Validate();
        if (_editingScope == 0) _defaults = options; else _selected = options;
    }
    private void RememberPort()
    {
        var ports = _ports[_editingScope];
        var port = (int)(_port.Value ?? 443);
        _ports[_editingScope] = _portProtocol == ProbeProtocol.Tcp ? (port, ports.Udp)
            : _portProtocol == ProbeProtocol.Udp ? (ports.Tcp, port) : ports;
    }
    private ProbeOptions ReadProbe()
    {
        var basis = _editingScope == 0 ? _defaults : _selected!;
        var source = _source.SelectedItem as ProbeSourceChoice;
        return basis with
        {
            Protocol = _protocol.SelectedItem is ProbeProtocol p ? p : throw new InvalidDataException("Select a packet type."),
            DestinationPort = (int)Value(_port), SourceAddress = source?.Address, InterfaceId = source?.Interface,
            Interval = TimeSpan.FromSeconds((double)Value(_interval)), Timeout = TimeSpan.FromSeconds((double)Value(_timeout)),
            MaxHops = (int)Value(_hops), Concurrency = (int)Value(_concurrency), HistoryLimit = (int)Value(_memory), ResolveHostnames = _resolve.IsChecked == true
        };
    }
    private void LoadProbe(ProbeOptions options)
    {
        _loading = true;
        try
        {
            _protocol.SelectedItem = options.Protocol; _port.Value = options.DestinationPort;
            _portProtocol = options.Protocol;
            _port.IsEnabled = options.Protocol != ProbeProtocol.Icmp;
            RememberPort();
            var sources = _source.ItemsSource!.Cast<ProbeSourceChoice>().ToList();
            var source = sources.FirstOrDefault(s => s.Address == options.SourceAddress && s.Interface == options.InterfaceId);
            if (source is null)
            {
                source = new ProbeSourceChoice($"Saved source (unavailable): {options.SourceAddress}", options.InterfaceId, options.SourceAddress);
                sources.Add(source); _source.ItemsSource = sources;
            }
            _source.SelectedItem = source;
            _interval.Value = (decimal)options.Interval.TotalSeconds; _timeout.Value = (decimal)options.Timeout.TotalSeconds;
            _hops.Value = options.MaxHops; _concurrency.Value = options.Concurrency; _memory.Value = options.HistoryLimit; _resolve.IsChecked = options.ResolveHostnames;
        }
        finally { _loading = false; }
    }
    private async Task<bool> SaveAsync(bool close)
    {
        if (_busy) return false;
        try
        {
            CaptureProbe();
            var configuration = _original with
            {
                DefaultProbeOptions = _defaults, Profiles = _profileDraft.ToArray(), RestoreOnStartup = _restore.IsChecked == true,
                UseDefaultProbeOptionsForNewTargets = _useDefaults.IsChecked == true,
                RetentionDays = (int)Value(_retention), AggregationSeconds = (int)Value(_aggregation),
                DesktopNotifications = _toasts.IsChecked == true, WebEnabled = _web.IsChecked == true, WebPort = (int)Value(_webPort),
                Actions = _original.Actions with { NativeNotificationsEnabled = _native.IsChecked == true, IncludeRecovery = _recoveries.IsChecked == true,
                    MinimumNotificationSpacingSeconds = (int)Value(_spacing) },
                Display = new DisplayConfiguration { WarningLatencyMs = (double)Value(_warning), CriticalLatencyMs = (double)Value(_critical),
                    FixedMaximumMs = (double)Value(_maximum), DefaultTimeRangeMinutes = TimeRanges[Math.Clamp(_range.SelectedIndex, 0, 7)],
                    ShowJitter = _jitter.IsChecked == true, ShowPacketLoss = _loss.IsChecked == true, ShowEventMarkers = _events.IsChecked == true }
            };
            configuration.Validate();
            _busy = true; _applyButton.IsEnabled = _okButton.IsEnabled = _cancelButton.IsEnabled = false;
            await _apply(new SettingsChange(configuration, _selected != _initialSelected ? _selected : null,
                _selected != _initialSelected ? _ports[1].Tcp : null,
                _selected != _initialSelected ? _ports[1].Udp : null));
            _status.Text = "Settings saved. Changes apply immediately; monitoring continues.";
            _busy = false;
            if (close) Close();
            return true;
        }
        catch (Exception ex) { _status.Text = "Settings were not applied: " + ex.Message; return false; }
        finally { _busy = false; _applyButton.IsEnabled = _okButton.IsEnabled = _cancelButton.IsEnabled = true; }
    }
    private void SaveProfile()
    {
        try
        {
            CaptureProbe(); var name = _profileName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Enter a profile name.");
            var profile = new MonitoringProfile(name, ReadProbe());
            var next = _profileDraft.Where(p => p.Name != name).Append(profile).ToList();
            (_original with { Profiles = next }).Validate(); _profileDraft = next; ReloadProfiles();
            _status.Text = $"Profile '{name}' staged. Press Apply or OK to save.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void DeleteProfile()
    {
        if (_profiles.SelectedItem is not string name) return;
        _profileDraft.RemoveAll(p => p.Name == name); ReloadProfiles();
        _status.Text = $"Profile '{name}' removed from the draft. Apply to save.";
    }
    private void ReloadProfiles()
    { _loading = true; _profiles.ItemsSource = _profileDraft.Select(p => p.Name).ToArray(); _profiles.SelectedIndex = -1; _loading = false; }
    private Button ToolButton(string label, string tool)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { if (await SaveAsync(true)) _openTool(tool); };
        return button;
    }
    private static Button ProfileButton(string text, string name, Action action)
    { var button = new Button { Content = text, Name = name }; button.Click += (_, _) => action(); return button; }
    private static NumericUpDown Number(string name, decimal minimum, decimal maximum, decimal increment = 1)
        => new() { Name = name, Minimum = minimum, Maximum = maximum, Increment = increment, Width = 165, FormatString = increment < 1 ? "0.###" : "0" };
    private static decimal Value(NumericUpDown input) => input.Value ?? throw new InvalidDataException($"A value is required for {input.Name}.");
    private static CheckBox Toggle(string name, string label) => new() { Name = name, Content = label };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A8ADB6"), LineHeight = 17 };
    private static StackPanel Page(string title, string description, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 13 }; panel.Children.Add(Heading(title)); panel.Children.Add(Note(description));
        foreach (var control in controls) panel.Children.Add(control); return panel;
    }
    private static Grid Row(string label, Control input)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), ColumnSpacing = 10 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        input.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(input, 1); row.Children.Add(input); return row;
    }
}
