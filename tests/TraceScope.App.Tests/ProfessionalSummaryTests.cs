using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProfessionalSummaryTests
{
    [AvaloniaFact]
    public void NewEditAndDeleteButtonsPersistCheckedTargetsAndNames()
    {
        var now = DateTimeOffset.UtcNow;
        var targets = new[] { Item("one.example", now, 15), Item("two.example", now, 25) };
        IReadOnlyList<SummaryDefinition> persisted = [];
        var window = new SummaryWindow(() => targets, _ => { }, definitionsChanged: definitions => persisted = definitions);
        window.Show();
        try
        {
            Click(Find<Button>(window, "SummaryNew"));
            var create = window.OwnedWindows.Single();
            Find<TextBox>(create, "SummaryEditName").Text = "My targets";
            create.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Tag as string == "two.example").IsChecked = false;
            Click(Find<Button>(create, "SummarySave"));
            Assert.Equal("My targets", Assert.Single(persisted).Name);
            Assert.Equal(["one.example"], persisted[0].Targets);
            Click(Find<Button>(window, "SummaryEdit"));
            var edit = window.OwnedWindows.Single();
            Find<TextBox>(edit, "SummaryEditName").Text = "Renamed";
            edit.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Tag as string == "two.example").IsChecked = true;
            Click(Find<Button>(edit, "SummarySave"));
            Assert.Equal("Renamed", Assert.Single(persisted).Name);
            Assert.Equal(2, persisted[0].Targets.Count);
            Click(Find<Button>(window, "SummaryDelete"));
            Assert.Empty(persisted);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SavedMembershipSurvivesNewSessionIdsAndChangesInvokePersistence()
    {
        var now = DateTimeOffset.UtcNow;
        var targets = new List<TargetItem> { Item("one.example", now, 20), Item("two.example", now, 45) };
        var definition = new SummaryDefinition { Name = "Office", Targets = ["ONE.example"] };
        IReadOnlyList<SummaryDefinition>? persisted = null;
        var window = new SummaryWindow(() => targets, _ => { }, [definition], d => persisted = d);
        window.Show();
        try
        {
            window.SelectSummary(definition.Id);
            var table = Find<DataGrid>(window, "SummaryTable");
            Assert.Equal("one.example", Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Target);
            targets[0] = Item("one.example", now.AddSeconds(1), 22);
            window.RefreshRows();
            Assert.Same(targets[0], Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Item);
            window.SetDefinition(definition with { Name = "Edited", Targets = ["two.example"], Layout = SummaryLayout.Graphs });
            Assert.Equal("Edited", Assert.Single(persisted!).Name);
            Assert.Equal(SummaryLayout.Graphs, Assert.Single(persisted!).Layout);
            Assert.Equal("two.example", Assert.Single(table.ItemsSource!.Cast<SummaryRow>()).Target);
            Assert.True(Find<Border>(window, "SummaryCombined").IsVisible);
            window.RemoveDefinition(definition.Id);
            Assert.Empty(persisted!);
            Assert.Equal(2, table.ItemsSource!.Cast<SummaryRow>().Count());
            targets.Add(Item("three.example", now, null));
            window.RefreshRows();
            Assert.Equal(3, table.ItemsSource!.Cast<SummaryRow>().Count());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LayoutAndFocusSettingsRestoreFromSerializedDefinitions()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Item("restored.example", now, 20);
        var definition = new SummaryDefinition { Name = "Saved", Targets = [item.Target], Layout = SummaryLayout.Tiles, FocusMinutes = 15 };
        var saved = JsonSerializer.Deserialize<SummaryDefinition>(JsonSerializer.Serialize(definition, WorkspaceStore.JsonOptions), WorkspaceStore.JsonOptions)!;
        var window = new SummaryWindow(() => [item], _ => { }, [saved]);
        window.Show();
        try
        {
            window.SelectSummary(saved.Id);
            Assert.True(Find<ScrollViewer>(window, "SummaryTiles").IsVisible);
            Assert.False(Find<DataGrid>(window, "SummaryTable").IsVisible);
            Assert.Equal(2, Find<ComboBox>(window, "SummaryFocus").SelectedIndex);
            Find<ComboBox>(window, "SummaryLayout").SelectedItem = SummaryLayout.Graphs;
            Assert.Equal(SummaryLayout.Graphs, Assert.Single(window.Definitions).Layout);
            Find<ComboBox>(window, "SummaryFocus").SelectedIndex = 3;
            Assert.Equal(60, Assert.Single(window.Definitions).FocusMinutes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyNamedSummaryDoesNotAccidentallyIncludeAllTargets()
    {
        var definition = new SummaryDefinition { Name = "Empty", Targets = [] };
        var window = new SummaryWindow(() => [Item("example", DateTimeOffset.UtcNow, 10)], _ => { }, [definition]);
        window.Show();
        try
        {
            window.SelectSummary(definition.Id);
            Assert.Empty(Find<DataGrid>(window, "SummaryTable").ItemsSource!.Cast<SummaryRow>());
            window.SelectSummary(null);
            Assert.Single(Find<DataGrid>(window, "SummaryTable").ItemsSource!.Cast<SummaryRow>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ImportedCustomFocusRetainsItsExactTimeRange()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Item("custom.example", now, 20);
        var definition = new SummaryDefinition { Name = "Custom focus", Targets = [item.Target], FocusMinutes = 37 };
        var window = new SummaryWindow(() => [item], _ => { }, [definition]);
        window.Show();
        try
        {
            window.SelectSummary(definition.Id);
            Assert.Equal("37 minutes", Find<ComboBox>(window, "SummaryFocus").SelectedItem);
            Assert.Equal(TimeSpan.FromMinutes(37), Assert.Single(Find<DataGrid>(window, "SummaryTable").ItemsSource!.Cast<SummaryRow>()).Focus);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void GraphAggregationRetainsExactLossAndExcludesProbeErrors()
    {
        var end = DateTimeOffset.UtcNow;
        var samples = new LatencySample[]
        {
            new(end.AddSeconds(-1), 10, ProbeStatus.Success),
            new(end.AddMilliseconds(-900), 30, ProbeStatus.Success),
            new(end.AddMilliseconds(-800), null, ProbeStatus.Timeout),
            new(end.AddMilliseconds(-700), null, ProbeStatus.Error),
            new(end.AddMinutes(-6), 1000, ProbeStatus.Success),
            new(end.AddMinutes(1), 1000, ProbeStatus.Success)
        };
        var bucket = Assert.Single(SummaryMeasurements.Aggregate(samples, end, TimeSpan.FromMinutes(5), 1));
        Assert.Equal(3, bucket.Sent);
        Assert.Equal(1, bucket.Errors);
        Assert.Equal(20, bucket.AverageMs);
        Assert.Equal(100d / 3, bucket.LossPercent, 5);
    }

    [Fact]
    public void LargeHistoryGraphIsBoundedAndSharesWallClockBucketsAcrossTargets()
    {
        var end = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 100000).Select(i => new LatencySample(end.AddMilliseconds(-i * 10), i % 2 == 0 ? 20 : null,
            i % 2 == 0 ? ProbeStatus.Success : ProbeStatus.Timeout)).ToArray();
        var one = SummaryMeasurements.Aggregate(samples, end, TimeSpan.FromMinutes(20), 300);
        var two = SummaryMeasurements.Aggregate(samples.Where((_, index) => index % 2 == 0).ToArray(), end, TimeSpan.FromMinutes(20), 300);
        Assert.InRange(one.Count, 1, 300);
        Assert.Equal(100000, one.Sum(p => p.Sent));
        Assert.Equal(50000, two.Sum(p => p.Sent));
        Assert.Equal(one.Select(p => p.Timestamp), two.Select(p => p.Timestamp));
        Assert.All(two, p => Assert.Equal(0, p.LossPercent));
        Assert.All(one, p => Assert.Equal(20, p.AverageMs));
    }

    [Theory]
    [InlineData("", "Valid", 5, SummaryLayout.Table)]
    [InlineData("bad", "Valid", 5, SummaryLayout.Table)]
    [InlineData("7dc5632646a94a329dfc9205ba853f20", "", 5, SummaryLayout.Table)]
    [InlineData("7dc5632646a94a329dfc9205ba853f20", "Valid", 0, SummaryLayout.Table)]
    [InlineData("7dc5632646a94a329dfc9205ba853f20", "Valid", 1441, SummaryLayout.Table)]
    [InlineData("7dc5632646a94a329dfc9205ba853f20", "Valid", 5, (SummaryLayout)99)]
    public void InvalidDefinitionsAreRejected(string id, string name, int focus, SummaryLayout layout)
        => Assert.Throws<InvalidDataException>(() => new SummaryDefinition { Id = id, Name = name, FocusMinutes = focus, Layout = layout }.Validate());

    [Fact]
    public void DuplicateOrNullMembershipIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new SummaryDefinition { Targets = ["example", "EXAMPLE"] }.Validate());
        Assert.Throws<InvalidDataException>(() => new SummaryDefinition { Targets = null! }.Validate());
        Assert.Throws<InvalidDataException>(() => new SummaryDefinition { Targets = [""] }.Validate());
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TargetItem Item(string target, DateTimeOffset now, double? latency)
    {
        var id = Guid.NewGuid();
        var status = latency.HasValue ? ProbeStatus.Success : ProbeStatus.Timeout;
        var destination = new HopSnapshot(0, "127.0.0.1", null, latency, latency, latency, latency, 0, 1,
            latency.HasValue ? 1 : 0, latency.HasValue ? 0 : 100, true, [new(now, latency, status)], status);
        return new TargetItem(new StoredSession(id, target, "127.0.0.1", now, now, 1))
        { Snapshot = new(id, target, "127.0.0.1", now, 1, latency.HasValue, [], destination, TimeSpan.Zero) };
    }
}
