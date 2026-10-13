using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProfessionalWindowsTests
{
    [AvaloniaFact]
    public void SummaryFiltersMeasuredRowsRetainsSelectionAndActivatesTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var healthy = Item("Healthy", "Office", now, 12);
        var failing = Item("Failing", "Home", now, null);
        TargetItem? activated = null;
        var window = new SummaryWindow(() => [healthy, failing], item => activated = item);
        window.Show();
        try
        {
            var table = Find<DataGrid>(window, "SummaryTable");
            var rows = table.ItemsSource!.Cast<SummaryRow>().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.False(rows[0].IsProblem);
            Assert.True(rows[1].IsProblem);
            Assert.Equal("12.0", rows[0].FocusAverage);
            Assert.Equal("100%", rows[1].Loss);
            table.SelectedItem = rows[0];
            window.RefreshRows();
            Assert.Same(healthy, Assert.IsType<SummaryRow>(table.SelectedItem).Item);
            var open = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Show selected trace");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(healthy, activated);
            Find<CheckBox>(window, "SummaryProblems").IsChecked = true;
            Assert.Same(failing, Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Item);
            Find<CheckBox>(window, "SummaryProblems").IsChecked = false;
            Find<ComboBox>(window, "SummaryGroup").SelectedItem = "Office";
            Assert.Same(healthy, Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Item);
            Find<ComboBox>(window, "SummaryFocus").SelectedIndex = 0;
            Assert.Equal(1, Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Samples);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SummaryTimeWindowsExcludeOldMeasurementsConsistently()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Item("Measured", "Default", now, 10);
        item.Snapshot = item.Snapshot! with { Destination = item.Snapshot.Destination with
        { History = [new(now.AddMinutes(-10), 400, ProbeStatus.Success), new(now.AddSeconds(-90), 30, ProbeStatus.Success), new(now, 10, ProbeStatus.Success)] } };
        var five = new SummaryRow(item, TimeSpan.FromMinutes(5), now);
        var one = new SummaryRow(item, TimeSpan.FromMinutes(1), now);
        Assert.Equal("20.0", five.FocusAverage);
        Assert.Equal(2, five.Samples);
        Assert.Equal("10.0", one.FocusAverage);
        Assert.Equal(1, one.Samples);
        Assert.Equal("10.0", five.Average30s);
        Assert.Equal("20.0", one.Average5m);
    }

    [AvaloniaFact]
    public async Task NetworkWindowDisplaysRealInterfaceInventoryWithoutTracing()
    {
        var traced = new List<string>();
        var window = new NetworkInfoWindow(address => { traced.Add(address); return Task.CompletedTask; });
        // Refresh before opening ensures its asynchronous inventory is completed for deterministic assertions.
        await window.RefreshAsync();
        window.Show();
        try
        {
            var interfaces = Find<DataGrid>(window, "NetworkInterfaces").ItemsSource!
                .Cast<NetworkInfoWindow.InterfaceRow>().ToArray();
            Assert.NotEmpty(interfaces);
            Assert.All(interfaces, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
            Assert.Empty(traced);
            Assert.NotNull(Find<DataGrid>(window, "NetworkNeighbors"));
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static TargetItem Item(string target, string group, DateTimeOffset now, double? latency)
    {
        var id = Guid.NewGuid();
        var status = latency.HasValue ? ProbeStatus.Success : ProbeStatus.Timeout;
        var destination = new HopSnapshot(0, "127.0.0.1", null, latency, latency, latency,
            latency, latency.HasValue ? 0 : null, 1, latency.HasValue ? 1 : 0,
            latency.HasValue ? 0 : 100, true, [new(now, latency, status)], status);
        return new TargetItem(new StoredSession(id, target, "127.0.0.1", now, now, 1))
        { Group = group, Snapshot = new(id, target, "127.0.0.1", now, 1, latency.HasValue, [], destination, TimeSpan.Zero) };
    }
}
