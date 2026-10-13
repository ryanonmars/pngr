using System.Diagnostics;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task ClosingTabsStopsOnlyTheirTracePreservesHistoryAndClearsTheLastTarget()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        await fixture.Start("127.0.0.2");
        var window = fixture.Window;
        var list = Control<ListBox>(window, "TargetList");
        var items = list.ItemsSource!.Cast<TargetItem>().ToArray();
        var first = items[0];
        var second = items[1];
        Assert.Same(second, list.SelectedItem);
        window.UpdateLayout();
        var firstClose = list.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("close-target") && ReferenceEquals(b.DataContext, first));
        Click(firstClose);
        await Until(() => list.ItemCount == 1);
        Assert.Equal(SessionState.Stopped, first.Session!.State);
        Assert.Equal(SessionState.Running, second.Session!.State);
        Assert.Same(second, list.SelectedItem);
        Assert.Equal("1", Control<TextBlock>(window, "TargetCount").Text);
        var firstFinalRound = first.Session.LatestSnapshot!.Round;
        await Until(() => second.Session.LatestSnapshot!.Round >= 3);
        Assert.Equal(firstFinalRound, first.Session.LatestSnapshot!.Round);

        window.UpdateLayout();
        var lastClose = list.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("close-target") && ReferenceEquals(b.DataContext, second));
        Click(lastClose);
        await Until(() => list.ItemCount == 0);
        Assert.Equal(SessionState.Stopped, second.Session.State);
        Assert.Null(list.SelectedItem);
        Assert.Equal("0", Control<TextBlock>(window, "TargetCount").Text);
        Assert.Equal("Ready to trace", Control<TextBlock>(window, "TargetTitle").Text);
        Assert.Empty(Control<DataGrid>(window, "HopTable").ItemsSource!.Cast<HopItem>());
        Assert.Empty(Control<LatencyTimeline>(window, "Timeline").Samples);
        Assert.Empty(Control<HopLatencyPlot>(window, "RoutePlot").Rows);
        Assert.True(Control<Button>(window, "StartButton").IsEnabled);
        Assert.False(Control<MenuItem>(window, "StopTraceAction").IsEnabled);
        Assert.False(Control<Button>(window, "ExportButton").IsEnabled);
        var saved = await fixture.Engine.Storage.LoadSessionsAsync();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, s => { Assert.NotNull(s.StoppedAt); Assert.True(s.SampleCount > 0); });

        var loader = typeof(MainWindow).GetMethod("LoadHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(loader.Invoke(window, [saved.Single(s => s.Id == first.Id)]));
        Assert.True(Assert.IsType<TargetItem>(list.SelectedItem).IsHistorical);
        window.UpdateLayout();
        Click(list.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("close-target")));
        await Until(() => list.ItemCount == 0);
        Assert.Equal(2, (await fixture.Engine.Storage.LoadSessionsAsync()).Count);
    }

    [AvaloniaFact]
    public async Task StartShowsMeasuredRouteAndSelectingHopsChangesHistory()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        await fixture.Start("127.0.0.1");
        var table = Control<DataGrid>(window, "HopTable");
        var timeline = Control<LatencyTimeline>(window, "Timeline");
        var plot = Control<HopLatencyPlot>(window, "RoutePlot");

        Assert.Equal("127.0.0.1", Control<TextBlock>(window, "TargetTitle").Text);
        Assert.Equal("1", Control<TextBlock>(window, "TargetCount").Text);
        var rows = table.ItemsSource!.Cast<HopItem>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("192.0.2.1", rows[0].Hop.Address);
        Assert.Equal(100d, rows[1].Hop.LossPercent);
        Assert.Equal(12d, rows[2].Hop.LastMs);
        Assert.Equal(3, plot.Rows.Count);
        Assert.Equal(100d, plot.Rows[1].LossPercent);
        Assert.All(timeline.Samples, sample => Assert.Equal(12d, sample.Latency));

        table.SelectedItem = rows[1];
        Assert.Equal(2, plot.SelectedHop);
        Assert.StartsWith("Hop 2", Control<TextBlock>(window, "GraphHeading").Text);
        Assert.NotEmpty(timeline.Samples);
        Assert.All(timeline.Samples, sample => { Assert.True(sample.Lost); Assert.Null(sample.Latency); });

        // Selecting a hop through the plot must update the table and graph together.
        plot.HopSelected!(1);
        Assert.Equal(1, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
        Assert.Equal(1, plot.SelectedHop);
        Assert.All(timeline.Samples, sample => Assert.Equal(2.5d, sample.Latency));
        Control<ComboBox>(window, "HorizonInput").SelectedIndex = 2;
        Assert.Equal(TimeSpan.FromMinutes(10), timeline.Horizon);
    }

    [AvaloniaFact]
    public async Task PauseResumeAndStopButtonsControlCollection()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        var session = Assert.Single(fixture.Engine.Sessions);
        await Until(() => session.LatestSnapshot!.Round >= 2);
        var pause = Control<Button>(fixture.Window, "StartButton");
        var stop = Control<MenuItem>(fixture.Window, "StopTraceAction");

        Click(pause);
        Assert.Equal(SessionState.Paused, session.State);
        Assert.Equal("▶", pause.Content);
        // Allow an already-started round to finish before checking that collection is suspended.
        await Task.Delay(180);
        var pausedRound = session.LatestSnapshot!.Round;
        await Task.Delay(260);
        Assert.Equal(pausedRound, session.LatestSnapshot!.Round);

        Click(pause);
        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal("Ⅱ", pause.Content);
        await Until(() => session.LatestSnapshot!.Round > pausedRound);
        Click(stop);
        await Until(() => session.State == SessionState.Stopped && !stop.IsEnabled && pause.IsEnabled);
        Assert.True(pause.IsEnabled);
        Assert.Equal("▶", pause.Content);
        Assert.NotEmpty(Control<LatencyTimeline>(fixture.Window, "Timeline").Samples);
        var finalRound = session.LatestSnapshot!.Round;
        await Task.Delay(180);
        Assert.Equal(finalRound, session.LatestSnapshot!.Round);
    }

    [AvaloniaFact]
    public async Task MultipleTargetsRemainIndependentAndSelectionChangesDisplayedSamples()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        await fixture.Start("127.0.0.2");
        var list = Control<ListBox>(fixture.Window, "TargetList");
        var items = list.ItemsSource!.Cast<TargetItem>().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("2", Control<TextBlock>(fixture.Window, "TargetCount").Text);
        var timeline = Control<LatencyTimeline>(fixture.Window, "Timeline");
        Assert.All(timeline.Samples, sample => Assert.Equal(22d, sample.Latency));

        Click(Control<Button>(fixture.Window, "StartButton"));
        Assert.Equal(SessionState.Paused, items[1].Session!.State);
        Assert.Equal(SessionState.Running, items[0].Session!.State);
        list.SelectedItem = items[0];
        Assert.Equal("127.0.0.1", Control<TextBlock>(fixture.Window, "TargetTitle").Text);
        Assert.Equal("Ⅱ", Control<Button>(fixture.Window, "StartButton").Content);
        Assert.All(timeline.Samples, sample => Assert.Equal(12d, sample.Latency));
        list.SelectedItem = items[1];
        Assert.Equal("127.0.0.2", Control<TextBlock>(fixture.Window, "TargetTitle").Text);
        Assert.Equal("▶", Control<Button>(fixture.Window, "StartButton").Content);
        Assert.All(timeline.Samples, sample => Assert.Equal(22d, sample.Latency));
    }

    [AvaloniaFact]
    public async Task CompactRouteRowsAlignWithLatencyPlotAndLeaveSpaceForHistory()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Width = 1180;
        window.Height = 790;
        await fixture.Start("127.0.0.1");
        await fixture.Start("127.0.0.2");
        var table = Control<DataGrid>(window, "HopTable");
        var plot = Control<HopLatencyPlot>(window, "RoutePlot");
        var timeline = Control<LatencyTimeline>(window, "Timeline");
        await Task.Delay(100);
        window.UpdateLayout();

        var rows = table.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is HopItem).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.InRange(row.Bounds.Height, 18, 24));
        var plotOrigin = plot.TranslatePoint(default, window)!.Value;
        foreach (var row in rows)
        {
            var hop = Assert.IsType<HopItem>(row.DataContext).Hop.Hop;
            var center = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;
            var plotCenter = plotOrigin.Y + plot.HeaderHeight + (hop - 0.5) * plot.RowHeight - plot.VerticalOffset;
            Assert.InRange(Math.Abs(center - plotCenter), 0, 2);
        }

        Assert.True(timeline.Bounds.Width >= window.ClientSize.Width * 0.85,
            $"The historical graph is too narrow: {timeline.Bounds.Width} of {window.ClientSize.Width}.");
        Assert.True(timeline.Bounds.Height >= 260,
            $"The historical graph needs useful vertical space: {timeline.Bounds.Height}.");
        var tabs = Control<ListBox>(window, "TargetList").GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Assert.Equal(2, tabs.Length);
        Assert.InRange(Math.Abs(tabs[0].Bounds.Top - tabs[1].Bounds.Top), 0, 1);
        Assert.True(tabs[1].Bounds.Left >= tabs[0].Bounds.Right);
        Assert.True(tabs[1].TranslatePoint(new Point(tabs[1].Bounds.Width, 0), window)!.Value.X <= window.ClientSize.Width);
    }

    [AvaloniaFact]
    public async Task RouteTableDisplaysIpAndResolvedNameInSeparateCells()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        Click(Control<Button>(fixture.Window, "StartButton"));
        var table = Control<DataGrid>(fixture.Window, "HopTable");
        var original = table.ItemsSource!.Cast<HopItem>().First();
        var named = new HopItem(original.Hop with { Hostname = "test.router.example" });
        table.ItemsSource = new[] { named };
        fixture.Window.UpdateLayout();

        Assert.Equal(new[] { "Hop", "Count", "IP", "Name", "Avg", "Min", "Cur", "Jit", "PL%" },
            table.Columns.Select(column => column.Header?.ToString()).ToArray());
        var cells = table.GetVisualDescendants().OfType<DataGridCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text).ToArray();
        Assert.Contains("192.0.2.1", cells);
        Assert.Contains("test.router.example", cells);
        Assert.DoesNotContain(cells, text => text is not null && text.Contains("192.0.2.1") && text.Contains("test.router.example"));
        Assert.Equal(original.Hop.Sent.ToString(), cells[1]);
    }

    [AvaloniaFact]
    public async Task HeldFocusAndHistoryBrowsingStayFixedWhileMonitoringContinues()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        var window = fixture.Window;
        var timeline = Control<LatencyTimeline>(window, "Timeline");
        var focus = Control<ComboBox>(window, "FocusInput");
        var slider = Control<Slider>(window, "HistorySlider");
        await Until(() => timeline.Samples.Count >= 3);

        focus.SelectedIndex = 2;
        var heldEnd = timeline.EndTime;
        var initialRound = Assert.Single(fixture.Engine.Sessions).LatestSnapshot!.Round;
        await Until(() => Assert.Single(fixture.Engine.Sessions).LatestSnapshot!.Round >= initialRound + 3);
        Assert.Equal(heldEnd, timeline.EndTime);
        Assert.Equal(SessionState.Running, Assert.Single(fixture.Engine.Sessions).State);
        Control<ComboBox>(window, "HorizonInput").SelectedIndex = 3;
        Assert.Equal(TimeSpan.FromMinutes(15), timeline.Horizon);
        Assert.Equal(heldEnd, timeline.EndTime);

        focus.SelectedIndex = 0;
        Assert.True(timeline.EndTime > heldEnd);
        slider.Value = 25;
        Assert.Equal(2, focus.SelectedIndex);
        var browsedEnd = timeline.EndTime;
        Assert.True(browsedEnd < timeline.Samples[^1].Time);
        initialRound = Assert.Single(fixture.Engine.Sessions).LatestSnapshot!.Round;
        await Until(() => Assert.Single(fixture.Engine.Sessions).LatestSnapshot!.Round >= initialRound + 3);
        Assert.Equal(browsedEnd, timeline.EndTime);

        Click(window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "Live"));
        Assert.Equal(0, focus.SelectedIndex);
        Assert.Equal(100d, slider.Value);
        Assert.True(timeline.EndTime > browsedEnd);
        Assert.Equal("LIVE", Control<TextBlock>(window, "HistoryPosition").Text);
    }

    [AvaloniaFact]
    public async Task InvalidTargetReportsFailureWithoutCreatingAFalseTrace()
    {
        await using var fixture = new WindowFixture();
        Control<TextBox>(fixture.Window, "TargetInput").Text = "";
        Click(Control<Button>(fixture.Window, "StartButton"));
        Assert.Empty(fixture.Engine.Sessions);
        Assert.Contains("Enter a hostname", Control<TextBlock>(fixture.Window, "StatusText").Text);
        Assert.Empty(Control<LatencyTimeline>(fixture.Window, "Timeline").Samples);
        Assert.True(Control<Button>(fixture.Window, "StartButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task ToolbarSettingsChangeOnlySelectedTargetAndSelectionRestoresItsSettings()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        await fixture.Start("127.0.0.2");
        var list = Control<ListBox>(fixture.Window, "TargetList");
        var items = list.ItemsSource!.Cast<TargetItem>().ToArray();
        var secondOptions = items[1].Session!.Options;
        var interval = Control<ComboBox>(fixture.Window, "IntervalInput");
        var timeout = Control<ComboBox>(fixture.Window, "TimeoutInput");
        var maxHops = Control<NumericUpDown>(fixture.Window, "MaxHopsInput");

        list.SelectedItem = items[0];
        // The injected engine's 100 ms interval has no matching production dropdown item.
        Assert.Equal(-1, interval.SelectedIndex);
        interval.SelectedIndex = 2;
        timeout.SelectedIndex = 2;
        maxHops.Value = 4;
        Assert.Equal(TimeSpan.FromSeconds(5), items[0].Session!.Options.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2), items[0].Session!.Options.Timeout);
        Assert.Equal(4, items[0].Session!.Options.MaxHops);
        Assert.Equal(secondOptions, items[1].Session!.Options);

        list.SelectedItem = items[1];
        Assert.Equal(-1, interval.SelectedIndex);
        Assert.Equal(1, timeout.SelectedIndex);
        Assert.Equal(3m, maxHops.Value);
        Assert.Equal(secondOptions, items[1].Session!.Options);
        list.SelectedItem = items[0];
        Assert.Equal(2, interval.SelectedIndex);
        Assert.Equal(2, timeout.SelectedIndex);
        Assert.Equal(4m, maxHops.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), items[0].Session!.Options.Interval);
    }

    [AvaloniaFact]
    public async Task SavedSessionReloadsIntoTableAndGraphAndCsvPreservesMissingReplies()
    {
        await using var fixture = new WindowFixture();
        await fixture.Start("127.0.0.1");
        var session = Assert.Single(fixture.Engine.Sessions);
        await Until(() => session.LatestSnapshot!.Round >= 3);
        Click(Control<MenuItem>(fixture.Window, "StopTraceAction"));
        await Until(() => session.State == SessionState.Stopped);
        var saved = Assert.Single(await fixture.Engine.Storage.LoadSessionsAsync());
        Assert.NotNull(saved.StoppedAt);
        Assert.True(saved.SampleCount >= 12);
        using var writer = new StringWriter();
        await fixture.Engine.Storage.ExportCsvAsync(saved.Id, writer);
        var csvLines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(saved.SampleCount + 1, csvLines.LongLength);
        Assert.StartsWith("Session,Round,Timestamp,Hop,Address,Destination,LatencyMs,Status", csvLines[0]);
        var timeoutRow = csvLines.Skip(1).Select(line => line.Trim().Split(',')).First(fields => fields[3] == "2");
        Assert.Equal("", timeoutRow[6]);
        Assert.Equal("Timeout", timeoutRow[7]);

        await fixture.Reopen();
        // Invoke the same asynchronous loader used by the history picker without platform file dialogs.
        var loader = typeof(MainWindow).GetMethod("LoadHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(loader.Invoke(fixture.Window, [saved]));
        var item = Assert.IsType<TargetItem>(Control<ListBox>(fixture.Window, "TargetList").SelectedItem);
        Assert.True(item.IsHistorical);
        Assert.Null(item.Session);
        Assert.Equal(saved.Id, item.Id);
        Assert.Equal("127.0.0.1", Control<TextBlock>(fixture.Window, "TargetTitle").Text);
        Assert.False(Control<Button>(fixture.Window, "StartButton").IsEnabled);
        Assert.False(Control<MenuItem>(fixture.Window, "StopTraceAction").IsEnabled);
        Assert.True(Control<Button>(fixture.Window, "ExportButton").IsEnabled);
        Assert.Equal("SAVED", Control<TextBlock>(fixture.Window, "HistoryPosition").Text);
        var table = Control<DataGrid>(fixture.Window, "HopTable");
        var hops = table.ItemsSource!.Cast<HopItem>().ToArray();
        Assert.Equal(3, hops.Length);
        Assert.Equal(100d, hops[1].Hop.LossPercent);
        var timeline = Control<LatencyTimeline>(fixture.Window, "Timeline");
        Assert.Equal(saved.SampleCount / 4, timeline.Samples.Count);
        Assert.All(timeline.Samples, sample => Assert.Equal(12d, sample.Latency));
        table.SelectedItem = hops[1];
        Assert.All(timeline.Samples, sample => Assert.True(sample.Lost));
        Control<Slider>(fixture.Window, "HistorySlider").Value = 25;
        Assert.NotEqual("SAVED", Control<TextBlock>(fixture.Window, "HistoryPosition").Text);
        Assert.True(timeline.EndTime < item.Snapshot!.Timestamp);
        var browsedTime = timeline.EndTime;
        Control<ComboBox>(fixture.Window, "HorizonInput").SelectedIndex = 3;
        Assert.Equal(TimeSpan.FromMinutes(15), timeline.Horizon);
        Assert.Equal(browsedTime, timeline.EndTime);
        table.SelectedItem = hops[0];
        Assert.Equal(browsedTime, timeline.EndTime);
        Assert.All(timeline.Samples, sample => Assert.Equal(2.5d, sample.Latency));
        var live = fixture.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content?.ToString() == "Live");
        Click(live);
        Assert.Equal(100d, Control<Slider>(fixture.Window, "HistorySlider").Value);
        Assert.Equal("SAVED", Control<TextBlock>(fixture.Window, "HistoryPosition").Text);
        Assert.Equal(timeline.Samples[^1].Time, timeline.EndTime);
    }

    private static T Control<T>(MainWindow window, string name) where T : Avalonia.Controls.Control
        => window.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control: {name}");

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Click(MenuItem item)
    {
        Assert.True(item.IsEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static async Task Until(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("UI did not reach the expected state within five seconds.");
            await Task.Delay(20);
        }
    }

    private sealed class WindowFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public TraceEngine Engine { get; private set; }
        public MainWindow Window { get; private set; }

        public WindowFixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate the project directory for test data.");
            _directory = Path.Combine(root.FullName, "artifacts", "tests", "ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"), transport: new MeasuredRouteTransport());
            Window = new MainWindow(["--data-dir", _directory], Engine);
            Control<NumericUpDown>(Window, "MaxHopsInput").Value = 3;
            Control<ComboBox>(Window, "IntervalInput").SelectedIndex = 0;
            Window.Show();
        }

        public async Task Start(string target)
        {
            Control<TextBox>(Window, "TargetInput").Text = target;
            Click(Control<Button>(Window, "StartButton"));
            await Until(() => Engine.Sessions.Any(s => s.Target == target && s.LatestSnapshot is not null)
                && Control<TextBlock>(Window, "TargetTitle").Text == target
                && Control<LatencyTimeline>(Window, "Timeline").Samples.Count > 0);
            var session = Engine.Sessions.Single(s => s.Target == target);
            Engine.UpdateOptions(session.Id, session.Options with { Interval = TimeSpan.FromMilliseconds(100), ResolveHostnames = false });
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Directory.Delete(_directory, true);
        }

        public async Task Reopen()
        {
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"), transport: new MeasuredRouteTransport());
            Window = new MainWindow(["--data-dir", _directory], Engine);
            Window.Show();
        }
    }

    private sealed class MeasuredRouteTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latency = destination.ToString() == "127.0.0.2" ? 22d : 12d;
            return Task.FromResult(ttl switch
            {
                1 => new ProbeResult("192.0.2.1", 2.5, ProbeStatus.TimeExceeded),
                2 => new ProbeResult(null, null, ProbeStatus.Timeout),
                _ => new ProbeResult(destination.ToString(), latency, ProbeStatus.Success)
            });
        }
    }
}
