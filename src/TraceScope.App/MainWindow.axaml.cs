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
        if (OperatingSystem.IsMacOS())
        {
            SettingsButton.IsVisible = false;
            SummaryButton.IsVisible = AlertsButton.IsVisible = WorkspaceButton.IsVisible = false;
            NetworkButton.IsVisible = WebButton.IsVisible = HistoryButton.IsVisible = ExportButton.IsVisible = false;
        }
        if (OperatingSystem.IsWindows())
        {
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://TraceScope/Assets/Icons/tracescope-windows.png"));
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
        ToolTip.SetTip(HopTable, "Intermediate routers may limit ICMP replies while forwarding traffic normally. Use destination loss to assess end-to-end delivery. Latency values are milliseconds.");
        ToolTip.SetTip(RoutePlot, "Markers show mean latency; whiskers show minimum and maximum latency. Thin red strips show the proportion of probes without a response.");
        IntervalInput.SelectionChanged += (_, _) => ApplySelectedOptions();
        TimeoutInput.SelectionChanged += (_, _) => ApplySelectedOptions();
        MaxHopsInput.ValueChanged += (_, _) => ApplySelectedOptions();
        RoutePlot.HopSelected = hop =>
        {
            HopTable.SelectedItem = _rows.FirstOrDefault(x => x.Hop.Hop == hop);
        };
        Timeline.TimeSelected = time =>
        {
            _heldEnd = Timeline.EndTime;
            FocusInput.SelectedIndex = 2;
            HistoryPosition.Text = time.ToLocalTime().ToString("HH:mm:ss");
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
        InitializeAllTargets();
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
            for (var i = 0; i < _args.Length - 1; i++)
                if (_args[i] == "--target") await AddTarget(_args[++i]);
            if (_args.Contains("--all-targets")) OpenAllTargets(null, new RoutedEventArgs());
            if (ArgumentValue("--settings-page") is { } page)
            {
                OpenSettings(null, new RoutedEventArgs());
                _settingsWindow?.SelectPage(page);
            }
            if (ArgumentValue("--capture") is { } capture)
            {
                await Task.Delay(TimeSpan.FromSeconds(int.TryParse(ArgumentValue("--capture-after"), out var secs) ? secs : 15), _applicationCancellation.Token);
                var visual = (Control)(_settingsWindow?.Content ?? Content!);
                var size = visual.Bounds.Size;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
                bitmap.Render(visual);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                bitmap.Save(capture);
                var details = new { Targets = _targets.Select(x => new { x.Target, x.StateLabel, x.Summary, Round = x.Snapshot?.Round, HopCount = x.Snapshot?.Hops.Count }), Rows = _rows.Count, Title = TargetTitle.Text, SettingsWindowOpen = _settingsWindow?.IsVisible == true };
                await File.WriteAllTextAsync(Path.ChangeExtension(capture, ".json"), System.Text.Json.JsonSerializer.Serialize(details));
                if (_args.Contains("--exit-after-capture")) Close();
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private ProbeOptions ReadOptions()
    {
        var basis = !_creatingTarget ? _selected?.Session?.Options ?? _draftOptions : _draftOptions;
        var source = SourceInput.SelectedItem as SourceChoice;
        return basis with
        {
            Interval = IntervalInput.SelectedIndex >= 0 ? TimeSpan.FromSeconds(new[] { 1d, 2.5, 5, 10 }[Math.Clamp(IntervalInput.SelectedIndex, 0, 3)]) : basis.Interval,
            Timeout = TimeoutInput.SelectedIndex >= 0 ? TimeSpan.FromMilliseconds(Math.Max(OperatingSystem.IsMacOS() ? 1000 : 500, new[] { 500, 1000, 2000 }[Math.Clamp(TimeoutInput.SelectedIndex, 0, 2)])) : basis.Timeout,
            MaxHops = (int)(MaxHopsInput.Value ?? 30),
            Protocol = ProtocolInput.SelectedItem is ProbeProtocol protocol ? protocol : basis.Protocol,
            DestinationPort = (int)(PortInput.Value ?? 443),
            SourceAddress = source?.Source, InterfaceId = source?.Interface
        };
    }

    private void ApplySelectedOptions()
    {
        if (_settingOptions || _engine is null) return;
        var options = ReadOptions();
        if (_creatingTarget || _selected is null) _newTargetOverride = true;
        RememberProbeOptions(options);
        if (!_creatingTarget && _selected?.Session is { } session)
        {
            _engine.UpdateOptions(session.Id, options);
            RememberTargetPort(_selected, options);
            _diskPoints = null; _lastHistoryQuery = null;
            StatusText.Text = $"Settings updated for {session.Target}; changes apply to the next measurement round.";
        }
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
        _heldEnd = null;
        _settingFocus = true;
        FocusInput.SelectedIndex = 0;
        _settingFocus = false;
        if (_selected?.Session is { } session)
        {
            _settingOptions = true;
            try
            {
                IntervalInput.SelectedIndex = Array.IndexOf(new[] { 1d, 2.5, 5, 10 }, session.Options.Interval.TotalSeconds);
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
            try { _rows.Clear(); HopTable.SelectedItem = null; }
            finally { _refreshing = false; }
            TargetTitle.Text = "Ready to trace";
            TargetSubtitle.Text = "Continuous route monitoring";
            RoutePlot.Rows = [];
            RoutePlot.SelectedHop = null;
            RoutePlot.VerticalOffset = 0;
            Timeline.Samples = [];
            Timeline.EndTime = null;
            Timeline.SelectedTime = null;
            Timeline.Title = "No active target";
            GraphHeading.Text = "Latency history";
            FocusRange.Text = "Focus: Auto";
            HistoryPosition.Text = "LIVE";
            LatencyValue.Text = LossValue.Text = JitterValue.Text = MosValue.Text = ShortAverageValue.Text = LongAverageValue.Text = "—";
            FooterCount.Text = FooterAverage.Text = FooterMinimum.Text = FooterCurrent.Text = "—";
            Timeline.Markers = [];
            LossValue.Foreground = Brush.Parse("#D5D8DE");
            UpdateButtons();
            return;
        }
        TargetTitle.Text = _selected.Target;
        TargetSubtitle.Text = _selected.Snapshot is { } snapshot
            ? $"{snapshot.DestinationAddress}  ·  {_selected.StateLabel}  ·  round {snapshot.Round}  ·  {snapshot.Hops.Count} hops"
            : $"{_selected.Address}  ·  {_selected.StateLabel}  ·  discovering route…";
        _refreshing = true;
        try
        {
            var hops = _selected.Snapshot?.Hops ?? [];
            var selectedNumber = _selectedHop;
            while (_rows.Count > hops.Count) _rows.RemoveAt(_rows.Count - 1);
            for (var i = 0; i < hops.Count; i++)
            {
                if (i < _rows.Count) _rows[i] = new HopItem(hops[i]);
                else _rows.Add(new HopItem(hops[i]));
            }
            var chosen = selectedNumber.HasValue ? _rows.FirstOrDefault(x => x.Hop.Hop == selectedNumber) : _rows.LastOrDefault(x => x.Hop.IsDestination);
            HopTable.SelectedItem = chosen;
            RoutePlot.Rows = hops.Select(x => new HopPlotRow(x.Hop, x.MinimumMs, x.AverageMs, x.MaximumMs, x.LastMs, x.LossPercent)).ToArray();
            RoutePlot.SelectedHop = chosen?.Hop.Hop;
        }
        finally { _refreshing = false; }
        var destination = _selected.Snapshot?.Destination;
        FooterCount.Text = destination?.Sent.ToString(CultureInfo.InvariantCulture) ?? "—";
        FooterAverage.Text = Number(destination?.AverageMs);
        FooterMinimum.Text = Number(destination?.MinimumMs);
        FooterCurrent.Text = Number(destination?.LastMs);
        LatencyValue.Text = Number(destination?.LastMs, " ms");
        LossValue.Text = destination is null ? "—" : $"{destination.LossPercent:0.#}%";
        LossValue.Foreground = destination?.LossPercent > 0 ? Brushes.IndianRed : Brush.Parse("#D5D8DE");
        var now = _selected.IsHistorical ? _selected.Snapshot?.Timestamp ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow;
        var shortStats = MonitoringStatistics.Calculate(destination?.History ?? [], TimeSpan.FromSeconds(30), now);
        var longStats = MonitoringStatistics.Calculate(destination?.History ?? [], TimeSpan.FromMinutes(5), now);
        JitterValue.Text = Number(shortStats.JitterMs, " ms");
        MosValue.Text = Number(shortStats.Mos);
        ShortAverageValue.Text = Number(shortStats.AverageMs);
        LongAverageValue.Text = Number(longStats.AverageMs);
        if (_selected.Snapshot is { } s)
            StatusText.Text = s.Warning ?? $"{s.Timestamp.ToLocalTime():HH:mm:ss} · round {s.Round} took {s.RoundDuration.TotalSeconds:0.##} s · statistics cover this session · saved locally";
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
        if (_refreshing) return;
        _diskPoints = null; _lastHistoryQuery = null;
        _selectedHop = (HopTable.SelectedItem as HopItem)?.Hop.Hop;
        RoutePlot.SelectedHop = _selectedHop;
        RefreshGraph();
    }

    private void RefreshGraph()
    {
        RefreshAllTargets();
        if (_selected is null || Timeline is null) return;
        var hop = _selectedHop.HasValue ? _selected.Snapshot?.Hops.FirstOrDefault(x => x.Hop == _selectedHop) : _selected.Snapshot?.Destination;
        var points = hop?.History.Select(x => new GraphPoint(x.Timestamp, x.LatencyMs, !x.LatencyMs.HasValue && x.Status != ProbeStatus.Error, IsError: x.Status == ProbeStatus.Error)).ToArray() ?? [];
        Timeline.Samples = _diskPoints ?? points;
        Timeline.Horizon = TimeSpan.FromMinutes(new[] { 1, 5, 10, 15, 60, 360, 1440, 10080 }[Math.Clamp(HorizonInput.SelectedIndex, 0, 7)]);
        var latest = (_selected.IsHistorical || FocusInput.SelectedIndex == 1 || _selected.Session?.State != SessionState.Running) && points.Length > 0 ? points[^1].Time : DateTimeOffset.UtcNow;
        if (FocusInput.SelectedIndex == 2 && _heldEnd.HasValue)
        {
            Timeline.EndTime = _heldEnd;
            HistoryPosition.Text = _heldEnd.Value.ToLocalTime().ToString("HH:mm:ss");
        }
        else if (HistorySlider.Value < 99.9 && (points.Length > 1 || _historyFirst.HasValue))
        {
            var first = _historyFirst ?? points[0].Time;
            var last = _historyLast ?? points[^1].Time;
            Timeline.EndTime = first + TimeSpan.FromTicks((long)((last - first).Ticks * HistorySlider.Value / 100));
            HistoryPosition.Text = Timeline.EndTime.Value.ToLocalTime().ToString("HH:mm:ss");
        }
        else { Timeline.EndTime = latest; HistoryPosition.Text = _selected.IsHistorical ? "SAVED" : "LIVE"; }
        if (FocusInput.SelectedIndex == 2) _heldEnd ??= Timeline.EndTime;
        if (Timeline.EndTime is { } end)
            FocusRange.Text = $"Focus: {(end - Timeline.Horizon).ToLocalTime():HH:mm:ss}–{end.ToLocalTime():HH:mm:ss}";
        GraphHeading.Text = _selectedHop.HasValue ? $"Hop {_selectedHop} · {hop?.Address ?? "no response"}" : $"Destination · {_selected.Target}";
        if (_diskPoints is null) Timeline.Title = hop is null ? "Waiting for measured samples" : $"{hop.Sent:N0} probes · {hop.LossPercent:0.#}% no response";
        if (_runtimeFeatures && (_selected.IsHistorical || _heldEnd.HasValue || Timeline.Horizon.TotalMinutes > 15))
            _ = LoadDiskGraphAsync();
    }

    private void ViewChanged(object? sender, SelectionChangedEventArgs e) { if (_engine is not null) { _diskPoints = null; _lastHistoryQuery = null; RefreshGraph(); } }
    private void HistoryPositionChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_engine is null) return;
        _diskPoints = null; _lastHistoryQuery = null;
        _heldEnd = null;
        if (HistorySlider.Value < 99.9)
        {
            _settingFocus = true;
            FocusInput.SelectedIndex = 2;
            _settingFocus = false;
        }
        RefreshGraph();
        if (FocusInput.SelectedIndex == 2) _heldEnd = Timeline.EndTime;
    }

    private void FocusChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || _settingFocus) return;
        _diskPoints = null; _lastHistoryQuery = null; _historyQuery?.Cancel();
        if (FocusInput.SelectedIndex == 2) _heldEnd ??= Timeline.EndTime;
        else { _heldEnd = null; HistorySlider.Value = 100; Timeline.SelectedTime = null; }
        RefreshGraph();
    }

    private void GoLive(object? sender, RoutedEventArgs e)
    {
        _diskPoints = null; _lastHistoryQuery = null; _historyQuery?.Cancel();
        _heldEnd = null;
        _settingFocus = true;
        FocusInput.SelectedIndex = 0;
        _settingFocus = false;
        HistorySlider.Value = 100; Timeline.SelectedTime = null; RefreshGraph();
    }

    private void UpdateButtons()
    {
        if (_engine is null) return;
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
        ExportButton.IsEnabled = _selected?.Snapshot is not null;
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
                Title = "Export measured samples", SuggestedFileName = $"tracescope-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
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
        if (samples.Count == 0) { StatusText.Text = "This session has no saved samples."; return; }
        var hops = samples.Where(x => x.Hop > 0).GroupBy(x => x.Hop).Select(g =>
        {
            var latestAddress = g.OrderBy(x => x.Timestamp).Last().Address;
            return Aggregate(g.Key, latestAddress, g.Where(x => x.Address == latestAddress));
        }).OrderBy(x => x.Hop).ToArray();
        var destSamples = samples.Where(x => x.Hop == 0).ToArray();
        var destination = destSamples.Length > 0 ? Aggregate(0, session.DestinationAddress, destSamples) : hops.LastOrDefault(x => x.IsDestination) ?? Aggregate(0, session.DestinationAddress, []);
        var warning = samples.Count < session.SampleCount ? $"Hop table covers the latest 15 minutes. Navigate the graph to query older saved measurements; CSV exports the entire session." : null;
        var item = new TargetItem(session) { Snapshot = new RoundSnapshot(session.Id, session.Target, session.DestinationAddress, samples.Max(x => x.Timestamp), samples.Max(x => x.Round), hops.Any(x => x.IsDestination), hops, destination, TimeSpan.Zero, warning) };
        item.Refresh(); _targets.Add(item); TargetList.SelectedItem = item; TargetCount.Text = _targets.Count.ToString();
        _historyFirst = range?.First; _historyLast = range?.Last;
        if (_runtimeFeatures) await LoadDiskGraphAsync(true);
    }

    private static HopSnapshot Aggregate(int hop, string? address, IEnumerable<StoredSample> source)
    {
        var samples = source.OrderBy(x => x.Timestamp).ToArray();
        var replies = samples.Where(x => x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).ToArray();
        var diffs = replies.Zip(replies.Skip(1), (a, b) => Math.Abs(a - b)).ToArray();
        return new HopSnapshot(hop, address, null, samples.LastOrDefault()?.LatencyMs, replies.Length > 0 ? replies.Average() : null,
            replies.Length > 0 ? replies.Min() : null, replies.Length > 0 ? replies.Max() : null, diffs.Length > 0 ? diffs.Average() : null,
            samples.LongCount(x => x.Status != ProbeStatus.Error), replies.Length, samples.Any(x => x.Status != ProbeStatus.Error) ? samples.Count(x => x.Status != ProbeStatus.Error && !x.LatencyMs.HasValue) * 100d / samples.Count(x => x.Status != ProbeStatus.Error) : 0,
            samples.Any(x => x.IsDestination), samples.Select(x => new LatencySample(x.Timestamp, x.LatencyMs, x.Status)).ToArray(), samples.LastOrDefault()?.Status ?? ProbeStatus.Timeout);
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
        _clock.Stop(); _workspaceSaveTimer.Stop(); _historyQuery?.Cancel();
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
    public RoundSnapshot? Snapshot { get; set; }
    public string StateLabel => IsHistorical ? "Saved session" : Session!.State.ToString();
    public IBrush StatusBrush => Brush.Parse(IsHistorical ? "#8C95A4" : Session!.State == SessionState.Paused ? "#DCB75D"
        : Session.State != SessionState.Running ? "#9E7474" : Snapshot?.Destination.Status == ProbeStatus.Error ? "#8C95A4"
        : Snapshot?.Destination.Sent > 0 && Snapshot.Destination.LastMs is null ? "#D65B5B"
        : Snapshot?.Destination.LossPercent > 5 ? "#DCB75D" : "#3CC659");
    public string Summary => Snapshot is null ? "Waiting for first round" : $"{MainWindow.Number(Snapshot.Destination.LastMs, " ms")}  ·  {Snapshot.Destination.LossPercent:0.#}% loss";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { PropertyChanged?.Invoke(this, new(nameof(StateLabel))); PropertyChanged?.Invoke(this, new(nameof(Summary))); PropertyChanged?.Invoke(this, new(nameof(StatusBrush))); }
}

