using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.App.Controls;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private sealed class HistoryViewState
    {
        public TimelineViewport Viewport { get; } = new();
        public HashSet<int> PinnedHops { get; } = [];
        public int PrimaryHop { get; set; }
        public bool PrimaryVisible { get; set; } = true;
        public Dictionary<int, double> GraphWeights { get; } = [];
        public Dictionary<int, HistoricalGraphWindow> Windows { get; } = [];
        public Dictionary<int, (HistoricalGraphWindow Window, GraphPoint[] Points)> Points { get; } = [];
        public IReadOnlyList<HopSnapshot>? Statistics { get; set; }
        public (DateTimeOffset Start, DateTimeOffset End)? StatisticsRange { get; set; }
        public IReadOnlyList<GraphEvent> Events { get; set; } = [];
        public string? LoadedKey { get; set; }
    }

    private readonly Dictionary<Guid, HistoryViewState> _historyViews = [];
    private readonly HistoryViewState _emptyHistoryView = new();
    private readonly Dictionary<int, LatencyTimeline> _additionalGraphs = [];
    private readonly DispatcherTimer _historyRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _settingHistoryViewport;
    private bool _refreshingHistory;
    private IReadOnlyList<HopSnapshot>? _selectionStatistics;
    private HistoryViewState CurrentHistoryView => _selected is null ? _emptyHistoryView : GetHistoryView(_selected.Id);

    private HistoryViewState GetHistoryView(Guid id)
    {
        if (_historyViews.TryGetValue(id, out var view))
        {
            NormalizeHistoryGraphIdentities(view);
            return view;
        }
        view = new HistoryViewState();
        _historyViews[id] = view;
        RestoreTargetHistoryLayout(id, view);
        NormalizeHistoryGraphIdentities(view);
        view.Viewport.Horizon = TimeSpan.FromMinutes(_configuration.Display.DefaultTimeRangeMinutes);
        view.Viewport.Changed += () => { if (_selected?.Id == id) HistoryViewportChanged(); };
        return view;
    }

    private void InitializeHistoricalAnalysis()
    {
        Timeline.Viewport = _emptyHistoryView.Viewport;
        InitializeHistoryLayout();
        _historyRefreshTimer.Tick += async (_, _) =>
        {
            _historyRefreshTimer.Stop();
            await LoadDiskGraphAsync();
        };
        HistoryGraphsScroll.SizeChanged += (_, _) => SizeHistoryGraphs();
        ToolTip.SetTip(HistoryGraphsScroll, "Drag to pan · double-click and drag to select · ⌘/Ctrl + wheel to zoom · Shift + wheel to pan · Home for Live · Esc clears selection");
        _targets.CollectionChanged += (_, _) =>
        {
            var ids = _targets.Select(t => t.Id).ToHashSet();
            foreach (var id in _historyViews.Keys.Where(id => !ids.Contains(id)).ToArray()) _historyViews.Remove(id);
        };
    }

    private void ActivateHistoryView()
    {
        _historyQuery?.Cancel();
        _historyRefreshTimer.Stop();
        _selectionStatistics = CurrentHistoryView.Statistics;
        Timeline.Viewport = CurrentHistoryView.Viewport;
        _settingHistoryViewport = true;
        try
        {
            SynchronizeHorizonPreset();
            HistorySlider.Value = 100;
        }
        finally { _settingHistoryViewport = false; }
    }

    private int GraphHop(int hop) => hop > 0 && _selected?.Snapshot?.Hops.FirstOrDefault(h => h.Hop == hop)?.IsDestination == true ? 0 : hop;
    private void NormalizeHistoryGraphIdentities(HistoryViewState view)
    {
        // A restored layout may precede route discovery. Once the destination hop is
        // known, its numbered alias and endpoint ID (0) must share one graph slot.
        var primary = GraphHop(view.PrimaryHop);
        var pinned = view.PinnedHops.Select(GraphHop).ToHashSet();
        var changed = primary != view.PrimaryHop || !view.PinnedHops.SetEquals(pinned);
        view.PrimaryHop = primary;
        if (changed) { view.PinnedHops.Clear(); view.PinnedHops.UnionWith(pinned); }
        foreach (var (hop, weight) in view.GraphWeights.Where(p => GraphHop(p.Key) != p.Key).ToArray())
        {
            view.GraphWeights.TryAdd(GraphHop(hop), weight);
            view.GraphWeights.Remove(hop);
            changed = true;
        }
        if (changed) view.LoadedKey = null;
    }

    private IEnumerable<int> VisibleHistoryHops(HistoryViewState view) => view.PinnedHops
        .Concat(view.PrimaryVisible ? [view.PrimaryHop] : Array.Empty<int>()).Distinct().OrderBy(h => h);
    private bool IsHistoryGraphVisible(int hop) => VisibleHistoryHops(CurrentHistoryView).Contains(GraphHop(hop));

    private void SelectPrimaryHistoryGraph(int hop)
    {
        var view = CurrentHistoryView;
        view.PrimaryHop = GraphHop(hop);
        view.PrimaryVisible = true;
        view.LoadedKey = null;
        foreach (var row in _rows) row.GraphVisible = IsHistoryGraphVisible(row.Hop.Hop);
        RememberHistoryLayout();
        RefreshGraph();
    }

    private void ToggleHopGraph(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: HopItem item })
        {
            ToggleHistoryGraph(item.Hop.Hop);
            item.GraphVisible = IsHistoryGraphVisible(item.Hop.Hop);
        }
    }
    private void GraphIndicatorPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    private void ToggleHistoryGraph(int hop)
    {
        if (_selected is null) return;
        hop = GraphHop(hop);
        var view = CurrentHistoryView;
        if (VisibleHistoryHops(view).Contains(hop))
        {
            view.PinnedHops.Remove(hop);
            if (view.PrimaryHop == hop) view.PrimaryVisible = false;
        }
        else view.PinnedHops.Add(hop);
        view.LoadedKey = null;
        foreach (var row in _rows) row.GraphVisible = IsHistoryGraphVisible(row.Hop.Hop);
        RememberHistoryLayout();
        RefreshGraph();
    }
    private void ClosePrimaryHistoryGraph(object? sender, RoutedEventArgs e) => HideHistoryGraph(CurrentHistoryView.PrimaryHop);
    private void HideHistoryGraph(int hop)
    {
        var view = CurrentHistoryView;
        view.PinnedHops.Remove(hop);
        if (view.PrimaryHop == hop) view.PrimaryVisible = false;
        view.LoadedKey = null;
        foreach (var row in _rows) row.GraphVisible = IsHistoryGraphVisible(row.Hop.Hop);
        RememberHistoryLayout();
        RefreshGraph();
    }
    private void ClearHistorySelection(object? sender, RoutedEventArgs e)
    {
        CurrentHistoryView.Viewport.ClearSelection();
        CurrentHistoryView.Statistics = null;
        _selectionStatistics = null;
        RefreshSelected();
    }

    private void HistoryViewportChanged()
    {
        if (_refreshingHistory || _settingHistoryViewport || _closing) return;
        _heldEnd = CurrentHistoryView.Viewport.EndTime;
        var refreshStatistics = false;
        if (CurrentHistoryView.Viewport.SelectionStart is null)
        {
            refreshStatistics = _selectionStatistics is not null;
            CurrentHistoryView.Statistics = _selectionStatistics = null;
            CurrentHistoryView.StatisticsRange = null;
        }
        else if (CurrentHistoryView.StatisticsRange != (CurrentHistoryView.Viewport.SelectionStart.Value, CurrentHistoryView.Viewport.SelectionEnd!.Value))
        {
            CurrentHistoryView.Statistics = _selectionStatistics = [];
            refreshStatistics = true;
        }
        _historyQuery?.Cancel();
        _settingFocus = true;
        try { FocusInput.SelectedIndex = _heldEnd.HasValue ? 2 : 0; }
        finally { _settingFocus = false; }
        _settingHistoryViewport = true;
        try
        {
            SynchronizeHorizonPreset();
        }
        finally { _settingHistoryViewport = false; }
        if (refreshStatistics) RefreshSelected(); else RefreshGraph();
        ScheduleHistoryLoad(true);
    }
    private void ScheduleHistoryLoad(bool debounce = false)
    {
        if (_selected is null || _closing) return;
        if (!debounce && _historyRefreshTimer.IsEnabled) return;
        _historyRefreshTimer.Stop(); _historyRefreshTimer.Start();
    }

    private void SynchronizeHorizonPreset()
    {
        var horizon = CurrentHistoryView.Viewport.Horizon;
        var index = Array.IndexOf(new[] { 1d, 5, 10, 15, 60, 360, 1440, 10080 }, horizon.TotalMinutes);
        if (index >= 0)
        {
            HorizonInput.SelectedIndex = index;
            if (HorizonInput.Items.Count > 8) HorizonInput.Items.RemoveAt(8);
        }
        else
        {
            var label = horizon.TotalMinutes < 2 ? $"{horizon.TotalSeconds:0.#} seconds"
                : horizon.TotalHours < 2 ? $"{horizon.TotalMinutes:0.#} minutes" : $"{horizon.TotalHours:0.#} hours";
            if (HorizonInput.Items.Count == 8) HorizonInput.Items.Add(new ComboBoxItem());
            ((ComboBoxItem)HorizonInput.Items[8]!).Content = label;
            HorizonInput.SelectedIndex = 8;
        }
    }

    private string HistoryKey(HistoryViewState view) => $"{_selected?.Id}:{view.Viewport.EffectiveEnd.UtcTicks}:{view.Viewport.Horizon.Ticks}:{view.Viewport.SelectionStart?.UtcTicks}:{view.Viewport.SelectionEnd?.UtcTicks}:{string.Join(',', VisibleHistoryHops(view))}";

    private void RefreshHistoryGraphs()
    {
        if (_refreshingHistory || Timeline is null || _engine is null) return;
        _refreshingHistory = true;
        try
        {
            var view = CurrentHistoryView;
            if (_selected is null)
            {
                Timeline.Samples = []; Timeline.Markers = []; PrimaryGraphFrame.IsVisible = false;
                foreach (var graph in _additionalGraphs.Values) graph.Viewport = null;
                _additionalGraphs.Clear();
                while (HistoryGraphsPanel.Children.Count > 1) HistoryGraphsPanel.Children.RemoveAt(1);
                return;
            }
            var snapshot = _selected.Snapshot;
            var first = snapshot?.Destination.History.FirstOrDefault()?.Timestamp;
            view.Viewport.First ??= first;
            view.Viewport.Last = snapshot?.Timestamp ?? view.Viewport.Last;
            Timeline.Viewport = view.Viewport;
            _heldEnd = view.Viewport.EndTime;
            var visible = VisibleHistoryHops(view).ToHashSet();
            PrimaryGraphFrame.IsVisible = visible.Contains(view.PrimaryHop);
            // The reusable primary frame also hosts a pinned primary hop, even if
            // the default graph was previously closed. Never create a second frame.
            var extras = visible.Where(h => h != view.PrimaryHop).ToHashSet();
            foreach (var hop in _additionalGraphs.Keys.Where(h => !extras.Contains(h)).ToArray())
            {
                var graph = _additionalGraphs[hop];
                graph.Viewport = null;
                HistoryGraphsPanel.Children.Remove((Control)graph.Parent!);
                _additionalGraphs.Remove(hop);
            }
            foreach (var hop in extras.OrderBy(h => h))
            {
                if (_additionalGraphs.ContainsKey(hop)) continue;
                var graph = new LatencyTimeline { Viewport = view.Viewport };
                var frame = new Grid { RowDefinitions = new RowDefinitions("18,*") };
                var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brush.Parse("#24262A") };
                var label = new TextBlock { Text = GraphHeader(hop), Margin = new Thickness(7, 1) };
                var close = new Button { Content = "×" };
                close.Classes.Add("close-target");
                Avalonia.Automation.AutomationProperties.SetName(close, "Close history graph");
                close.Click += (_, _) => HideHistoryGraph(hop);
                heading.Children.Add(label); Grid.SetColumn(close, 1); heading.Children.Add(close);
                frame.Children.Add(heading); Grid.SetRow(graph, 1); frame.Children.Add(graph);
                _additionalGraphs.Add(hop, graph); HistoryGraphsPanel.Children.Add(frame);
            }
            PopulateGraph(Timeline, view.PrimaryHop, view);
            foreach (var (hop, graph) in _additionalGraphs)
            {
                graph.Viewport = view.Viewport;
                PopulateGraph(graph, hop, view);
                if (graph.Parent is Grid frame && frame.Children[0] is Grid heading && heading.Children[0] is TextBlock label)
                    label.Text = GraphHeader(hop);
            }
            PrimaryGraphHeader.Text = GraphHeader(view.PrimaryHop);
            GraphHeading.Text = _selectedHop.HasValue ? $"Hop {_selectedHop} · {snapshot?.Hops.FirstOrDefault(h => h.Hop == _selectedHop)?.Address ?? "no response"}" : $"Destination · {_selected.Target}";
            var end = view.Viewport.EffectiveEnd;
            ClearSelectionButton.IsVisible = view.Viewport.SelectionStart.HasValue;
            FocusRange.Text = view.Viewport.SelectionStart is { } selectedStart && view.Viewport.SelectionEnd is { } selectedEnd
                ? $"Selection: {selectedStart.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}–{selectedEnd.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}"
                : $"Focus: {(view.Viewport.EndTime.HasValue || _selected.IsHistorical ? "Historical" : "Live")} · {(end - view.Viewport.Horizon).ToLocalTime():HH:mm:ss.fff}–{end.ToLocalTime():HH:mm:ss.fff}";
            HistoryPosition.Text = view.Viewport.EndTime.HasValue ? end.ToLocalTime().ToString("HH:mm:ss.fff") : _selected.IsHistorical ? "SAVED" : "LIVE";
            ArrangeHistoryGraphFrames();
            SizeHistoryGraphs();
            if (view.LoadedKey != HistoryKey(view)) ScheduleHistoryLoad();
        }
        finally { _refreshingHistory = false; }
    }

    private string GraphHeader(int hop)
    {
        if (hop == 0)
        {
            var destinationHops = _selectionStatistics?.Where(h => h.Hop > 0 && h.IsDestination).Select(h => h.Hop).Distinct().OrderBy(h => h).ToArray()
                ?? _selected?.Snapshot?.Hops.Where(h => h.IsDestination).Select(h => h.Hop).ToArray() ?? [];
            return $"{(destinationHops.Length > 0 ? $"Hop {string.Join('/', destinationHops)} · " : "")}Destination — {_selected?.Address}";
        }
        var addresses = _selectionStatistics?.Where(h => h.Hop == hop).Select(h => h.Address ?? "no response").Distinct().ToArray();
        if (addresses is null && CurrentHistoryView.Windows.TryGetValue(hop, out var window))
            addresses = window.IsAggregated ? window.AggregatedSamples.Select(s => s.Address ?? "no response").Distinct().ToArray()
                : window.RawSamples.Select(s => s.Address ?? "no response").Distinct().ToArray();
        return $"Hop {hop} — {(addresses is { Length: > 0 } ? string.Join(" / ", addresses.Take(3)) + (addresses.Length > 3 ? " / …" : "") : _selected?.Snapshot?.Hops.FirstOrDefault(h => h.Hop == hop)?.Address ?? "no response")}";
    }
    private void PopulateGraph(LatencyTimeline graph, int hop, HistoryViewState view)
    {
        var display = _configuration.Display;
        graph.WarningLatencyMs = display.WarningLatencyMs; graph.CriticalLatencyMs = display.CriticalLatencyMs;
        graph.FixedMaximumMs = display.FixedMaximumMs; graph.ShowJitter = display.ShowJitter;
        graph.ShowPacketLoss = display.ShowPacketLoss; graph.ShowEventMarkers = display.ShowEventMarkers;
        if (view.Windows.TryGetValue(hop, out var window))
        {
            if (!view.Points.TryGetValue(hop, out var cached) || !ReferenceEquals(cached.Window, window))
            {
                var points = window.IsAggregated ? window.AggregatedSamples.Select(s => new GraphPoint(s.Timestamp, s.AverageMs,
                s.AverageMs is null && s.Count > 0, s.LossPercent, s.JitterMs, s.Count == 0 && s.Errors > 0, true, s.Count + s.Errors)).ToArray()
                : RawGraphPoints(window.RawSamples);
                cached = (window, points);
                view.Points[hop] = cached;
            }
            if (!ReferenceEquals(graph.Samples, cached.Points)) graph.Samples = cached.Points;
            graph.Title = $"{window.TotalSamples:N0} probes · {(window.IsAggregated ? "aggregated bins; may include address changes" : "raw measurements")}";
            graph.Markers = view.Events;
        }
        else
        {
            var snapshot = hop == 0 ? _selected?.Snapshot?.Destination : _selected?.Snapshot?.Hops.FirstOrDefault(h => h.Hop == hop);
            graph.Samples = snapshot?.History.Select(s => new GraphPoint(s.Timestamp, s.LatencyMs,
                !MonitoringStatistics.IsResponse(s) && s.Status != ProbeStatus.Error, s.Status == ProbeStatus.Error ? null : MonitoringStatistics.IsResponse(s) ? 0 : 100, IsError: s.Status == ProbeStatus.Error, Address: snapshot.Address)).ToArray() ?? [];
            graph.Title = $"{snapshot?.Sent ?? 0:N0} probes · raw measurements";
            graph.Markers = _monitoringEvents.Where(e => e.SessionId == _selected?.Id).Select(e => new GraphEvent(e.Timestamp, e.Kind, e.Message)).ToArray();
        }
    }
    private static GraphPoint[] RawGraphPoints(IReadOnlyList<StoredSample> samples)
    {
        var result = new GraphPoint[samples.Count];
        StoredSample? previous = null;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var response = sample.LatencyMs.HasValue && sample.Status is ProbeStatus.Success or ProbeStatus.TimeExceeded;
            var previousResponse = previous?.LatencyMs.HasValue == true && previous.Status is ProbeStatus.Success or ProbeStatus.TimeExceeded;
            result[i] = new(sample.Timestamp, response ? sample.LatencyMs : null, !response && sample.Status != ProbeStatus.Error,
                sample.Status == ProbeStatus.Error ? null : response ? 0 : 100,
                response && previousResponse && previous!.Address == sample.Address ? Math.Abs(sample.LatencyMs!.Value - previous.LatencyMs!.Value) : null,
                sample.Status == ProbeStatus.Error, Address: sample.Address);
            previous = sample;
        }
        return result;
    }

    private async Task LoadDiskGraphAsync(bool force = false)
    {
        if (_selected is not { } item || _closing) return;
        _historyRefreshTimer.Stop();
        var view = CurrentHistoryView;
        var key = HistoryKey(view);
        if (!force && key == view.LoadedKey) return;
        _historyQuery?.Cancel(); _historyQuery?.Dispose();
        _historyQuery = CancellationTokenSource.CreateLinkedTokenSource(_applicationCancellation.Token);
        var token = _historyQuery.Token;
        try
        {
            var range = await Task.Run(() => _engine.Storage.LoadTimeRangeAsync(item.Id, cancellationToken: token), token);
            if (range is { } bounds)
            {
                _settingHistoryViewport = true;
                try { view.Viewport.First = bounds.First; view.Viewport.Last = bounds.Last; }
                finally { _settingHistoryViewport = false; }
            }
            var end = view.Viewport.EffectiveEnd;
            var from = end - view.Viewport.Horizon;
            key = HistoryKey(view);
            var windows = new Dictionary<int, HistoricalGraphWindow>();
            foreach (var hop in VisibleHistoryHops(view).ToArray())
                windows[hop] = await Task.Run(() => _engine.Storage.LoadGraphWindowAsync(item.Id, hop, from, end, 1600, token), token);
            var events = await Task.Run(() => _engine.Storage.LoadEventsAsync(item.Id, from, end, token), token);
            IReadOnlyList<HopSnapshot>? statistics = null;
            if (view.Viewport.SelectionStart is { } selectionStart && view.Viewport.SelectionEnd is { } selectionEnd)
                statistics = !force && view.StatisticsRange == (selectionStart, selectionEnd) ? view.Statistics
                    : await Task.Run(() => _engine.Storage.LoadSelectionStatisticsAsync(item.Id, selectionStart, selectionEnd, token), token);
            if (token.IsCancellationRequested || _selected?.Id != item.Id || key != HistoryKey(view)) return;
            view.Windows.Clear(); foreach (var (hop, window) in windows) view.Windows[hop] = window;
            foreach (var hop in view.Points.Keys.Where(h => !windows.ContainsKey(h)).ToArray()) view.Points.Remove(hop);
            view.Events = events.Select(e => new GraphEvent(e.Timestamp, e.Kind, e.Message)).ToArray();
            view.Statistics = _selectionStatistics = statistics;
            view.StatisticsRange = view.Viewport.SelectionStart is { } ss && view.Viewport.SelectionEnd is { } se ? (ss, se) : null;
            view.LoadedKey = key;
            RefreshSelected();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "History query failed: " + ex.Message; }
    }
}
