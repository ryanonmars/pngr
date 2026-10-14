using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class RoutePresentationTests
{
    [Fact]
    public void CollapsesOnlyLongTrailingUnansweredSequencesWithoutChangingSnapshots()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Hop(i, i <= 7)).ToArray();
        var result = RoutePresentation.Create(rows, false);
        Assert.Equal(7, result.Rows.Count);
        Assert.Equal(8, result.FirstHiddenHop);
        Assert.Equal(30, result.LastHiddenHop);
        Assert.Equal(23, result.UnansweredCount);
        Assert.All(result.Rows, row => Assert.Same(rows[row.Hop - 1], row));
        Assert.Equal(30, rows.Length);
        var expanded = RoutePresentation.Create(rows, true);
        Assert.Same(rows, expanded.Rows);
        Assert.Equal(result.UnansweredCount, expanded.UnansweredCount);
    }

    [Fact]
    public void InteriorMissingPositionsKnownRoutersAndLocalErrorsStayVisible()
    {
        var rows = new[] { Hop(1, true), Hop(2), Hop(3), Hop(4), Hop(5, true), Hop(6), Hop(7), Hop(8) };
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, RoutePresentation.Create(rows, false).Rows.Select(h => h.Hop));
        var known = rows[^1] with { Address = "192.0.2.8", Received = 1 };
        Assert.False(RoutePresentation.Create(rows[..^1].Append(known).ToArray(), false).HasTrailingGroup);
        Assert.False(RoutePresentation.Create(rows[..^1].Append(rows[^1] with { Status = ProbeStatus.Error }).ToArray(), false).HasTrailingGroup);
        Assert.False(RoutePresentation.Create([Hop(1), Hop(2)], false).HasTrailingGroup);
        Assert.False(RoutePresentation.Create([Hop(1), Hop(2), Hop(4)], false).HasTrailingGroup);
    }

    [Fact]
    public void RouteChangesUpdateTheGroupAndTerminalResponsesRemoveIt()
    {
        var unknown = Enumerable.Range(1, 12).Select(i => Hop(i, i <= 5)).ToArray();
        Assert.Equal(7, RoutePresentation.Create(unknown, false).UnansweredCount);
        var changed = unknown.Select(h => h.Hop == 8 ? Hop(8, true) : h).ToArray();
        Assert.Equal(9, RoutePresentation.Create(changed, false).FirstHiddenHop);
        var reached = changed.Take(9).Append(Hop(10, true) with { IsDestination = true }).ToArray();
        Assert.Same(reached, RoutePresentation.Create(reached, false).Rows);
        Assert.False(RoutePresentation.Create(reached, false).HasTrailingGroup);
    }

    [AvaloniaFact]
    public async Task ActualExpandControlPreservesSourceSnapshotAndSelectedHiddenHop()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracescope-route-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var engine = new TraceEngine(Path.Combine(directory, "history.db"));
        var window = new MainWindow(["--data-dir", directory], engine);
        try
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var rows = Enumerable.Range(1, 30).Select(i => Hop(i, i <= 7)).ToArray();
            var snapshot = new RoundSnapshot(id, "192.0.2.1", "192.0.2.1", now, 1, false, rows, Hop(0), TimeSpan.Zero);
            var target = new TargetItem(new StoredSession(id, "192.0.2.1", "192.0.2.1", now, now, 30)) { Snapshot = snapshot };
            Set(window, "_selected", target);
            Set(window, "_selectedHop", (int?)20);
            Invoke(window, "RefreshSelected");
            var table = window.FindControl<DataGrid>("HopTable")!;
            var button = window.FindControl<Button>("UnansweredHopsButton")!;
            Assert.True(button.IsVisible);
            Assert.Contains("Expand", button.Content!.ToString());
            Assert.Equal(7, table.ItemsSource.Cast<HopItem>().Count());
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(30, table.ItemsSource.Cast<HopItem>().Count());
            Assert.Equal(20, Assert.IsType<HopItem>(table.SelectedItem).Hop.Hop);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(7, table.ItemsSource.Cast<HopItem>().Count());
            Assert.Equal(20, Get<int?>(window, "_selectedHop"));
            Assert.Same(snapshot, target.Snapshot);
            Assert.Same(rows, target.Snapshot!.Hops);
        }
        finally { await (Task)Invoke(window, "ShutdownAsync")!; }
    }

    private static HopSnapshot Hop(int number, bool replied = false) => new(number, replied ? $"192.0.2.{number}" : null, null,
        replied ? 10 : null, replied ? 10 : null, replied ? 10 : null, replied ? 10 : null, null, 5, replied ? 5 : 0,
        replied ? 0 : 100, false, [], replied ? ProbeStatus.Success : ProbeStatus.Timeout);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
}