public sealed record HopItem(HopSnapshot Hop)
{
    public string HopLabel => Hop.Hop.ToString();
    public string AddressLabel => Hop.Address is null ? "*  No response" : !string.IsNullOrWhiteSpace(Hop.Hostname) && Hop.Hostname != Hop.Address ? $"{Hop.Address}  {Hop.Hostname}" : Hop.Address;
    public string IpLabel => Hop.Address ?? "*";
    public string NameLabel => Hop.Status == ProbeStatus.Error ? "Probe unavailable" : Hop.Address is null ? "No response" : Hop.Hostname ?? Hop.Address;
    public IBrush StatusBrush => Brush.Parse(Hop.LastMs.HasValue ? "#35C756" : Hop.LossPercent >= 100 ? "#CF6767" : "#D9B662");
    public long Sent => Hop.Sent;
    public string LossLabel => $"{Hop.LossPercent:0.#}";
    public string AverageLabel => MainWindow.Number(Hop.AverageMs);
    public string MinimumLabel => MainWindow.Number(Hop.MinimumMs);
    public string MaximumLabel => MainWindow.Number(Hop.MaximumMs);
    public string CurrentLabel => MainWindow.Number(Hop.LastMs);
}

public sealed record HistoryEntry(StoredSession Session)
{
    public override string ToString() => $"{Session.Target}  ·  {Session.StartedAt.ToLocalTime():MMM d, HH:mm:ss}  ·  {Session.SampleCount:N0} samples";
}
