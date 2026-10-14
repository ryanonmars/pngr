using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class AllTargetsTests
{
    [AvaloniaFact]
    public async Task OverviewKeepsBothTargetsLiveAndRowSelectionControlsOnlyItsDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracescope-all-targets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var engine = new TraceEngine(Path.Combine(directory, "trace.db"), transport: new RouteTransport());
        var window = new MainWindow(["--data-dir", directory], engine);
        window.Show();
        try
        {
            var add = typeof(MainWindow).GetMethod("AddTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var options = new ProbeOptions { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), ResolveHostnames = false };
            await (Task)add.Invoke(window, ["127.0.0.1 127.0.0.2", options, null, false])!;
            await Until(() => engine.Sessions.Count == 2 && engine.Sessions.All(s => s.LatestSnapshot?.Round >= 2));
            Click(window.FindControl<Button>("AllTargetsButton")!);
            var pane = window.FindControl<AllTargetsView>("AllTargetsPane")!;
            Assert.True(pane.IsVisible);
            Assert.False(window.FindControl<Grid>("SingleTargetPane")!.IsVisible);
            Assert.Null(window.FindControl<ListBox>("TargetList")!.SelectedItem);
            Assert.Equal(new[] { "Status", "Count", "IP", "Name", "Setting", "Avg", "Min", "Cur", "PL%" },
                pane.Table.Columns.Take(9).Select(c => c.Header));
            Assert.Equal(2, pane.Rows.Count);
            var first = pane.Rows.Single(r => r.Item.Target == "127.0.0.1");
            var second = pane.Rows.Single(r => r.Item.Target == "127.0.0.2");
            Assert.Equal(12, first.Average);
            Assert.Equal(22, second.Average);
            Assert.True(first.Count >= 2);
            window.FindControl<ComboBox>("FocusInput")!.SelectedIndex = 2;
            var heldTime = window.FindControl<LatencyTimeline>("Timeline")!.EndTime;
            pane.Table.SelectedItem = first;
            Assert.True(pane.IsVisible);
            Assert.Equal(2, window.FindControl<ComboBox>("FocusInput")!.SelectedIndex);
            var viewport = window.FindControl<LatencyTimeline>("Timeline")!.Viewport!;
            Assert.NotNull(viewport.EndTime);
            Assert.InRange(viewport.EndTime!.Value, viewport.First!.Value, heldTime!.Value);
            Assert.Contains("127.0.0.1", window.FindControl<TextBlock>("PrimaryGraphHeader")!.Text);
            Assert.All(window.FindControl<LatencyTimeline>("Timeline")!.Samples, p => Assert.Equal(12, p.Latency));
            Click(window.FindControl<Button>("StartButton")!);
            Assert.Equal(SessionState.Paused, first.Item.Session!.State);
            Assert.Equal(SessionState.Running, second.Item.Session!.State);
            var round = second.Item.Snapshot!.Round;
            await Until(() => second.Item.Snapshot!.Round > round);
            Assert.True(pane.IsVisible);
            Click(window.FindControl<Button>("StartButton")!);
            Assert.Equal(SessionState.Running, first.Item.Session.State);
            window.FindControl<ListBox>("TargetList")!.SelectedItem = second.Item;
            Assert.False(pane.IsVisible);
            Assert.True(window.FindControl<Grid>("SingleTargetPane")!.IsVisible);
            Click(window.FindControl<Button>("AllTargetsButton")!);
            pane.Table.SelectedItem = first;
            window.UpdateLayout();
            var close = window.FindControl<ListBox>("TargetList")!.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("close-target") && ReferenceEquals(b.DataContext, first.Item));
            Click(close);
            await Until(() => pane.Rows.Count == 1);
            Assert.True(pane.IsVisible);
            Assert.Contains("127.0.0.2", window.FindControl<TextBlock>("PrimaryGraphHeader")!.Text);
            Assert.Equal(SessionState.Running, second.Item.Session.State);
            await (Task)add.Invoke(window, ["127.0.0.3", options, null, false])!;
            Assert.True(pane.IsVisible);
            Assert.Equal(2, pane.Rows.Count);
        }
        finally
        {
            window.Close();
            await Until(() => !window.IsVisible);
            await engine.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FocusStatisticsExcludeOtherHopsOldSamplesAndProbeErrors()
    {
        var end = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LatencySample(end.AddMinutes(-20), 999, ProbeStatus.Success),
            new LatencySample(end.AddSeconds(-4), 10, ProbeStatus.Success),
            new LatencySample(end.AddSeconds(-3), null, ProbeStatus.Timeout),
            new LatencySample(end.AddSeconds(-2), null, ProbeStatus.Error),
            new LatencySample(end.AddSeconds(-1), 20, ProbeStatus.Success),
            new LatencySample(end.AddSeconds(1), 999, ProbeStatus.Success)
        };
        var destination = new HopSnapshot(0, "127.0.0.1", "localhost", 999, 999, 1, 999, 0, 100, 99, 1, true, samples, ProbeStatus.Success);
        var item = new TargetItem(new StoredSession(Guid.NewGuid(), "127.0.0.1", "127.0.0.1", end, null, 6))
        { Snapshot = new RoundSnapshot(Guid.NewGuid(), "127.0.0.1", "127.0.0.1", end, 1, true, [], destination, TimeSpan.Zero) };
        var row = new AllTargetRow(item);
        row.Update(end, TimeSpan.FromMinutes(10), []);
        Assert.Equal(3, row.Count);
        Assert.Equal(15, row.Average);
        Assert.Equal(10, row.Minimum);
        Assert.Equal(20, row.Maximum);
        Assert.Equal(20, row.Current);
        Assert.Equal(100d / 3, row.Loss!.Value, 6);
        Assert.Equal("localhost", row.Name);
        row.Update(end.AddHours(1), TimeSpan.FromMinutes(1), []);
        Assert.Equal(0, row.Count);
        Assert.Null(row.Average);
        Assert.Equal("—", row.LossLabel);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class RouteTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 1, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), destination.ToString() == "127.0.0.1" ? 12 : 22, ProbeStatus.Success));
    }
}
