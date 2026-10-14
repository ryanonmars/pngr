using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class V061LayoutTests
{
    [AvaloniaFact]
    public async Task OneTwoAndThreeGraphsShareAvailablePanelWithoutOverflow()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 1080;
        await Task.Delay(30);
        fixture.Layout();
        AssertFitted(fixture, 1);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        fixture.Layout();
        AssertFitted(fixture, 2);
        Invoke(fixture.Window, "ToggleHistoryGraph", 2);
        fixture.Layout();
        AssertFitted(fixture, 3);
        Assert.All(fixture.Graphs, graph => Assert.Same(fixture.Timeline.Viewport, graph.Viewport));
    }

    [AvaloniaFact]
    public async Task FourGraphsFitAtDefaultWindowHeight()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 790;
        await Task.Delay(30);
        foreach (var hop in new[] { 1, 2, 4 }) Invoke(fixture.Window, "ToggleHistoryGraph", hop);
        fixture.Layout();
        AssertFitted(fixture, 4);
        Assert.All(fixture.Graphs, graph => Assert.True(graph.Bounds.Height >= 54));
    }

    [AvaloniaFact]
    public async Task ActualPointerDragsResizePanelAndIndividualGraphDividers()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 1000;
        await Task.Delay(30);
        fixture.Layout();
        var divider = Find<GridSplitter>(fixture.Window, "HistoryPanelDivider");
        var initial = fixture.Frames.Single().Height;
        Drag(fixture.Window, divider, -45);
        fixture.Layout();
        Assert.True(fixture.Frames.Single().Height > initial);
        var point = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left); fixture.Window.MouseUp(point, MouseButton.Left);
        fixture.Window.MouseDown(point, MouseButton.Left); fixture.Window.MouseUp(point, MouseButton.Left);
        fixture.Layout();
        var configuration = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        Assert.Equal(.68, configuration.HistoryLayout.PanelFraction, 3);
        await Task.Delay(550); // Start a fresh click sequence after the double-click reset.
        Drag(fixture.Window, divider, -30);
        fixture.Layout();
        var resized = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        var analysis = Find<Grid>(fixture.Window, "AnalysisGrid");
        var total = analysis.RowDefinitions[0].ActualHeight + analysis.RowDefinitions[2].ActualHeight;
        Assert.Equal(analysis.RowDefinitions[2].ActualHeight / total, resized.HistoryLayout.PanelFraction, 3);
        Assert.True(resized.HistoryLayout.PanelFraction > .6);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        fixture.Layout();
        var primaryBefore = fixture.Frames[0].Height;
        var graphDivider = Find<StackPanel>(fixture.Window, "HistoryGraphsPanel").Children.OfType<Thumb>().Single();
        Drag(fixture.Window, graphDivider, 25);
        fixture.Layout();
        Assert.True(fixture.Frames[0].Height > primaryBefore);
        Assert.Same(fixture.Timeline.Viewport, fixture.Graphs[1].Viewport);
    }

    private static void Drag(Window window, Control divider, double delta)
    {
        var point = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseMove(new Point(point.X, point.Y + delta), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(point.X, point.Y + delta), MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task WindowAndPanelResizingReallocateGraphHeightsAndResetRestoresDefault()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 1000;
        await Task.Delay(30);
        fixture.Layout();
        var initial = fixture.Frames.Single().Height;
        fixture.Window.Height = 1200;
        await Task.Delay(30);
        fixture.Layout();
        Assert.True(fixture.Frames.Single().Height > initial);
        Invoke(fixture.Window, "ApplyHistoryPanelFraction", .7);
        fixture.Layout();
        var expanded = fixture.Frames.Single().Height;
        Invoke(fixture.Window, "ApplyHistoryPanelFraction", .3);
        fixture.Layout();
        Assert.True(fixture.Frames.Single().Height < expanded);
        Invoke(fixture.Window, "ResetHistoryPanelLayout", null, null);
        fixture.Layout();
        var configuration = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        var layout = configuration.GetType().GetProperty("HistoryLayout")!.GetValue(configuration)!;
        Assert.Equal(.68, (double)layout.GetType().GetProperty("PanelFraction")!.GetValue(layout)!);
        AssertFitted(fixture, 1);
    }

    [AvaloniaFact]
    public async Task IndividualGraphSizingPreservesTotalHeightAndSharedTimeline()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 1200;
        await Task.Delay(30);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        fixture.Layout();
        var primaryBefore = ((Control)fixture.Timeline.Parent!).Bounds.Height;
        var other = fixture.Graphs.Single(g => !ReferenceEquals(g, fixture.Timeline));
        var otherBefore = ((Control)other.Parent!).Bounds.Height;
        Invoke(fixture.Window, "ResizeHistoryGraphPair", 0, 1, 40d);
        fixture.Layout();
        Assert.True(((Control)fixture.Timeline.Parent!).Bounds.Height > primaryBefore);
        Assert.True(((Control)other.Parent!).Bounds.Height < otherBefore);
        Assert.Equal(primaryBefore + otherBefore, ((Control)fixture.Timeline.Parent!).Bounds.Height + ((Control)other.Parent!).Bounds.Height, 1);
        fixture.Timeline.Viewport!.Zoom(.7, .4);
        Assert.Same(fixture.Timeline.Viewport, other.Viewport);
        Assert.Equal(fixture.Timeline.Horizon, other.Horizon);
        Invoke(fixture.Window, "ToggleHistoryGraph", 2);
        fixture.Layout();
        Assert.Equal(3, fixture.Frames.Length);
        Assert.All(fixture.Frames, frame => Assert.True(frame.Height > 80));
    }

    [AvaloniaFact]
    public async Task ExcessiveGraphsUseScrollableReadableFramesInsteadOfClipping()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 740;
        await Task.Delay(30);
        foreach (var hop in new[] { 1, 2, 4, 5, 6, 7, 8, 9 }) Invoke(fixture.Window, "ToggleHistoryGraph", hop);
        fixture.Layout();
        Assert.Equal(9, fixture.Frames.Length);
        Assert.All(fixture.Frames, frame => Assert.True(frame.Height >= 80));
        var scroll = Find<ScrollViewer>(fixture.Window, "HistoryGraphsScroll");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.All(fixture.Graphs, graph => Assert.Same(fixture.Timeline.Viewport, graph.Viewport));
    }

    [AvaloniaFact]
    public async Task WorkspaceRoundTripRestoresPanelGraphVisibilityAndRelativeSizes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Window.Height = 1200;
        await Task.Delay(30);
        Invoke(fixture.Window, "ApplyHistoryPanelFraction", .65);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        fixture.Layout();
        Invoke(fixture.Window, "ResizeHistoryGraphPair", 0, 1, 30d);
        fixture.Layout();
        var expectedRatio = ((Control)fixture.Timeline.Parent!).Bounds.Height / ((Control)fixture.Graphs.Single(g => !ReferenceEquals(g, fixture.Timeline)).Parent!).Bounds.Height;
        await InvokeTask(fixture.Window, "SaveWorkspaceNowAsync");
        var restored = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.NotNull(restored);
        Invoke(fixture.Window, "ApplyHistoryPanelFraction", .3);
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        await InvokeTask(fixture.Window, "ApplyWorkspaceAsync", restored);
        fixture.Layout();
        Assert.Equal(2, fixture.Frames.Length);
        var configuration = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        var layout = configuration.GetType().GetProperty("HistoryLayout")!.GetValue(configuration)!;
        Assert.Equal(.65, (double)layout.GetType().GetProperty("PanelFraction")!.GetValue(layout)!, 3);
        var restoredRatio = ((Control)fixture.Timeline.Parent!).Bounds.Height / ((Control)fixture.Graphs.Single(g => !ReferenceEquals(g, fixture.Timeline)).Parent!).Bounds.Height;
        Assert.Equal(expectedRatio, restoredRatio, 2);
        Assert.Single(fixture.Engine.Sessions);
    }

    [AvaloniaFact]
    public async Task SharedTimeControlsStayInFirstVisibleGraphHeaderAfterCloseAndResize()
    {
        await using var fixture = await Fixture.CreateAsync();
        Invoke(fixture.Window, "ToggleHistoryGraph", 1);
        Invoke(fixture.Window, "ToggleHistoryGraph", 2);
        fixture.Layout();
        var controls = Find<StackPanel>(fixture.Window, "HistoryTimeControls");
        Assert.Same(((Grid)fixture.Frames[0]).Children[0], controls.Parent);
        Assert.Equal(30, ((Grid)fixture.Frames[0]).RowDefinitions[0].ActualHeight);
        Assert.Null(fixture.Window.FindControl<TextBlock>("GraphHeading"));
        Invoke(fixture.Window, "HideHistoryGraph", 0);
        fixture.Layout();
        Assert.Same(((Grid)fixture.Frames[0]).Children[0], controls.Parent);
        Assert.Equal(2, fixture.Graphs.Length);
        Assert.Single(fixture.Window.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "HorizonInput");
        var viewport = fixture.Graphs[0].Viewport;
        Assert.All(fixture.Graphs, graph => Assert.Same(viewport, graph.Viewport));
        fixture.Window.Width = 980; fixture.Window.Height = 640;
        await Task.Delay(30);
        fixture.Layout();
        AssertFitted(fixture, 2);
        Assert.True(controls.Bounds.Width > 0);
    }

    private static void AssertFitted(Fixture fixture, int count)
    {
        var frames = fixture.Frames;
        Assert.Equal(count, frames.Length);
        var scroll = Find<ScrollViewer>(fixture.Window, "HistoryGraphsScroll");
        // Earlier frames are rounded down to device pixels; the last frame owns
        // their accumulated remainder (up to count-1 pixels at 100% scaling).
        var rounding = Math.Max(1, (count - 1) / fixture.Window.RenderScaling);
        Assert.All(frames, frame => Assert.InRange(frame.Height, frames[0].Height - rounding, frames[0].Height + rounding));
        Assert.InRange(frames.Sum(frame => frame.Height), scroll.Viewport.Height - (count - 1) * 12 - 2, scroll.Viewport.Height + 2);
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 2, $"Extent {scroll.Extent.Height}; viewport {scroll.Viewport.Height}; bounds {scroll.Bounds.Height}; frames {string.Join(',', frames.Select(f => f.Height))}");
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
    private static object? Invoke(MainWindow window, string name, params object?[] args)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static Task InvokeTask(MainWindow window, string name, params object?[] args)
        => Assert.IsAssignableFrom<Task>(Invoke(window, name, args));
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "TraceScope-v061-layout-tests", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public LatencyTimeline Timeline => Find<LatencyTimeline>(Window, "Timeline");
        public LatencyTimeline[] Graphs => Find<StackPanel>(Window, "HistoryGraphsPanel").GetVisualDescendants().OfType<LatencyTimeline>().Where(g => g.IsEffectivelyVisible).ToArray();
        public Control[] Frames => Graphs.Select(g => (Control)g.Parent!).ToArray();
        private Fixture()
        {
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new MeasuredTransport());
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Show();
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await InvokeTask(fixture.Window, "InitializeProfessionalOnOpenAsync");
            await InvokeTask(fixture.Window, "AddTarget", "127.0.0.1", new ProbeOptions
            { MaxHops = 3, Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (fixture.Engine.Sessions.Single().LatestSnapshot is null || Find<DataGrid>(fixture.Window, "HopTable").ItemsSource!.Cast<HopItem>().Count() != 3)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Layout fixture did not acquire a route.");
                await Task.Delay(20);
            }
            return fixture;
        }
        public void Layout()
        {
            Window.UpdateLayout();
            Invoke(Window, "SizeHistoryGraphs");
            Window.UpdateLayout();
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Window.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class MeasuredTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(ttl < 3 ? new ProbeResult($"192.0.2.{ttl}", ttl, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
