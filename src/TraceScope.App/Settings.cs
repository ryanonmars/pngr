using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TraceScope.App.Views;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private SettingsWindow? _settingsWindow;

    private void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        var session = _selected?.Session;
        var editable = session?.State is SessionState.Running or SessionState.Paused ? session : null;
        _settingsWindow = new SettingsWindow(_configuration, editable?.Options, editable?.Target,
            SourceInput.ItemsSource!.Cast<SourceChoice>().Select(s => new ProbeSourceChoice(s.Label, s.Interface, s.Source)).ToArray(),
            _dataDirectory, change => ApplySettingsAsync(change, editable?.Id), async () =>
            {
                ConfigureActions();
                return await _actions!.RequestNotificationPermissionAsync(_applicationCancellation.Token);
            }, tool =>
            {
                switch (tool)
                {
                    case "workspace": OpenWorkspace(null, e); break;
                    case "alerts": OpenAlerts(null, e); break;
                    case "actions": OpenAlertActions(null, e); break;
                    case "web": OpenWeb(null, e); break;
                }
            }, _selected?.TcpPort ?? 443, _selected?.UdpPort ?? 33434, () => _webServer?.Url, () => _webServer?.LastError);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    internal void OpenSettingsFromMenu() => OpenSettings(null, new RoutedEventArgs());

    private NativeMenuItem? _exportMenuAction;

    internal void RegisterApplicationMenu(NativeMenu menu)
    {
        _exportMenuAction = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == "File")
            .Menu!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Export CSV…");
        _exportMenuAction.IsEnabled = _selected?.Snapshot is not null;
    }

    internal void ExecuteAppMenuCommand(string header)
    {
        var args = new RoutedEventArgs();
        switch (header)
        {
            case "Settings…": OpenSettingsFromMenu(); break;
            case "Check for Updates…": _ = CheckForUpdatesAsync(true); break;
            case "Open History…": OpenHistory(null, args); break;
            case "Export CSV…": ExportCsv(null, args); break;
            case "All Targets": OpenAllTargets(null, args); break;
            case "Summary": OpenSummary(null, args); break;
            case "Network": case "Network…": OpenNetwork(null, args); break;
            case "Web": case "Local Web / API…": OpenWeb(null, args); break;
            case "Alerts": case "Alerts…": OpenAlerts(null, args); break;
            case "Workspace": case "Workspace…": OpenWorkspace(null, args); break;
            case "Load Profile…": OpenLoadProfileMenu(); break;
            case "Save Current Configuration…": OpenSaveCurrentProfileMenu(); break;
            case "Manage/Delete Profiles…": OpenManageProfilesMenu(); break;
        }
    }

    private void SettingsShortcut(object? sender, KeyEventArgs e)
    {
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key != Key.OemComma || e.KeyModifiers != modifier) return;
        OpenSettings(null, new RoutedEventArgs()); e.Handled = true;
    }

    private async Task ApplySettingsAsync(SettingsChange change, Guid? selectedSessionId)
    {
        // Keep targets, rules and summaries that may have changed while the modeless editor was open.
        var draft = change.Configuration;
        var configuration = _configuration with
        {
            RestoreOnStartup = draft.RestoreOnStartup, DefaultProbeOptions = draft.DefaultProbeOptions,
            UseDefaultProbeOptionsForNewTargets = draft.UseDefaultProbeOptionsForNewTargets,
            Display = draft.Display, RetentionDays = draft.RetentionDays, AggregationSeconds = draft.AggregationSeconds,
            Profiles = draft.Profiles, DesktopNotifications = draft.DesktopNotifications,
            WebEnabled = draft.WebEnabled, WebPort = draft.WebPort,
            WebLanEnabled = draft.WebLanEnabled, WebLanAddress = draft.WebLanAddress,
            WebLanAutomatic = draft.WebLanAutomatic,
            WebHttpAcknowledged = draft.WebHttpAcknowledged, WebAccessKeyVerifier = draft.WebAccessKeyVerifier,
            Actions = _configuration.Actions with
            {
                NativeNotificationsEnabled = draft.Actions.NativeNotificationsEnabled,
                IncludeRecovery = draft.Actions.IncludeRecovery,
                MinimumNotificationSpacingSeconds = draft.Actions.MinimumNotificationSpacingSeconds
            }
        };
        var selectedOptions = selectedSessionId.HasValue && change.SelectedTargetOptions is { } options
            && _engine.Sessions.Any(s => s.Id == selectedSessionId && s.State is SessionState.Running or SessionState.Paused)
            && _engine.Sessions.First(s => s.Id == selectedSessionId).Options != options ? options : null;
        await CommitSettingsCoreAsync(configuration, selectedSessionId, selectedOptions, change.SelectedTcpPort, change.SelectedUdpPort,
            change.ReevaluateWebInterface);
        StatusText.Text = "Settings saved · monitoring continues";
    }

    private Task CommitSettingsAsync(WorkspaceConfiguration configuration, Guid? sessionId = null, ProbeOptions? options = null,
        int? tcpPort = null, int? udpPort = null)
        => CommitSettingsCoreAsync(configuration, sessionId, options, tcpPort, udpPort, true);

    private async Task CommitSettingsCoreAsync(WorkspaceConfiguration configuration, Guid? sessionId, ProbeOptions? options,
        int? tcpPort, int? udpPort, bool reevaluateWebInterface)
    {
        configuration.Validate();
        await _webLifecycle.WaitAsync(_applicationCancellation.Token);
        LocalWebServer? replacement = null;
        var previousConfiguration = _configuration;
        var stoppedPrevious = false;
        var committed = false;
        try
        {
            _applicationCancellation.Token.ThrowIfCancellationRequested();
            if (reevaluateWebInterface || _webServer?.Url is null || !previousConfiguration.WebLanEnabled
                || !previousConfiguration.WebUsesAutomaticInterface)
                configuration = await ResolveWebInterfaceAsync(configuration);
            else if (configuration.WebLanEnabled && configuration.WebUsesAutomaticInterface)
                configuration = configuration with { WebLanAddress = previousConfiguration.WebLanAddress };
            var replace = configuration.WebEnabled && (_webServer?.Url is null
                || previousConfiguration.WebPort != configuration.WebPort
                || previousConfiguration.WebLanEnabled != configuration.WebLanEnabled
                || (configuration.WebLanEnabled && previousConfiguration.WebLanAddress != configuration.WebLanAddress));
            if (replace)
            {
                // Changing mode/address on the same port needs an orderly stop before rebinding.
                // A different port can be tested while the existing listener remains available.
                if (_webServer is not null && previousConfiguration.WebPort == configuration.WebPort)
                {
                    var stoppedServer = _webServer; _webServer = null; stoppedPrevious = true;
                    await stoppedServer.DisposeAsync();
                }
                replacement = new LocalWebServer(_engine);
                await replacement.StartAsync(WebOptions(configuration), _applicationCancellation.Token);
            }
            var saved = configuration with
            {
                WindowLayout = CaptureMainWindowLayout(),
                LastUsedProbeOptions = options ?? configuration.LastUsedProbeOptions,
                LastTcpPort = options?.Protocol == ProbeProtocol.Tcp ? options.DestinationPort : configuration.LastTcpPort,
                LastUdpPort = options?.Protocol == ProbeProtocol.Udp ? options.DestinationPort : configuration.LastUdpPort,
                Targets = CaptureWorkspace().Targets.Select(t => t with
                {
                    Options = options is not null && _targets.Any(x => x.Id == sessionId && x.Target == t.Target) ? options : t.Options,
                    TcpPort = options is not null && _targets.Any(x => x.Id == sessionId && x.Target == t.Target)
                        ? tcpPort ?? (options.Protocol == ProbeProtocol.Tcp ? options.DestinationPort : t.TcpPort) : t.TcpPort,
                    UdpPort = options is not null && _targets.Any(x => x.Id == sessionId && x.Target == t.Target)
                        ? udpPort ?? (options.Protocol == ProbeProtocol.Udp ? options.DestinationPort : t.UdpPort) : t.UdpPort
                }).ToArray()
            };
            // Failure to bind or save leaves the previous settings/listener intact.
            saved = await _workspaceStore.SaveAsync(saved, _applicationCancellation.Token);
            if (options is not null && sessionId.HasValue)
            {
                _engine.UpdateOptions(sessionId.Value, options);
                if (_targets.FirstOrDefault(t => t.Id == sessionId) is { } item)
                {
                    if (tcpPort is { } tcp) item.TcpPort = tcp;
                    if (udpPort is { } udp) item.UdpPort = udp;
                    RememberTargetPort(item, options);
                }
            }
            var resetTimeRange = _configuration.Display.DefaultTimeRangeMinutes != saved.Display.DefaultTimeRangeMinutes;
            _configuration = saved;
            committed = true;
            var previous = _webServer;
            if (!configuration.WebEnabled) _webServer = null;
            else if (replacement is not null) { _webServer = replacement; replacement = null; }
            else if (configuration.WebLanEnabled && configuration.WebAccessKeyVerifier != previousConfiguration.WebAccessKeyVerifier)
                _webServer?.RotateAccessKey(configuration.WebAccessKeyVerifier!);
            if (previous is not null && previous != _webServer) await previous.DisposeAsync();
            ApplyPreferenceControls(resetTimeRange); ReloadProfiles(); ConfigureActions();
            if (_selected?.Session?.Id == sessionId && options is not null)
            {
                SynchronizeProbeControls(options);
                CurrentHistoryView.LoadedKey = null;
            }
            CurrentHistoryView.LoadedKey = null; _historyQuery?.Cancel();
            RefreshGraph();
        }
        catch
        {
            if (replacement is not null) { await replacement.DisposeAsync(); replacement = null; }
            if (stoppedPrevious && !committed && !_applicationCancellation.IsCancellationRequested)
            {
                try
                {
                    var restored = new LocalWebServer(_engine);
                    try { await restored.StartAsync(WebOptions(previousConfiguration), _applicationCancellation.Token); _webServer = restored; }
                    catch { await restored.DisposeAsync(); throw; }
                }
                catch { StatusText.Text = "Previous web listener could not be restored. Reopen Web Server settings and apply to retry."; }
            }
            throw;
        }
        finally
        {
            try { if (replacement is not null) await replacement.DisposeAsync(); }
            finally { _webLifecycle.Release(); }
        }
    }

    private static WebServerOptions WebOptions(WorkspaceConfiguration configuration) => new()
    {
        Port = configuration.WebPort, LanEnabled = configuration.WebLanEnabled, LanAddress = configuration.WebLanAddress,
        HttpAcknowledged = configuration.WebHttpAcknowledged, AccessKeyVerifier = configuration.WebAccessKeyVerifier
    };

    private async Task<WorkspaceConfiguration> ResolveWebInterfaceAsync(WorkspaceConfiguration configuration)
    {
        if (!configuration.WebEnabled || !configuration.WebLanEnabled || !configuration.WebUsesAutomaticInterface) return configuration;
        var address = await WebServerNetwork.GetAutomaticLanAddressAsync(_applicationCancellation.Token)
            ?? throw new InvalidOperationException("Automatic selection could not find a suitable LAN address on the default route. Choose an interface in Advanced network options.");
        return configuration with { WebLanAddress = address.Address, WebLanAutomatic = true };
    }

    private void ApplyPreferenceControls(bool resetTimeRange = true)
    {
        _draftOptions = NewTargetOptions();
        var display = _configuration.Display;
        RoutePlot.WarningLatencyMs = Timeline.WarningLatencyMs = display.WarningLatencyMs;
        RoutePlot.CriticalLatencyMs = Timeline.CriticalLatencyMs = display.CriticalLatencyMs;
        RoutePlot.FixedMaximumMs = Timeline.FixedMaximumMs = display.FixedMaximumMs;
        RoutePlot.ShowPacketLoss = display.ShowPacketLoss;
        Timeline.ShowJitter = display.ShowJitter; Timeline.ShowPacketLoss = display.ShowPacketLoss; Timeline.ShowEventMarkers = display.ShowEventMarkers;
        LatencyLegend.Text = $"{display.WarningLatencyMs:0.#} ms   {display.CriticalLatencyMs:0.#} ms";
        if (resetTimeRange) HorizonInput.SelectedIndex = Array.IndexOf(SettingsWindow.TimeRanges, display.DefaultTimeRangeMinutes);
        if (_selected is null) SynchronizeProbeControls(_draftOptions);
        UpdateProbeSummary();
    }

    private void SynchronizeProbeControls(ProbeOptions options)
    {
        _settingOptions = true;
        try
        {
            ProtocolInput.SelectedItem = options.Protocol; PortInput.Value = options.DestinationPort;
            UdpModeInput.SelectedIndex = options.UdpPortMode == UdpPortMode.Range ? 1 : 0;
            UdpEndPortInput.Value = options.UdpEndPort;
            UpdateProtocolControls();
            IntervalInput.SelectedIndex = Array.IndexOf(ProbeIntervalSeconds, options.Interval.TotalSeconds);
            TimeoutInput.SelectedIndex = Array.IndexOf(new[] { 500d, 1000, 2000 }, options.Timeout.TotalMilliseconds);
            MaxHopsInput.Value = options.MaxHops;
            var choices = SourceInput.ItemsSource!.Cast<SourceChoice>().ToList();
            if ((options.SourceAddress is not null || options.InterfaceId is not null)
                && !choices.Any(s => s.Source == options.SourceAddress && s.Interface == options.InterfaceId))
            {
                choices.Add(new SourceChoice($"{options.InterfaceId ?? "Source"} · {options.SourceAddress ?? "unavailable"}", options.InterfaceId, options.SourceAddress));
                SourceInput.ItemsSource = choices;
            }
            SourceInput.SelectedItem = SourceInput.ItemsSource!.Cast<SourceChoice>()
                .FirstOrDefault(s => s.Source == options.SourceAddress && s.Interface == options.InterfaceId)
                ?? SourceInput.ItemsSource!.Cast<SourceChoice>().First();
        }
        finally { _settingOptions = false; }
        UpdateProbeSummary();
    }
}
