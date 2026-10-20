using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.Core;
using TraceScope.App.Controls;

namespace TraceScope.App.Views;

public sealed record ProbeSourceChoice(string Label, string? Interface, string? Address)
{
    public override string ToString() => Label;
}

public sealed record SettingsChange(WorkspaceConfiguration Configuration, ProbeOptions? SelectedTargetOptions,
    int? SelectedTcpPort = null, int? SelectedUdpPort = null, bool ReevaluateWebInterface = false, bool AlertRulesChanged = false);

/// <summary>Edits a private draft. Only Apply/OK can change running monitoring or saved preferences.</summary>
public sealed partial class SettingsWindow : Window
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
    private readonly AlertSettingsView _alertRules;
    public AlertSettingsView AlertRulesEditor => _alertRules;
    private readonly ListBox _navigation;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly ComboBox _scope = new() { Name = "ProbeScope", Width = 320 };
    private readonly ComboBox _protocol = new() { Name = "SettingsProtocol", Width = 160, ItemsSource = Enum.GetValues<ProbeProtocol>() };
    private readonly ComboBox _source = new() { Name = "SettingsSource", MinWidth = 310, MaxWidth = 440 };
    private readonly SettingsNumberField _port = Number("SettingsDestinationPort", 1, 65535);
    private readonly ComboBox _udpMode = new() { Name = "SettingsUdpPortMode", Width = 165, ItemsSource = new[] { "Fixed port", "Port range" } };
    private readonly SettingsNumberField _udpEndPort = Number("SettingsUdpEndPort", 1, 65535);
    private readonly SettingsNumberField _udpSize = Number("SettingsUdpPacketSize", 56, 65535);
    private readonly CheckBox _udpFragmentation = Toggle("SettingsUdpAllowFragmentation", "Allow UDP IP fragmentation (path MTU may still restrict delivery)");
    private readonly SettingsNumberField _interval = Number("SettingsInterval", .1m, 3600, .1m);
    private readonly SettingsNumberField _timeout = Number("SettingsTimeout", .1m, 30, .1m);
    private readonly SettingsNumberField _hops = Number("SettingsMaxHops", 1, 64);
    private readonly SettingsNumberField _concurrency = Number("SettingsConcurrency", 1, 32);
    private readonly SettingsNumberField _memory = Number("SettingsHistoryLimit", 10, 100000, 100);
    private readonly CheckBox _resolve = Toggle("SettingsResolveHostnames", "Resolve router hostnames");
    private readonly CheckBox _useDefaults = Toggle("SettingsUseDefaults", "Use saved defaults for every new target");
    private readonly CheckBox _restore = Toggle("SettingsRestore", "Restore monitored targets and paused states at startup");
    private readonly CheckBox _toasts = Toggle("SettingsInAppNotifications", "Show alerts inside Pngr");
    private readonly CheckBox _native = Toggle("SettingsNativeNotifications", "Show desktop notifications");
    private readonly CheckBox _recoveries = Toggle("SettingsIncludeRecovery", "Notify when a connection recovers");
    private readonly SettingsNumberField _spacing = Number("SettingsNotificationSpacing", 0, 86400);
    private readonly SettingsNumberField _warning = Number("SettingsWarningLatency", 1, 59999, fractional: true);
    private readonly SettingsNumberField _critical = Number("SettingsCriticalLatency", 2, 60000, fractional: true);
    private readonly SettingsNumberField _maximum = Number("SettingsGraphMaximum", 0, 60000, fractional: true);
    private readonly CheckBox _jitter = Toggle("SettingsShowJitter", "Show jitter graph");
    private readonly CheckBox _loss = Toggle("SettingsShowPacketLoss", "Show packet-loss bars and scale");
    private readonly CheckBox _events = Toggle("SettingsShowEvents", "Show alert, recovery and route-change markers");
    private readonly ComboBox _range = new() { Name = "SettingsTimeRange", Width = 180,
        ItemsSource = new[] { "1 minute", "5 minutes", "10 minutes", "15 minutes", "1 hour", "6 hours", "1 day", "1 week" } };
    private readonly SettingsNumberField _retention = Number("SettingsRetention", 1, 3650);
    private readonly SettingsNumberField _aggregation = Number("SettingsAggregation", 1, 3600);
    private readonly CheckBox _web = Toggle("SettingsWebEnabled", "Enable Web Server");
    private readonly SettingsNumberField _webPort = Number("SettingsWebPort", 1024, 65535);
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
        int selectedTcpPort = 443, int selectedUdpPort = 33434, Func<string?>? serverAddress = null, Func<string?>? serverError = null,
        Func<CancellationToken, Task<WebServerAddress?>>? automaticAddress = null,
        Func<WebhookAlertConfiguration>? savedWebhook = null,
        Func<WebhookAlertConfiguration, Task<NotificationResult>>? testWebhook = null,
        Func<WebhookAlertConfiguration, Func<TelegramPairingPrompt, Task>, CancellationToken, Task<TelegramPairingResult>>? pairTelegram = null,
        Action<string>? openTelegram = null,
        IEnumerable<MonitoringEvent>? alertEvents = null, IEnumerable<string>? alertTargets = null,
        IEnumerable<string>? alertGroups = null, Func<Guid, string>? eventTarget = null,
        Func<MonitoringEvent, Task<bool>>? acknowledgeEvent = null)
    {
        _original = configuration;
        _pairTelegram = pairTelegram;
        _openTelegram = openTelegram;
        _defaults = configuration.DefaultProbeOptions;
        _ports[0] = (configuration.LastTcpPort, configuration.LastUdpPort);
        _ports[1] = (selectedTcpPort, selectedUdpPort);
        _selected = selectedOptions;
        _initialSelected = selectedOptions;
        _profileDraft = configuration.Profiles.ToList();
        _apply = apply; _permission = permission; _openTool = openTool;
        Title = "Pngr Settings"; Width = 830; Height = 650; MinWidth = 760; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SettingsUi.Apply(this);
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
        _warning.SetDouble(configuration.Display.WarningLatencyMs);
        _critical.SetDouble(configuration.Display.CriticalLatencyMs);
        _maximum.SetDouble(configuration.Display.FixedMaximumMs);
        _range.SelectedIndex = Array.IndexOf(TimeRanges, configuration.Display.DefaultTimeRangeMinutes);
        _jitter.IsChecked = configuration.Display.ShowJitter;
        _loss.IsChecked = configuration.Display.ShowPacketLoss;
        _events.IsChecked = configuration.Display.ShowEventMarkers;
        _retention.Value = configuration.RetentionDays; _aggregation.Value = configuration.AggregationSeconds;
        _web.IsChecked = configuration.WebEnabled; _webPort.Value = configuration.WebPort;
        InitializeWebSettings(configuration, serverAddress, serverError, automaticAddress);
        LoadProbe(_defaults); ReloadProfiles();


        _pages["General"] = BuildGeneralPage(dataDirectory);
        _pages["Monitoring"] = BuildMonitoringPage();
        _pages["Display"] = BuildDisplayPage(configuration.Display);
        _pages["History"] = BuildHistoryPage(dataDirectory);
        UpdateUdpControls();
        var permissionButton = new Button { Name = "SettingsNotificationPermission", Content = "Request OS notification permission…", VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
        permissionButton.Click += async (_, _) =>
        {
            if (_busy) return;
            try { permissionButton.IsEnabled = false; _status.Text = (await _permission()).Message; }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { permissionButton.IsEnabled = true; }
        };
        var configureNotifications = new Button { Name = "SettingsConfigureNotifications", Content = "Configure Notifications…", HorizontalAlignment = HorizontalAlignment.Left };
        configureNotifications.Click += (_, _) => SelectPage("Notification actions");
        var notificationToggles = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 4, Children = { _toasts, _native } };
        var notifications = new StackPanel { Spacing = 7, Children = { new TextBlock { Text = "Notifications", FontSize = 14, FontWeight = FontWeight.SemiBold }, notificationToggles, configureNotifications } };
        _alertRules = new AlertSettingsView(configuration.Alerts, alertTargets ?? configuration.Targets.Select(t => t.Target),
            alertGroups ?? TargetGroupCatalog.List(configuration), notifications, acknowledgeEvent, eventTarget);
        _alertRules.Margin = new Thickness(0, 0, 8, 0);
        _alertRules.SetEvents(alertEvents ?? []);
        if (alertEvents is System.Collections.Specialized.INotifyCollectionChanged changingEvents)
        {
            void RefreshEvents(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
                => _alertRules.SetEvents(alertEvents);
            changingEvents.CollectionChanged += RefreshEvents;
            Closed += (_, _) => changingEvents.CollectionChanged -= RefreshEvents;
        }
        _pages["Alerts & notifications"] = _alertRules;
        var advancedActions = ToolButton("Apply & configure email and executable actions…", "actions");
        advancedActions.Name = "SettingsConfigureAdvancedActions";
        var backToAlerts = new Button { Name = "SettingsBackToAlerts", Content = "Back to alert rules", HorizontalAlignment = HorizontalAlignment.Left };
        backToAlerts.Click += (_, _) => SelectPage("Alerts & notifications");
        _pages["Notification actions"] = SettingsUi.Page(
            SettingsUi.Section("General notifications",
                SettingsUi.Actions(_recoveries, backToAlerts),
                SettingsUi.Columns(SettingsUi.Field("Minimum notification spacing", _spacing, "seconds"), permissionButton)),
            SettingsUi.Divider(), BuildWebhookSettings(configuration.Actions.Webhook, savedWebhook, testWebhook),
            SettingsUi.Divider(), SettingsUi.Section("Advanced actions", advancedActions));
        ToolTip.SetTip(_spacing, "Minimum spacing between native desktop notifications. Recovery notification filtering is configured above.");
        _pages["Web Server"] = BuildWebPage();
        var nav = new ListBox { Name = "SettingsNavigation", ItemsSource = _pages.Keys.ToArray(), SelectedIndex = 0, Background = Brushes.Transparent };
        _navigation = nav;
        nav.SelectionChanged += (_, _) => { if (nav.SelectedItem is string name) _page.Content = _pages[name]; };
        _page.Content = _pages["General"];
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("175,*") };
        body.Children.Add(new Border { Background = Brush.Parse("#22242A"), Padding = new Thickness(7, 12), Child = nav });
        var content = new ScrollViewer { Name = "SettingsPageScroll", Content = _page, Margin = new Thickness(22, 16) };
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
            try { RememberPort(); }
            catch (InvalidDataException error)
            {
                _status.Text = error.Message;
                _loading = true; _protocol.SelectedItem = _portProtocol; _loading = false;
                return;
            }
            var ports = _ports[_editingScope];
            var next = (ProbeProtocol)_protocol.SelectedItem!;
            _port.Value = next == ProbeProtocol.Udp ? ports.Udp : next == ProbeProtocol.Tcp ? ports.Tcp : _port.Value;
            _portProtocol = next;
            UpdateUdpControls();
        };
        _udpMode.SelectionChanged += (_, _) => UpdateUdpControls();
        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is string name && _profileDraft.FirstOrDefault(p => p.Name == name) is { } profile)
            { LoadProbe(profile.Options); _profileName.Text = name; }
        };
        _applyButton.Click += async (_, _) => await SaveAsync(false);
        _okButton.Click += async (_, _) => await SaveAsync(true);
        _cancelButton.Click += (_, _) => Close();
        Closing += (_, e) => { if (_busy || _testingWebhook) e.Cancel = true; };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            void CancelForShutdown(object? sender, ShutdownRequestedEventArgs e) => CancelTelegramPairing();
            lifetime.ShutdownRequested += CancelForShutdown;
            Closed += (_, _) => lifetime.ShutdownRequested -= CancelForShutdown;
        }
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy && !_testingWebhook) { Close(); e.Handled = true; } };
    }

    internal IReadOnlyList<AlertRule> GroupReferences(string name) => _alertRules.GroupReferences(name);
    internal void RefreshGroups(IEnumerable<string> groups, string? oldName, string? newName)
        => _alertRules.RefreshGroups(groups, oldName, newName);

    public static int[] TimeRanges { get; } = [1, 5, 10, 15, 60, 360, 1440, 10080];
    public void SelectTelegramSettings()
    {
        SelectPage("Notification actions");
        _webhookFormat.SelectedIndex = (int)WebhookFormat.Telegram;
    }
    public void SelectPage(string name) => _navigation.SelectedItem = name == "Local web / API" ? "Web Server" : name;

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
        var port = (int)Value(_port);
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
            DestinationPort = (int)Value(_port),
            UdpPortMode = _udpMode.SelectedIndex == 1 ? UdpPortMode.Range : UdpPortMode.Fixed,
            UdpEndPort = (int)Value(_udpEndPort), UdpPacketSize = (int)Value(_udpSize),
            UdpAllowFragmentation = _udpFragmentation.IsChecked == true, SourceAddress = source?.Address, InterfaceId = source?.Interface,
            Interval = _interval.ReadDuration(), Timeout = _timeout.ReadDuration(),
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
            _udpMode.SelectedIndex = options.UdpPortMode == UdpPortMode.Range ? 1 : 0;
            _udpEndPort.Value = options.UdpEndPort; _udpSize.Value = options.UdpPacketSize;
            _udpFragmentation.IsChecked = options.UdpAllowFragmentation;
            UpdateUdpControls();
            RememberPort();
            var sources = _source.ItemsSource!.Cast<ProbeSourceChoice>().ToList();
            var source = sources.FirstOrDefault(s => s.Address == options.SourceAddress && s.Interface == options.InterfaceId);
            if (source is null)
            {
                source = new ProbeSourceChoice($"Saved source (unavailable): {options.SourceAddress}", options.InterfaceId, options.SourceAddress);
                sources.Add(source); _source.ItemsSource = sources;
            }
            _source.SelectedItem = source;
            _interval.SetDuration(options.Interval); _timeout.SetDuration(options.Timeout);
            _hops.Value = options.MaxHops; _concurrency.Value = options.Concurrency; _memory.Value = options.HistoryLimit; _resolve.IsChecked = options.ResolveHostnames;
        }
        finally { _loading = false; }
    }
    private void UpdateUdpControls()
    {
        var udp = _protocol.SelectedItem is ProbeProtocol.Udp;
        _port.IsEnabled = _protocol.SelectedItem is not ProbeProtocol.Icmp;
        _udpMode.IsEnabled = _udpSize.IsEnabled = _udpFragmentation.IsEnabled = udp;
        _udpEndPort.IsEnabled = udp && _udpMode.SelectedIndex == 1;
        if (_portField is not null) _portField.IsVisible = _port.IsEnabled;
        if (_udpDetails is not null) _udpDetails.IsVisible = udp;
        if (udp && !_loading) _probeAdvanced.IsExpanded = true;
        if (_udpRangeField is not null) _udpRangeField.IsVisible = udp && _udpMode.SelectedIndex == 1;
    }
    private async Task<bool> SaveAsync(bool close)
    {
        if (_busy || _testingWebhook || _pairingTelegram is not null) return false;
        try
        {
            CaptureProbe();
            ValidateWebDraft();
            var configuration = _original with
            {
                Alerts = _alertRules.CaptureRules(),
                DefaultProbeOptions = _defaults, Profiles = _profileDraft.ToArray(), RestoreOnStartup = _restore.IsChecked == true,
                UseDefaultProbeOptionsForNewTargets = _useDefaults.IsChecked == true,
                RetentionDays = (int)Value(_retention), AggregationSeconds = (int)Value(_aggregation),
                DesktopNotifications = _toasts.IsChecked == true, WebEnabled = _web.IsChecked == true, WebPort = (int)Value(_webPort),
                WebLanEnabled = _webLan.IsChecked == true, WebLanAddress = SelectedWebAddress,
                WebLanAutomatic = IsAutomaticWebInterface,
                WebHttpAcknowledged = _webAcknowledged.IsChecked == true, WebAccessKeyVerifier = _webVerifier,
                Actions = _original.Actions with { NativeNotificationsEnabled = _native.IsChecked == true, IncludeRecovery = _recoveries.IsChecked == true,
                    MinimumNotificationSpacingSeconds = (int)Value(_spacing), Webhook = ReadWebhookDraft() },
                Display = new DisplayConfiguration { WarningLatencyMs = _warning.ReadDouble(), CriticalLatencyMs = _critical.ReadDouble(),
                    FixedMaximumMs = _graphAutomatic.IsChecked == true ? 0 : ReadGraphMaximum(), DefaultTimeRangeMinutes = TimeRanges[Math.Clamp(_range.SelectedIndex, 0, 7)],
                    ShowJitter = _jitter.IsChecked == true, ShowPacketLoss = _loss.IsChecked == true, ShowEventMarkers = _events.IsChecked == true }
            };
            configuration.Validate();
            _busy = true; _applyButton.IsEnabled = _okButton.IsEnabled = _cancelButton.IsEnabled = false;
            _alertRules.IsEnabled = false;
            _page.IsEnabled = false;
            UpdateTelegramConnection();
            await _apply(new SettingsChange(configuration, _selected != _initialSelected ? _selected : null,
                _selected != _initialSelected ? _ports[1].Tcp : null,
                _selected != _initialSelected ? _ports[1].Udp : null,
                _webNetworkChanged || _navigation.SelectedItem as string == "Web Server", _alertRules.RulesChanged));
            _alertRules.AcceptChanges(configuration.Alerts);
            WebhookSettingsSaved();
            _webNetworkChanged = false;
            if (IsAutomaticWebInterface && _webLan.IsChecked == true && _serverAddress?.Invoke() is { } running)
                _webAutomaticAddress = new WebServerAddress("Running network", new Uri(running).Host.Trim('[', ']'));
            _status.Text = "Settings saved. Changes apply immediately; monitoring continues.";
            _webSaved = true;
            UpdateWebControls();
            _busy = false;
            if (close) Close();
            return true;
        }
        catch (Exception ex) { _status.Text = "Settings were not applied: " + ex.Message; return false; }
        finally
        {
            _busy = false; _cancelButton.IsEnabled = true;
            _alertRules.IsEnabled = true;
            _page.IsEnabled = true;
            // Credential fields are cleared while saving. Recompute action availability
            // only after leaving the busy state, using the persisted secret reference.
            UpdateTelegramConnection();
            UpdateWebControls();
        }
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
    private static SettingsNumberField Number(string name, decimal minimum, decimal maximum, decimal increment = 1, bool fractional = false)
        => new() { Name = name, Minimum = minimum, Maximum = maximum, WholeNumber = increment >= 1 && !fractional };
    private static decimal Value(SettingsNumberField input) => input.Read();
    private static CheckBox Toggle(string name, string label) => new() { Name = name, Content = label };
    private static TextBlock Heading(string text) => SettingsUi.Heading(text);
    private static TextBlock Note(string text) => SettingsUi.Note(text);
    private static StackPanel Page(string title, string description, params Control[] controls)
        => SettingsUi.Page(SettingsUi.Section(title, controls));
    private static StackPanel Row(string label, Control input) => SettingsUi.Field(label, input);
}
