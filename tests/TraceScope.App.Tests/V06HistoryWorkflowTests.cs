using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class V06HistoryWorkflowTests
{
    [AvaloniaFact]
    public async Task RepeatedHeldWindowRefreshesReuseLoadedDataInsteadOfIssuingAnotherQuery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTarget("127.0.0.1");
        fixture.Engine.Pause(target.Id);
        var range = (await fixture.Engine.Storage.LoadTimeRangeAsync(target.Id))!.Value;
        fixture.Timeline.Viewport!.SelectRange(range.First, range.Last);
        await fixture.LoadGraph();
        var source = Field<CancellationTokenSource>(fixture.Window, "_historyQuery");
        var points = fixture.Timeline.Samples;
        await InvokeTask(fixture.Window, "LoadDiskGraphAsync", false);
        Assert.Same(source, Field<CancellationTokenSource>(fixture.Window, "_historyQuery"));
        Assert.Same(points, fixture.Timeline.Samples);
        Assert.Equal(SessionState.Paused, target.Session!.State);
    }

    [AvaloniaFact]
    public async Task ActualGraphIndicatorPointerClickTogglesGraphWithoutSelectingItsRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        fixture.Window.UpdateLayout();
        var table = Find<DataGrid>(fixture.Window, "HopTable");
        // Establish a user selection explicitly; a newly laid out DataGrid may have no selected row.
        var destinationRow = table.GetVisualDescendants().OfType<DataGridRow>()
            .Single(r => r.DataContext is HopItem { Hop.Hop: 3 });
        var rowPoint = destinationRow.TranslatePoint(new Point(300, destinationRow.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseDown(rowPoint, MouseButton.Left);
        fixture.Window.MouseUp(rowPoint, MouseButton.Left);
        Assert.Equal(3, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
        var selected = Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop;
        fixture.Window.UpdateLayout();
        var indicator = table.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("graph-toggle") && b.DataContext is HopItem { Hop.Hop: 1 });
        var point = indicator.TranslatePoint(new Point(indicator.Bounds.Width / 2, indicator.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Assert.Single(Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs"));
        Assert.True(Assert.IsType<HopItem>(indicator.DataContext).GraphVisible);
        Assert.Equal("#ff9fd5ff", indicator.Foreground!.ToString());
        Assert.Equal(selected, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
        fixture.Window.UpdateLayout();
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Assert.Empty(Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs"));
        Assert.False(Assert.IsType<HopItem>(indicator.DataContext).GraphVisible);
        Assert.Equal("#ff646c77", indicator.Foreground!.ToString());
        Assert.Equal(selected, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
        // Rapid input must update the current indicator without waiting for the next probe or query.
        for (var index = 0; index < 8; index++)
        {
            fixture.Window.MouseMove(point);
            fixture.Window.MouseDown(point, MouseButton.Left);
            fixture.Window.MouseUp(point, MouseButton.Left);
            var visible = index % 2 == 0;
            Assert.Equal(visible, Assert.IsType<HopItem>(indicator.DataContext).GraphVisible);
            Assert.Equal(visible ? "#ff9fd5ff" : "#ff646c77", indicator.Foreground!.ToString());
            Assert.Equal(visible ? 1 : 0, Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs").Count);
            Assert.Equal(selected, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
            fixture.Window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public async Task DestinationIndicatorReopensHiddenDefaultGraphOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTarget("127.0.0.1");
        var before = await fixture.Engine.Storage.LoadSamplesAsync(target.Id);
        var panel = Find<StackPanel>(fixture.Window, "HistoryGraphsPanel");
        LatencyTimeline[] VisibleGraphs() => panel.GetVisualDescendants().OfType<LatencyTimeline>()
            .Where(g => g.IsEffectivelyVisible).ToArray();
        Assert.Single(VisibleGraphs());
        Invoke(fixture.Window, "ClosePrimaryHistoryGraph", null, new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(VisibleGraphs());
        fixture.Window.UpdateLayout();
        var indicator = Find<DataGrid>(fixture.Window, "HopTable").GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("graph-toggle") && b.DataContext is HopItem { Hop.IsDestination: true });
        void ClickIndicator()
        {
            var point = indicator.TranslatePoint(new Point(indicator.Bounds.Width / 2, indicator.Bounds.Height / 2), fixture.Window)!.Value;
            fixture.Window.MouseMove(point);
            fixture.Window.MouseDown(point, MouseButton.Left);
            fixture.Window.MouseUp(point, MouseButton.Left);
            fixture.Window.UpdateLayout();
        }
        ClickIndicator();
        Assert.Same(fixture.Timeline, Assert.Single(VisibleGraphs()));
        Assert.True(Assert.IsType<HopItem>(indicator.DataContext).GraphVisible);
        Assert.Empty(Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs"));
        // Row selection and refreshed history must reuse the same graph identity.
        Invoke(fixture.Window, "SelectPrimaryHistoryGraph", 3);
        await fixture.LoadGraph();
        Assert.Single(VisibleGraphs());
        ClickIndicator();
        Assert.Empty(VisibleGraphs());
        ClickIndicator();
        Assert.Single(VisibleGraphs());
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        fixture.Window.UpdateLayout();
        Assert.Equal(2, VisibleGraphs().Length);
        var other = Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs")[1];
        Assert.Same(fixture.Timeline.Viewport, other.Viewport);
        fixture.Timeline.Viewport!.Zoom(.5, .4);
        Assert.Equal(fixture.Timeline.Horizon, other.Horizon);
        Invoke(fixture.Window, "ResizeHistoryGraphPair", 0, 1, 10d);
        Assert.Equal(2, VisibleGraphs().Length);
        Assert.Equal(SessionState.Running, target.Session!.State);
        Assert.True((await fixture.Engine.Storage.LoadSamplesAsync(target.Id)).Count >= before.Count);
    }

    [AvaloniaFact]
    public async Task RestoredDestinationHopAliasUsesOneSessionGraphAndKeepsSizingPreference()
    {
        await using var fixture = await Fixture.CreateAsync();
        typeof(MainWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window,
            new WorkspaceConfiguration
            {
                HistoryLayout = new() { Targets = [new("127.0.0.1", 3, true, [0, 3], new Dictionary<int, double> { [3] = 1.5 })] }
            });
        var first = await fixture.AddTarget("127.0.0.1");
        fixture.Window.UpdateLayout();
        var panel = Find<StackPanel>(fixture.Window, "HistoryGraphsPanel");
        Assert.Single(panel.GetVisualDescendants().OfType<LatencyTimeline>(), g => g.IsEffectivelyVisible);
        var saved = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        var layout = Assert.Single(saved.HistoryLayout.Targets);
        Assert.Equal(0, layout.PrimaryHop);
        Assert.Equal(new[] { 0 }, layout.PinnedHops);
        Assert.Equal(1.5, layout.GraphWeights![0]);
        var second = await fixture.AddTarget("127.0.0.2");
        Assert.Single(panel.GetVisualDescendants().OfType<LatencyTimeline>(), g => g.IsEffectivelyVisible);
        Find<ListBox>(fixture.Window, "TargetList").SelectedItem = first;
        Assert.Single(panel.GetVisualDescendants().OfType<LatencyTimeline>(), g => g.IsEffectivelyVisible);
        Assert.All(fixture.Engine.Sessions, session => Assert.Equal(SessionState.Running, session.State));
        Invoke(fixture.Window, "ToggleHistoryGraph", 3);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<LatencyTimeline>(), g => g.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task SelectionUsesStoredProbesWhileMonitoringContinuesAndClearingRestoresSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTarget("127.0.0.1");
        await Until(() => target.Session!.LatestSnapshot!.Round >= 3);
        var stored = await fixture.Engine.Storage.LoadSamplesAsync(target.Id);
        var timestamp = stored.First().Timestamp;
        fixture.Timeline.Viewport!.SelectRange(timestamp, timestamp);
        await fixture.LoadGraph();
        var expected = await fixture.Engine.Storage.LoadSelectionStatisticsAsync(target.Id, timestamp, timestamp);
        var rows = fixture.Rows;
        Assert.Equal(expected.Count(x => x.Hop > 0), rows.Length);
        foreach (var row in rows)
        {
            var stats = expected.Single(x => x.Hop == row.Hop.Hop && x.Address == row.Hop.Address);
            Assert.Equal(stats.Sent, row.Hop.Sent);
            Assert.Equal(stats.AverageMs, row.Hop.AverageMs);
            Assert.Equal(stats.LossPercent, row.Hop.LossPercent);
            Assert.Equal(stats.LastMs, row.Hop.LastMs);
        }
        Assert.Equal(100, rows.Single(x => x.Hop.Hop == 2).Hop.LossPercent);
        Assert.Equal("1", Find<TextBlock>(fixture.Window, "FooterCount").Text);
        var round = target.Session!.LatestSnapshot!.Round;
        await Until(() => target.Session.LatestSnapshot!.Round > round);
        Assert.Equal(SessionState.Running, target.Session.State);
        Assert.All(fixture.Rows, row => Assert.Equal(1, row.Sent));
        Invoke(fixture.Window, "ClearHistorySelection", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Rows.All(row => row.Sent >= 3));
        Assert.Null(fixture.Timeline.Viewport.SelectionStart);
        Assert.True((await fixture.Engine.Storage.LoadSamplesAsync(target.Id)).Count >= stored.Count);
    }

    [AvaloniaFact]
    public async Task GraphVisibilitySharesTimelineAndSelectionWithoutDuplicateGraphs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var target = await fixture.AddTarget("127.0.0.1");
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        var extra = Field<Dictionary<int, LatencyTimeline>>(fixture.Window, "_additionalGraphs");
        Assert.Single(extra);
        Assert.Same(fixture.Timeline.Viewport, extra[1].Viewport);
        var time = (await fixture.Engine.Storage.LoadSamplesAsync(target.Id)).First().Timestamp;
        fixture.Timeline.Viewport!.SelectRange(time, time);
        await fixture.LoadGraph();
        Assert.Equal(time, extra[1].Viewport!.SelectionStart);
        Assert.NotEmpty(extra[1].Samples);
        fixture.Timeline.Viewport.Zoom(.5, .5);
        Assert.Equal(fixture.Timeline.Horizon, extra[1].Horizon);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        Assert.Empty(extra);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        Assert.Single(extra);
        Assert.Equal(SessionState.Running, target.Session!.State);
        Invoke(fixture.Window, "GoLive", null, new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(fixture.Timeline.Viewport.EndTime);
        Assert.Null(extra[1].Viewport!.SelectionStart);
    }

    [AvaloniaFact]
    public async Task SwitchingTargetsPreservesEachTimelineAndSelectedInterval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddTarget("127.0.0.1");
        var firstViewport = fixture.Timeline.Viewport!;
        var timestamp = (await fixture.Engine.Storage.LoadSamplesAsync(first.Id)).First().Timestamp;
        firstViewport.SelectRange(timestamp, timestamp);
        firstViewport.Horizon = TimeSpan.FromSeconds(30);
        await fixture.LoadGraph();
        var second = await fixture.AddTarget("127.0.0.2");
        var secondViewport = fixture.Timeline.Viewport!;
        Assert.NotSame(firstViewport, secondViewport);
        Assert.Null(secondViewport.SelectionStart);
        Assert.Null(secondViewport.EndTime);
        Find<ListBox>(fixture.Window, "TargetList").SelectedItem = first;
        Assert.Same(firstViewport, fixture.Timeline.Viewport);
        Assert.Equal(timestamp, firstViewport.SelectionStart);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Timeline.Horizon);
        Assert.All(fixture.Rows, row => Assert.Equal(1, row.Sent));
        Find<ListBox>(fixture.Window, "TargetList").SelectedItem = second;
        Assert.Same(secondViewport, fixture.Timeline.Viewport);
        Assert.Null(secondViewport.SelectionStart);
        Assert.All(fixture.Engine.Sessions, session => Assert.Equal(SessionState.Running, session.State));
    }

    [AvaloniaFact]
    public async Task SavedSessionSelectionAndNavigationLeaveOtherTargetRunning()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddTarget("127.0.0.1");
        var second = await fixture.AddTarget("127.0.0.2");
        await InvokeTask(fixture.Window, "CloseTargetAsync", first);
        var saved = (await fixture.Engine.Storage.LoadSessionsAsync()).Single(x => x.Id == first.Id);
        await InvokeTask(fixture.Window, "LoadHistory", saved);
        Assert.True(Assert.IsType<TargetItem>(Find<ListBox>(fixture.Window, "TargetList").SelectedItem).IsHistorical);
        await fixture.LoadGraph();
        var range = (await fixture.Engine.Storage.LoadTimeRangeAsync(first.Id))!.Value;
        fixture.Timeline.Viewport!.SelectRange(range.First, range.Last);
        await fixture.LoadGraph();
        Assert.NotEmpty(fixture.Rows);
        Assert.All(fixture.Rows, row => Assert.True(row.Sent > 0));
        fixture.Timeline.Viewport.Pan(TimeSpan.FromDays(-1));
        Assert.InRange(fixture.Timeline.Viewport.EffectiveEnd, range.First, range.Last);
        Invoke(fixture.Window, "GoLive", null, new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(range.Last, fixture.Timeline.Viewport.EffectiveEnd);
        Assert.Null(fixture.Timeline.Viewport.SelectionStart);
        var round = second.Session!.LatestSnapshot!.Round;
        await Until(() => second.Session.LatestSnapshot!.Round > round);
        Assert.Equal(SessionState.Running, second.Session.State);
        Assert.Equal(saved.SampleCount, (await fixture.Engine.Storage.LoadSessionsAsync()).Single(x => x.Id == first.Id).SampleCount);
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static Task InvokeTask(MainWindow window, string name, params object?[] args)
        => Assert.IsAssignableFrom<Task>(Invoke(window, name, args));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Historical workflow did not complete.");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "TraceScope-v06-tests", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public LatencyTimeline Timeline => Find<LatencyTimeline>(Window, "Timeline");
        public HopItem[] Rows => Find<DataGrid>(Window, "HopTable").ItemsSource!.Cast<HopItem>().ToArray();
        private Fixture()
        {
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"), transport: new MeasuredTransport());
            Window = new MainWindow(["--data-dir", _directory], Engine);
            Window.Show();
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await InvokeTask(fixture.Window, "InitializeProfessionalOnOpenAsync");
            return fixture;
        }
        public async Task<TargetItem> AddTarget(string address)
        {
            await InvokeTask(Window, "AddTarget", address, new ProbeOptions
            { MaxHops = 3, Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            await Until(() => Engine.Sessions.Single(x => x.Target == address).LatestSnapshot is not null
                && Find<ListBox>(Window, "TargetList").SelectedItem is TargetItem { Snapshot: not null } selected
                && selected.Target == address && Rows.Length == 3 && Timeline.Samples.Count > 0);
            return Assert.IsType<TargetItem>(Find<ListBox>(Window, "TargetList").SelectedItem);
        }
        public Task LoadGraph() => InvokeTask(Window, "LoadDiskGraphAsync", true);
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            System.IO.Directory.Delete(_directory, true);
        }
    }
    private sealed class MeasuredTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : ttl == 2 ? new ProbeResult(null, null, ProbeStatus.Timeout)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
