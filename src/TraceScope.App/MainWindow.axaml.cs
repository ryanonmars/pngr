using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow : Window
{
    private readonly TraceEngine _engine;
    private readonly ObservableCollection<TargetItem> _targets = [];
    private readonly ObservableCollection<HopItem> _rows = [];
    private readonly string[] _args;
    private TargetItem? _selected;
    private int? _selectedHop;
    private bool _refreshing;
    private bool _closing;
    private bool _shutdownComplete;
    private Task? _shutdownTask;
    private readonly CancellationTokenSource _applicationCancellation = new();
    private bool _settingOptions;
    private bool _settingFocus;
    private readonly HashSet<Guid> _closingTargets = [];
    private DateTimeOffset? _heldEnd;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow() : this([]) { }
    public MainWindow(string[] args, TraceEngine? engine = null)
    {
        InitializeComponent();
        VersionLabel.Text = $"Pngr {System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion ?? "0.7.3"}";
        ApplicationMenuBar.IsVisible = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://pngr/Assets/Icons/pngr-windows.png"));
            Icon = new WindowIcon(icon);
        }
        _args = args;
        var dataDirectory = ArgumentValue("--data-dir") ?? DefaultDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        _engine = engine ?? new TraceEngine(Path.Combine(dataDirectory, "tracescope.db"));
        _engine.SnapshotUpdated += (_, snapshot) => Dispatcher.UIThread.Post(() => ReceiveSnapshot(snapshot));
        _engine.SessionChanged += (_, session) => Dispatcher.UIThread.Post(() =>
        {
            var item = _targets.FirstOrDefault(t => t.Id == session.Id);
            item?.Refresh();
            if (_selected?.Id == session.Id) { UpdateButtons(); RefreshSelected(); }
        });
        TargetList.ItemsSource = _targets;
        HopTable.ItemsSource = _rows;
        HopTable.LayoutUpdated += (_, _) => SynchronizeRouteScroll();
        if (OperatingSystem.IsMacOS())
        {
            ((ComboBoxItem)TimeoutInput.Items[0]!).IsEnabled = false;
            ToolTip.SetTip(TimeoutInput, "The macOS traceroute tool has a minimum timeout of one second.");
        }
        ToolTip.SetTip(HopTable, "Statistics are per router identity and configuration. Response loss is not proven forwarding loss.");
        ToolTip.SetTip(RoutePlot, "Markers show average latency; whiskers show min/max. Red bars show response loss.");
        ToolTip.SetTip(IntervalInput, "Minimum time between round starts. Rounds never overlap; longer rounds delay the next start.");
        IntervalInput.SelectionChanged += (_, _) => ApplySelectedOptions();
        TimeoutInput.SelectionChanged += (_, _) => ApplySelectedOptions();
        MaxHopsInput.ValueChanged += (_, _) => ApplySelectedOptions();
        RoutePlot.HopSelected = hop =>
        {
            HopTable.SelectedItem = _rows.FirstOrDefault(x => x.Hop.Hop == hop);
        };
        _clock.Tick += (_, _) =>
        {
            foreach (var item in _targets) item.Refresh();
            if (_selected is { } selected)
            {
                UpdateButtons();
                if (selected.Session?.State == SessionState.Error)
                    StatusText.Text = selected.Session.Error ?? "Measurement error";
                RefreshGraph();
            }
        };
        InitializeProfessional(dataDirectory, engine is null);
        InitializeUpdates();
        InitializeAllTargets();
        InitializeHistoricalAnalysis();
        InitializeMainWindowLayout();
        KeyDown += SettingsShortcut;
        _clock.Start();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private string? ArgumentValue(string name)
    {
        var i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private static string DefaultDataDirectory()
    {
        // Packaged builds launched from Finder still store their experiment data in the project.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TraceScope.slnx")))
                return Path.Combine(directory.FullName, "artifacts", "data");
        // Keep the legacy TraceScope folder and tracescope.db filename for compatibility.
        // Renaming the app must not split existing history or require moving an open database.
        // Installed application bundles/Program Files can be read-only; keep standalone user
        // data in the normal per-user location instead of modifying the executable directory.
        return OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "TraceScope")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraceScope");
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await InitializeProfessionalOnOpenAsync();
            if (_closing) return;
            // Avoid update traffic in headless tests and explicit capture-only runs.
            if (_runtimeFeatures && !_args.Contains("--capture")) _ = CheckForUpdatesAsync(false);
            _windowLayoutReady = true;
            RememberMainWindowLayout();
            // Capture-only sizing keeps native visual verification repeatable.
            if (_args.Contains("--capture"))
            {
                if (int.TryParse(ArgumentValue("--capture-width"), out var width)) Width = Math.Max(MinWidth, width);
                if (int.TryParse(ArgumentValue("--capture-height"), out var height)) Height = Math.Max(MinHeight, height);
            }
            for (var i = 0; i < _args.Length - 1; i++)
                if (_args[i] == "--target") await AddTarget(_args[++i]);
            if (_args.Contains("--all-targets")) OpenAllTargets(null, new RoutedEventArgs());
            if (ArgumentValue("--settings-page") is { } page)
            {
                OpenSettings(null, new RoutedEventArgs());
                _settingsWindow?.SelectPage(page);
            }
            if (_args.Contains("--probe-configuration")) OpenProbeConfiguration(null, new RoutedEventArgs());
            if (ArgumentValue("--profile-menu") is { } profileOperation)
            {
                if (profileOperation == "load") ExecuteAppMenuCommand("Load Profile…");
                else if (profileOperation == "save") ExecuteAppMenuCommand("Save Current Configuration…");
                else if (profileOperation == "manage") ExecuteAppMenuCommand("Manage/Delete Profiles…");
            }
            if (ArgumentValue("--capture") is { } capture)
            {
                await Task.Delay(TimeSpan.FromSeconds(int.TryParse(ArgumentValue("--capture-after"), out var secs) ? secs : 15), _applicationCancellation.Token);
                ToolTip? capturedTip = null;
                Control? tipOwner = null;
                if (ArgumentValue("--capture-tooltip") is { } tipName && _probeConfigurationWindow is { } probeWindow)
                {
                    tipOwner = probeWindow.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == tipName);
                    if (tipOwner is not null && ToolTip.GetTip(tipOwner) is { } tipText)
                    {
                        capturedTip = new ToolTip { Content = new TextBlock { Text = tipText.ToString(), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } };
                        ToolTip.SetTip(tipOwner, capturedTip);
                        ToolTip.SetIsOpen(tipOwner, true);
                        await Task.Delay(150, _applicationCancellation.Token);
                    }
                }
                var visual = (Control)(_profileWindow?.Content ?? _probeConfigurationWindow?.Content ?? _settingsWindow?.Content ?? Content!);
                var size = visual.Bounds.Size;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
                bitmap.Render(visual);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                bitmap.Save(capture);
                if (capturedTip is { Bounds.Width: > 0, Bounds.Height: > 0 })
                {
                    var tipSize = capturedTip.Bounds.Size;
                    using var tipBitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(tipSize.Width), (int)Math.Ceiling(tipSize.Height)), new Vector(96, 96));
                    tipBitmap.Render(capturedTip);
                    tipBitmap.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(capture))!, Path.GetFileNameWithoutExtension(capture) + "-tooltip.png"));
                    ToolTip.SetIsOpen(tipOwner!, false);
                }
                var details = new { Targets = _targets.Select(x => new { x.Target, x.StateLabel, x.Summary, Round = x.Snapshot?.Round, HopCount = x.Snapshot?.Hops.Count }), Rows = _rows.Count, Title = TargetTitle.Text, SettingsWindowOpen = _settingsWindow?.IsVisible == true };
                await File.WriteAllTextAsync(Path.ChangeExtension(capture, ".json"), System.Text.Json.JsonSerializer.Serialize(details));
                if (_args.Contains("--exit-after-capture")) { _profileWindow?.Close(); _probeConfigurationWindow?.Close(); Close(); }
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private ProbeOptions ReadOptions()
    {
        var basis = !_creatingTarget ? _selected?.Session?.Options ?? _draftOptions : _draftOptions;
        var source = SourceInput.SelectedItem as SourceChoice;
        var selectedProtocol = ProtocolInput.SelectedItem is ProbeProtocol p ? p : basis.Protocol;
        return basis with
        {
            Interval = IntervalInput.SelectedIndex >= 0 ? TimeSpan.FromSeconds(ProbeIntervalSeconds[Math.Clamp(IntervalInput.SelectedIndex, 0, ProbeIntervalSeconds.Length - 1)]) : basis.Interval,
            Timeout = TimeoutInput.SelectedIndex >= 0 ? TimeSpan.FromMilliseconds(Math.Max(OperatingSystem.IsMacOS() && selectedProtocol == ProbeProtocol.Icmp ? 1000 : 500, new[] { 500, 1000, 2000 }[Math.Clamp(TimeoutInput.SelectedIndex, 0, 2)])) : basis.Timeout,
            MaxHops = (int)(MaxHopsInput.Value ?? 30),
            UdpPacketSize = _packetSizeCommit ?? basis.UdpPacketSize,
            Protocol = selectedProtocol,
            DestinationPort = (int)(PortInput.Value ?? 443),
            UdpPortMode = UdpModeInput.SelectedIndex == 1 ? UdpPortMode.Range : UdpPortMode.Fixed,
            UdpEndPort = (int)(UdpEndPortInput.Value ?? 33625),
            SourceAddress = source?.Source, InterfaceId = source?.Interface
        };
    }

    private void ApplySelectedOptions()
    {
        if (_settingOptions || _engine is null) return;
        var options = ReadOptions();
        try { (_configuration with { DefaultProbeOptions = options }).Validate(); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        if (_creatingTarget || _selected is null) _newTargetOverride = true;
        RememberProbeOptions(options);
        if (!_creatingTarget && _selected?.Session is { } session)
        {
            _engine.UpdateOptions(session.Id, options);
            RememberTargetPort(_selected, options);
            CurrentHistoryView.LoadedKey = null;
            StatusText.Text = $"Settings updated for {session.Target}; changes apply to the next measurement round.";
        }
        UpdateProbeDetailsAvailability();
        UpdateProbeSummary();
        ScheduleWorkspaceSave();
    }

    private async void StartTrace(object? sender, RoutedEventArgs e)
    {
        if (_addingTarget || _closing) return;
        if (!string.IsNullOrWhiteSpace(TargetInput.Text) || _creatingTarget || _selected is null)
            await AddTarget(TargetInput.Text ?? "");
        else if (_selected.Session?.State is SessionState.Running or SessionState.Paused)
            PauseTrace(sender, e);
        else if (_selected.Session is not null)
            await RestartTargetAsync(_selected);
    }

    private async void TargetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await AddTarget(TargetInput.Text ?? ""); }
    }

    private async Task AddTarget(string text, ProbeOptions? options = null, string? group = null, bool paused = false)
    {
        if (_closing || _addingTarget) return;
        if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = "Enter a hostname or IP address."; return; }
        var returnToAllTargets = _allTargetsVisible;
        var newOptions = options ?? (_configuration.UseDefaultProbeOptionsForNewTargets && !_newTargetOverride
            ? _configuration.DefaultProbeOptions : ReadOptions());
        var profileName = options is null ? ProfileInput.SelectedItem as string : null;
        _addingTarget = true;
        UpdateButtons();
        try
        {
            foreach (var target in text.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var existing = _targets.FirstOrDefault(x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && x.Session?.State is SessionState.Running or SessionState.Paused);
                if (existing is not null) { TargetList.SelectedItem = existing; continue; }
                StatusText.Text = $"Resolving {target}…";
                var session = await _engine.StartTargetAsync(target, newOptions, _applicationCancellation.Token);
                if (_closing) { await _engine.StopAsync(session.Id); return; }
                var item = new TargetItem(session) { TcpPort = _configuration.LastTcpPort, UdpPort = _configuration.LastUdpPort, ProfileName = profileName, Group = group ?? (string.IsNullOrWhiteSpace(GroupInput.Text) ? "Default" : GroupInput.Text.Trim()) };
                RememberTargetPort(item, session.Options);
                session.Group = item.Group;
                AssignAlerts(item);
                if (paused) _engine.Pause(session.Id);
                _targets.Add(item);
                TargetList.SelectedItem = item;
                if (session.LatestSnapshot is { } snapshot) ReceiveSnapshot(snapshot);
                StatusText.Text = $"Tracing {target} · {session.Options.Protocol} · interval {session.Options.Interval.TotalSeconds:g} s";
            }
            if (!_restoringWorkspace)
            {
                RememberProbeOptions(newOptions, profileName);
                TargetInput.Text = "";
                TargetInput.Focus();
            }
            _creatingTarget = false;
            _newTargetOverride = false;
            TargetCount.Text = _targets.Count.ToString(CultureInfo.InvariantCulture);
            ScheduleWorkspaceSave();
        }
        catch (Exception ex) { StatusText.Text = $"Could not start trace: {ex.Message}"; }
        finally
        {
            _addingTarget = false; UpdateButtons();
            if (returnToAllTargets) OpenAllTargets(null, new RoutedEventArgs());
        }
    }

    private void ReceiveSnapshot(RoundSnapshot snapshot)
    {
        var item = _targets.FirstOrDefault(x => x.Id == snapshot.SessionId);
        if (item is null) return;
        item.Snapshot = snapshot;
        item.Refresh();
        if (_selected?.Id == snapshot.SessionId) RefreshSelected();
    }

    private void TargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_clearingOverviewTab) return;
        if (!_previewingOverviewTarget) SetAllTargetsVisible(false);
        _selected = TargetList.SelectedItem as TargetItem;
        _creatingTarget = false;
        _selectedHop = null;
        ActivateHistoryView();
        _heldEnd = Timeline.Viewport?.EndTime;
        _settingFocus = true;
        FocusInput.SelectedIndex = _heldEnd.HasValue ? 2 : 0;
        _settingFocus = false;
        if (_selected?.Session is { } session)
        {
            _settingOptions = true;
            try
            {
                IntervalInput.SelectedIndex = Array.IndexOf(ProbeIntervalSeconds, session.Options.Interval.TotalSeconds);
                TimeoutInput.SelectedIndex = Array.IndexOf(new[] { 500d, 1000, 2000 }, session.Options.Timeout.TotalMilliseconds);
                MaxHopsInput.Value = session.Options.MaxHops;
            }
            finally { _settingOptions = false; }
        }
        UpdateProfessionalSelection();
        if (_selected is null) SynchronizeProbeControls(NewTargetOptions());
        HistorySlider.Value = 100;
        Timeline.SelectedTime = null;
        RefreshSelected();
    }

    private void RefreshSelected()
    {
        if (_selected is null)
        {
            _refreshing = true;
            try { _rows.Clear(); HopTable.SelectedItem = null; ClearRoutePresentation(); }
            finally { _refreshing = false; }
            TargetTitle.Text = "Ready to trace";
            TargetSubtitle.Text = "";
            RoutePlot.Rows = [];
            RoutePlot.SelectedHop = null;
            RoutePlot.VerticalOffset = 0;
            Timeline.Samples = [];
            Timeline.EndTime = null;
            Timeline.SelectedTime = null;
            Timeline.Title = "No active target";
            FocusRange.Text = "Focus: Auto";
            HistoryPosition.Text = "LIVE";
            LatencyValue.Text = LossValue.Text = JitterValue.Text = MosValue.Text = ShortAverageValue.Text = LongAverageValue.Text = "—";
            FooterCount.Text = FooterAverage.Text = FooterMinimum.Text = FooterCurrent.Text = "—";
            Timeline.Markers = [];
            LossValue.Foreground = Brush.Parse("#D5D8DE");
            UpdateButtons();
            return;
        }
        TargetTitle.Text = _selected.StateLabel;
        TargetSubtitle.Text = "";
        _refreshing = true;
        try
        {
            var allHops = _selectionStatistics?.Where(h => h.Hop > 0).ToArray() ?? _selected.Snapshot?.Hops ?? [];
            var hops = PresentRoute(allHops);
            var selectedNumber = _selectedHop;
            while (_rows.Count > hops.Count) _rows.RemoveAt(_rows.Count - 1);
            for (var i = 0; i < hops.Count; i++)
            {
                if (i < _rows.Count && _rows[i].Hop.Hop == hops[i].Hop && _rows[i].Hop.Address == hops[i].Address)
                    _rows[i].Update(hops[i], IsHistoryGraphVisible(hops[i].Hop));
                else if (i < _rows.Count) _rows[i] = new HopItem(hops[i]) { GraphVisible = IsHistoryGraphVisible(hops[i].Hop) };
                else _rows.Add(new HopItem(hops[i]) { GraphVisible = IsHistoryGraphVisible(hops[i].Hop) });
            }
            var chosen = selectedNumber.HasValue ? _rows.FirstOrDefault(x => x.Hop.Hop == selectedNumber) : _rows.LastOrDefault(x => x.Hop.IsDestination);
            HopTable.SelectedItem = chosen;
            RoutePlot.Rows = hops.Select(x => new HopPlotRow(x.Hop, x.MinimumMs, x.AverageMs, x.MaximumMs, x.LastMs, x.LossPercent)).ToArray();
            RoutePlot.SelectedHop = chosen?.Hop.Hop;
        }
        finally { _refreshing = false; }
        var selectedEndpoints = _selectionStatistics?.Where(h => h.Hop == 0).ToArray();
        var mixedConfigurations = selectedEndpoints is { Length: > 1 };
        var destination = mixedConfigurations ? null : selectedEndpoints?.SingleOrDefault()
            ?? (_selectionStatistics is null ? _selected.Snapshot?.Destination : null);
        FooterCount.Text = destination?.Sent.ToString(CultureInfo.InvariantCulture) ?? "—";
        FooterAverage.Text = Number(destination?.AverageMs);
        FooterMinimum.Text = Number(destination?.MinimumMs);
        FooterCurrent.Text = Number(destination?.LastMs);
        LatencyValue.Text = Number(destination?.LastMs, " ms");
        LossValue.Text = destination is { Sent: > 0 } ? $"{destination.LossPercent:0.#}%" : "—";
        LossValue.Foreground = destination?.LossPercent > 0 ? Brushes.IndianRed : Brush.Parse("#D5D8DE");
        var endpointPort = _selectionStatistics is null ? _selected.Snapshot?.Measurements.FirstOrDefault(m => m.Hop == 255)?.Options.DestinationPort : null;
        ToolTip.SetTip(LossValue, "Response loss counts failed diagnostic attempts, not proven forwarding loss."
            + (endpointPort is { } port ? $" Latest endpoint port: {port}." : ""));
        var now = _selected.IsHistorical ? _selected.Snapshot?.Timestamp ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow;
        var shortStats = MonitoringStatistics.Calculate(destination?.History ?? [], TimeSpan.FromSeconds(30), now);
        var longStats = MonitoringStatistics.Calculate(destination?.History ?? [], TimeSpan.FromMinutes(5), now);
        JitterValue.Text = Number(_selectionStatistics is null ? shortStats.JitterMs : destination?.JitterMs, " ms");
        MosValue.Text = Number(shortStats.Mos);
        ShortAverageValue.Text = Number(shortStats.AverageMs);
        LongAverageValue.Text = Number(longStats.AverageMs);
        if (_selectionStatistics is not null)
        {
            var viewport = CurrentHistoryView.Viewport;
            var loaded = viewport.SelectionStart is { } start && viewport.SelectionEnd is { } end
                && CurrentHistoryView.StatisticsRange == (start, end);
            StatusText.Text = !loaded ? "Loading selected interval from stored measurements…"
                : _selectionStatistics.Count == 0 ? "No stored measurements in the selected interval"
                : mixedConfigurations ? "Selected interval spans different probing configurations; destination summary unavailable. Router statistics are separated by configuration."
                : "Selected interval · exact raw statistics · router identities and configurations shown separately";
            if (_selected.HistoricalConfigurationUnknown) StatusText.Text += " · legacy probing configurations were not recorded";
        }
        else if (_selected.Snapshot is { } s)
            StatusText.Text = $"Round {s.Round} · {(_selected.Session?.Capability?.HopDiscoveryAvailable == false ? "endpoint monitoring; hop discovery unavailable" : $"{s.Hops.Count} probed TTL positions")} · {(s.DestinationReached ? "destination replied" : s.RouteBoundIsHistorical ? "incomplete; retained terminal bound" : "incomplete route")} · {s.RoundDuration.TotalSeconds:0.##} s duration"
                + (s.SamplingInterval is { } actual ? $" · actual interval {actual.TotalSeconds:0.##} s" : "")
                + $" · counters per router identity/configuration {s.MeasurementEpoch + 1}"
                + (s.Warning is { } warning ? " · " + warning : " · saved locally");
        RefreshGraph();
        UpdateButtons();
    }

    private void SynchronizeRouteScroll()
    {
        if (_rows.Count == 0) { RoutePlot.VerticalOffset = 0; return; }
        var firstVisible = HopTable.GetVisualDescendants().OfType<DataGridRow>()
            .Select(row => new { Row = row, Item = row.DataContext as HopItem, Position = row.TranslatePoint(new Point(0, 0), HopTable) })
            .Where(x => x.Item is not null && x.Position.HasValue && x.Position.Value.Y + x.Row.Bounds.Height > HopTable.ColumnHeaderHeight && x.Position.Value.Y < HopTable.Bounds.Height)
            .OrderBy(x => x.Position!.Value.Y).FirstOrDefault();
        if (firstVisible is null) return;
        var index = _rows.IndexOf(firstVisible.Item!);
        if (index >= 0) RoutePlot.VerticalOffset = index * RoutePlot.RowHeight - firstVisible.Position!.Value.Y + RoutePlot.HeaderHeight;
    }

    private void HopChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || HopTable.SelectedItem is not HopItem item) return;
        CurrentHistoryView.LoadedKey = null;
        _selectedHop = item.Hop.Hop;
        RoutePlot.SelectedHop = _selectedHop;
        SelectPrimaryHistoryGraph(_selectedHop ?? 0);
        RefreshGraph();
    }

    private void RefreshGraph()
    {
        RefreshHistoryGraphs();
        RefreshAllTargets();
    }

    private void ViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || _settingHistoryViewport) return;
        var index = HorizonInput.SelectedIndex;
        if (index is >= 0 and < 8) CurrentHistoryView.Viewport.Horizon = TimeSpan.FromMinutes(new[] { 1, 5, 10, 15, 60, 360, 1440, 10080 }[index]);
        RefreshGraph();
    }

    private void HistoryPositionChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_engine is null || _settingHistoryViewport) return;
        var view = CurrentHistoryView;
        if (HistorySlider.Value >= 99.9) { GoLive(sender, new RoutedEventArgs()); return; }
        var first = view.Viewport.First ?? _selected?.Snapshot?.Destination.History.FirstOrDefault()?.Timestamp;
        var last = view.Viewport.Last ?? _selected?.Snapshot?.Timestamp;
        if (first.HasValue && last.HasValue)
            view.Viewport.EndTime = first.Value + TimeSpan.FromTicks((long)((last.Value - first.Value).Ticks * HistorySlider.Value / 100));
    }

    private void FocusChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || _settingFocus) return;
        if (FocusInput.SelectedIndex == 2) CurrentHistoryView.Viewport.EndTime ??= Timeline.EndTime ?? DateTimeOffset.UtcNow;
        else CurrentHistoryView.Viewport.GoLive();
        RefreshGraph();
    }

    private void GoLive(object? sender, RoutedEventArgs e)
    {
        CurrentHistoryView.Viewport.GoLive();
        _heldEnd = null;
        _selectionStatistics = null;
        CurrentHistoryView.LoadedKey = null;
        _settingHistoryViewport = _settingFocus = true;
        try { HistorySlider.Value = 100; FocusInput.SelectedIndex = 0; }
        finally { _settingHistoryViewport = _settingFocus = false; }
        RefreshSelected();
    }

    private void UpdateButtons()
    {
        if (_engine is null) return;
        UpdateProbeSummary();
        var state = _selected?.Session?.State;
        var busy = _addingTarget || (_selected is not null && (_monitoringOperations.Contains(_selected.Id) || _closingTargets.Contains(_selected.Id)));
        var adding = _creatingTarget || !string.IsNullOrWhiteSpace(TargetInput.Text) || _selected is null;
        StartButton.IsEnabled = !busy && !_closing && (adding || _selected?.Session is not null);
        StartButton.Content = busy ? "…" : !adding && state == SessionState.Running ? "Ⅱ" : "▶";
        var label = busy ? "Updating monitoring…" : adding ? "Add new target" : state == SessionState.Running ? "Pause monitoring" : "Start monitoring";
        ToolTip.SetTip(StartButton, label);
        Avalonia.Automation.AutomationProperties.SetName(StartButton, label);
        StopTraceAction.IsEnabled = !busy && state is SessionState.Running or SessionState.Paused;
        RestartTraceAction.IsEnabled = !busy && _selected?.Session is not null;
        ResetStatisticsAction.IsEnabled = !busy && _selected?.Session is not null;
        CloseTargetAction.IsEnabled = !busy && _selected is not null;
        NewTargetAction.IsEnabled = !_addingTarget && !_closing;
        if (_exportMenuAction is not null) _exportMenuAction.IsEnabled = _selected?.Snapshot is not null;
    }

    private void PauseTrace(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Session is not { } session || _monitoringOperations.Contains(session.Id)) return;
        if (session.State == SessionState.Paused) _engine.Resume(session.Id);
        else if (session.State == SessionState.Running) _engine.Pause(session.Id);
        _selected.Refresh(); UpdateButtons(); RefreshSelected(); ScheduleWorkspaceSave();
    }

    private async void StopTrace(object? sender, RoutedEventArgs e)
    {
        var item = _selected;
        if (item?.Session is null || !_monitoringOperations.Add(item.Id)) return;
        UpdateButtons();
        try { await _engine.StopAsync(item.Id); item.Refresh(); ScheduleWorkspaceSave(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _monitoringOperations.Remove(item.Id); RefreshSelected(); }
    }

    private async void CloseSelectedTarget(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } item) await CloseTargetAsync(item);
    }

    private async void CloseTarget(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: TargetItem item }) await CloseTargetAsync(item);
    }

    private async Task CloseTargetAsync(TargetItem item)
    {
        if (_monitoringOperations.Contains(item.Id) || !_closingTargets.Add(item.Id)) return;
        UpdateButtons();
        try
        {
            if (item.Session is not null) { await _engine.StopAsync(item.Id); _engine.ForgetSession(item.Id); }
            var index = _targets.IndexOf(item);
            if (index < 0) return;
            var selected = TargetList.SelectedItem as TargetItem;
            _targets.RemoveAt(index);
            TargetList.SelectedItem = ReferenceEquals(selected, item)
                ? (_targets.Count > 0 ? _targets[Math.Min(index, _targets.Count - 1)] : null)
                : selected;
            TargetCount.Text = _targets.Count.ToString(CultureInfo.InvariantCulture);
            StatusText.Text = $"Closed {item.Target} · saved history is available in Open history";
            ScheduleWorkspaceSave();
        }
        catch (Exception ex) { StatusText.Text = $"Could not close {item.Target}: {ex.Message}"; }
        finally { _closingTargets.Remove(item.Id); UpdateButtons(); }
    }

    private async void ExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export measured samples", SuggestedFileName = $"Pngr-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                DefaultExtension = "csv", FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            using var writer = new StreamWriter(stream);
            await _engine.Storage.ExportCsvAsync(_selected.Id, writer);
            StatusText.Text = $"Exported samples to {file.Name}";
        }
        catch (Exception ex) { StatusText.Text = $"Export failed: {ex.Message}"; }
    }

    private async void OpenHistory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = await Task.Run(() => _engine.Storage.LoadSessionsAsync());
            var list = new ListBox { ItemsSource = sessions.Select(x => new HistoryEntry(x)).ToArray(), Margin = new Thickness(16), MinHeight = 200 };
            var button = new Button { Content = "Open selected session", Margin = new Thickness(16, 0, 16, 16), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var panel = new DockPanel(); DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button); panel.Children.Add(list);
            var dialog = new Window { Title = "Saved trace sessions", Width = 650, Height = 410, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
            button.Click += async (_, _) =>
            {
                if (list.SelectedItem is not HistoryEntry entry) return;
                button.IsEnabled = false;
                try { await LoadHistory(entry.Session); dialog.Close(); }
                catch (Exception ex) { StatusText.Text = ex.Message; button.IsEnabled = true; }
            };
            list.DoubleTapped += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) { StatusText.Text = $"Could not open history: {ex.Message}"; }
    }

    private async Task LoadHistory(StoredSession session)
    {
        var live = _targets.FirstOrDefault(x => x.Id == session.Id);
        if (live is not null) { TargetList.SelectedItem = live; return; }
        var range = await Task.Run(() => _engine.Storage.LoadTimeRangeAsync(session.Id));
        var samples = await Task.Run(() => _engine.Storage.LoadSamplesAsync(session.Id, from: range?.Last - TimeSpan.FromMinutes(15)));
        var configurations = await _engine.Storage.LoadMeasurementOptionsAsync(session.Id);
        if (samples.Count == 0) { StatusText.Text = "This session has no saved samples."; return; }
        var latestEpoch = samples.Max(x => x.MeasurementEpoch);
        var latestRound = samples.Max(x => x.Round);
        var currentPositions = samples.Where(x => x.Round == latestRound && x.Hop > 0).Select(x => x.Hop).ToHashSet();
        var epochSamples = samples.Where(x => x.MeasurementEpoch == latestEpoch).ToArray();
        var hops = epochSamples.Where(x => x.Hop > 0 && currentPositions.Contains(x.Hop)).GroupBy(x => x.Hop).Select(g =>
        {
            var latestAddress = g.OrderBy(x => x.Timestamp).Last().Address;
            return Aggregate(g.Key, latestAddress, g);
        }).OrderBy(x => x.Hop).ToArray();
        var destSamples = epochSamples.Where(x => x.Hop == 0).ToArray();
        var destination = destSamples.Length > 0 ? Aggregate(0, session.DestinationAddress, destSamples) : hops.LastOrDefault(x => x.IsDestination) ?? Aggregate(0, session.DestinationAddress, []);
        var warning = "Saved hop table shows the latest probing configuration and route, within the latest 15 minutes. Select a historical interval to inspect earlier measurements.";
        var item = new TargetItem(session) { HistoricalConfigurationUnknown = configurations.Count == 0, Snapshot = new RoundSnapshot(session.Id, session.Target, session.DestinationAddress, samples.Max(x => x.Timestamp), samples.Max(x => x.Round), hops.Any(x => x.IsDestination && x.Status == ProbeStatus.Success && x.LastMs.HasValue), hops, destination, TimeSpan.Zero, warning) };
        if (item.HistoricalConfigurationUnknown) item.Snapshot = item.Snapshot! with { Warning = warning + " Legacy probing configurations were not recorded." };
        item.Refresh(); _targets.Add(item); TargetList.SelectedItem = item; TargetCount.Text = _targets.Count.ToString();
        _settingHistoryViewport = true;
        try { CurrentHistoryView.Viewport.First = range?.First; CurrentHistoryView.Viewport.Last = range?.Last; }
        finally { _settingHistoryViewport = false; }
        await LoadDiskGraphAsync(true);
    }

    private static HopSnapshot Aggregate(int hop, string? address, IEnumerable<StoredSample> source)
    {
        var statistics = new HopStatistics();
        StoredSample? previous = null;
        foreach (var sample in source.OrderBy(x => x.Timestamp))
        {
            if (previous is not null && (previous.Address != sample.Address || previous.MeasurementEpoch != sample.MeasurementEpoch))
                statistics.Suspend();
            if (sample.Address != address) { statistics.Suspend(); previous = sample; continue; }
            statistics.Add(sample.Timestamp, new ProbeResult(sample.Address, sample.LatencyMs, sample.Status, sample.Detail)
                { ResponseKind = sample.ResponseKind, Transmission = sample.Transmission }, 1800);
            previous = sample;
        }
        return statistics.Snapshot(hop, address, null, hop == 0 || previous?.IsDestination == true)
            with { MeasurementEpoch = previous?.MeasurementEpoch ?? 0 };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _shutdownTask = ShutdownAsync();
        await _shutdownTask;
        _shutdownComplete = true;
        Close();
    }

    private async Task ShutdownAsync()
    {
        _clock.Stop(); _workspaceSaveTimer.Stop(); _historyQuery?.Cancel(); _historyRefreshTimer.Stop();
        _maintenanceClock.Stop();
        await _applicationCancellation.CancelAsync();
        // Each step must run even when an earlier operation fails (e.g. an unwritable workspace).
        try { if (_runtimeFeatures) await SaveWorkspaceNowAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Workspace save on exit: " + ex.Message); }
        try { await StopWebAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Web shutdown: " + ex.Message); }
        try { if (_actions is not null) await _actions.DisposeAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Alert action shutdown: " + ex.Message); }
        try { if (_maintenanceTask is not null) await _maintenanceTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine("Maintenance shutdown: " + ex.Message); }
        try { await _engine.DisposeAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Monitoring shutdown: " + ex.Message); }
    }

    internal static string Number(double? number, string unit = "") => number.HasValue ? number.Value.ToString("0.0", CultureInfo.InvariantCulture) + unit : "—";
}

public sealed class TargetItem : INotifyPropertyChanged
{
    public TargetItem(TargetSession session) { Session = session; Id = session.Id; Target = session.Target; Address = session.ResolvedAddress; }
    public TargetItem(StoredSession session) { Id = session.Id; Target = session.Target; Address = session.DestinationAddress; }
    public Guid Id { get; }
    public string Target { get; }
    public string Address { get; }
    public TargetSession? Session { get; }
    public string Group { get; set; } = "Default";
    public int TcpPort { get; set; } = 443;
    public int UdpPort { get; set; } = 33434;
    public string? ProfileName { get; set; }
    public bool IsHistorical => Session is null;
    public bool HistoricalConfigurationUnknown { get; set; }
    public RoundSnapshot? Snapshot { get; set; }
    public string StateLabel => IsHistorical ? "Saved session" : Session!.State.ToString();
    public IBrush StatusBrush => Brush.Parse(IsHistorical ? "#8C95A4" : Session!.State == SessionState.Paused ? "#DCB75D"
        : Session.State != SessionState.Running ? "#9E7474" : Snapshot is null || Snapshot.Destination.Sent == 0 || Snapshot.Destination.Status == ProbeStatus.Error ? "#8C95A4"
        : Snapshot?.Destination.Sent > 0 && Snapshot.Destination.LastMs is null ? "#D65B5B"
        : Snapshot?.Destination.LossPercent > 5 ? "#DCB75D" : "#3CC659");
    public string Summary => Snapshot is null ? "Waiting for first round" : $"{MainWindow.Number(Snapshot.Destination.LastMs, " ms")}  ·  {Snapshot.Destination.LossPercent:0.#}% response loss";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { PropertyChanged?.Invoke(this, new(nameof(StateLabel))); PropertyChanged?.Invoke(this, new(nameof(Summary))); PropertyChanged?.Invoke(this, new(nameof(StatusBrush))); }
}

public sealed class HopItem(HopSnapshot hop) : INotifyPropertyChanged
{
    public HopSnapshot Hop { get; private set; } = hop;
    public void Update(HopSnapshot hop, bool graphVisible)
    {
        Hop = hop;
        GraphVisible = graphVisible;
        // An empty property name refreshes every measurement-derived binding.
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
    private bool _graphVisible;
    public bool GraphVisible
    {
        get => _graphVisible;
        set { if (_graphVisible == value) return; _graphVisible = value; PropertyChanged?.Invoke(this, new(nameof(GraphVisible))); PropertyChanged?.Invoke(this, new(nameof(GraphBrush))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IBrush GraphBrush => Brush.Parse(GraphVisible ? "#9FD5FF" : "#646C77");
    public string GraphHint => Hop.IsDestination ? "Show or hide independent destination endpoint history" : "Show or hide this TTL position history";
    public string StatisticsHint => $"{Hop.Sent} eligible attempts for this router/context · configuration {Hop.MeasurementEpoch + 1}."
        + (Hop.Detail is { Length: > 0 } detail ? "\n" + detail : "");
    public string HopLabel => Hop.Hop.ToString();
    public string AddressLabel => Hop.Address is null ? "*  No response" : !string.IsNullOrWhiteSpace(Hop.Hostname) && Hop.Hostname != Hop.Address ? $"{Hop.Address}  {Hop.Hostname}" : Hop.Address;
    public string IpLabel => Hop.Address ?? "*";
    public string NameLabel => Hop.Status == ProbeStatus.Error ? "Probe unavailable" : Hop.Address is null ? "No response" : Hop.Hostname ?? Hop.Address;
    public IBrush StatusBrush => Brush.Parse(Hop.Status == ProbeStatus.Error || Hop.Sent == 0 ? "#8C95A4" : Hop.LastMs.HasValue ? "#35C756" : Hop.LossPercent >= 100 ? "#CF6767" : "#D9B662");
    public long Sent => Hop.Sent;
    public string LossLabel => Hop.Sent > 0 ? $"{Hop.LossPercent:0.#}" : "—";
    public string AverageLabel => MainWindow.Number(Hop.AverageMs);
    public string MinimumLabel => MainWindow.Number(Hop.MinimumMs);
    public string MaximumLabel => MainWindow.Number(Hop.MaximumMs);
    public string CurrentLabel => MainWindow.Number(Hop.LastMs);
    public string JitterLabel => MainWindow.Number(Hop.JitterMs);
}

public sealed record HistoryEntry(StoredSession Session)
{
    public override string ToString() => $"{Session.Target}  ·  {Session.StartedAt.ToLocalTime():MMM d, HH:mm:ss}  ·  {Session.SampleCount:N0} samples";
}
