using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TraceScope.App.Controls;
using TraceScope.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProfessionalIntegrationTests
{
    [AvaloniaFact]
    public async Task GroupContextMenuPreservesOtherTargetsAndSavesAssignmentsWithoutToolbarRow()
    {
        await using var fixture = new Fixture();
        await fixture.Add("127.0.0.1");
        await fixture.Add("127.0.0.2");
        var list = Control<ListBox>(fixture.Window, "TargetList");
        var targets = list.ItemsSource!.Cast<TargetItem>().ToArray();
        var first = targets[0];
        var menu = new MenuItem { DataContext = first };
        Invoke(fixture.Window, "OpenTargetGroup", menu, new Avalonia.Interactivity.RoutedEventArgs());
        var dialog = fixture.Window.OwnedWindows.Single(w => w.Title == "Assign group");
        var controls = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Control>().ToArray();
        controls.OfType<TextBox>().Single(c => c.Name == "TargetGroupName").Text = "Office";
        controls.OfType<Button>().Single(c => c.Name == "SaveTargetGroup").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => !dialog.IsVisible);
        Assert.Equal("Office", first.Group);
        Assert.Equal("Office", first.Session!.Group);
        Assert.Equal("Default", targets[1].Group);
        Assert.False(Control<TextBox>(fixture.Window, "GroupInput").IsEffectivelyVisible);
        await InvokeTask(fixture.Window, "SaveWorkspaceNowAsync");
        var stored = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal("Office", stored.Targets.Single(t => t.Target == first.Target).Group);
        Assert.Equal(1200, fixture.Window.Width);
        Assert.Equal(800, fixture.Window.Height);
    }

    [AvaloniaFact]
    public async Task ProbeProtocolPortAndGroupControlsApplyOnlyToSelectedTarget()
    {
        await using var fixture = new Fixture();
        await fixture.Add("127.0.0.1");
        var first = fixture.Engine.Sessions.Single();
        await fixture.Add("127.0.0.2");
        var second = fixture.Engine.Sessions.Single(s => s.Id != first.Id);
        var list = Control<ListBox>(fixture.Window, "TargetList");
        list.SelectedItem = list.ItemsSource!.Cast<TargetItem>().Single(t => t.Id == first.Id);
        Control<ComboBox>(fixture.Window, "ProtocolInput").SelectedItem = ProbeProtocol.Udp;
        Control<NumericUpDown>(fixture.Window, "PortInput").Value = 5353;
        Control<TextBox>(fixture.Window, "GroupInput").Text = "Office";
        await Until(() => first.Group == "Office");
        Assert.Equal(ProbeProtocol.Udp, first.Options.Protocol);
        Assert.Equal(5353, first.Options.DestinationPort);
        Assert.Equal("Office", first.Group);
        Assert.Equal(ProbeProtocol.Icmp, second.Options.Protocol);
        Assert.Equal("Default", second.Group);
        list.SelectedItem = list.ItemsSource!.Cast<TargetItem>().Single(t => t.Id == second.Id);
        Assert.Equal(ProbeProtocol.Icmp, Control<ComboBox>(fixture.Window, "ProtocolInput").SelectedItem);
        Assert.Equal(443, Control<NumericUpDown>(fixture.Window, "PortInput").Value);
        Assert.Equal("Default", Control<TextBox>(fixture.Window, "GroupInput").Text);
        await Until(() => fixture.Transport.Configurations.Any(o => o.Protocol == ProbeProtocol.Udp && o.DestinationPort == 5353));
    }

    [AvaloniaFact]
    public async Task WorkspaceCaptureRestoresPausedTargetsSettingsGroupsAndProfiles()
    {
        await using var fixture = new Fixture();
        var customOptions = new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = 8443,
            Interval = TimeSpan.FromSeconds(5), Timeout = TimeSpan.FromSeconds(2), MaxHops = 3, ResolveHostnames = false };
        var requested = new WorkspaceConfiguration
        {
            Name = "Integration workspace", AggregationSeconds = 10,
            Targets = [new("127.0.0.1", customOptions, "Office", true), new("127.0.0.2", customOptions with { Protocol = ProbeProtocol.Udp, DestinationPort = 33450 }, "Home", true)],
            Profiles = [new("Custom TCP", customOptions)]
        };
        await InvokeTask(fixture.Window, "ApplyWorkspaceAsync", requested);
        var captured = Assert.IsType<WorkspaceConfiguration>(Invoke(fixture.Window, "CaptureWorkspace"));
        Assert.Equal(requested.Name, captured.Name);
        Assert.Equal(2, captured.Targets.Count);
        Assert.All(captured.Targets, target => Assert.True(target.Paused));
        Assert.Equal("Office", captured.Targets[0].Group);
        Assert.Equal(customOptions, captured.Targets[0].Options);
        Assert.Equal(33450, captured.Targets[1].Options.DestinationPort);
        var saved = await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync();
        Assert.NotNull(saved);
        Assert.Equal(captured.Name, saved.Name);
        Assert.Equal(captured.AggregationSeconds, saved.AggregationSeconds);
        Assert.Equal(captured.Targets, saved.Targets);
        Assert.Equal(captured.Profiles, saved.Profiles);
        // Apply the independently serialized workspace again: previous sessions must stop and new ones restore paused.
        var oldIds = fixture.Engine.Sessions.Select(s => s.Id).ToArray();
        await InvokeTask(fixture.Window, "ApplyWorkspaceAsync", saved!);
        Assert.All(fixture.Engine.Sessions.Where(s => oldIds.Contains(s.Id)), s => Assert.Equal(SessionState.Stopped, s.State));
        var restored = fixture.Engine.Sessions.Where(s => !oldIds.Contains(s.Id)).ToArray();
        Assert.Equal(2, restored.Length);
        Assert.All(restored, s => Assert.Equal(SessionState.Paused, s.State));
        Assert.Equal(new[] { "Custom TCP" }, Control<ComboBox>(fixture.Window, "ProfileInput").ItemsSource!.Cast<string>());
        Control<ComboBox>(fixture.Window, "ProfileInput").SelectedItem = "Custom TCP";
        var selected = Assert.IsType<TargetItem>(Control<ListBox>(fixture.Window, "TargetList").SelectedItem);
        Assert.Equal(ProbeProtocol.Tcp, selected.Session!.Options.Protocol);
        Assert.Equal(8443, selected.Session.Options.DestinationPort);
        Assert.Equal(3, selected.Session.Options.MaxHops);
    }

    [AvaloniaFact]
    public async Task DiskHistoryNavigationBoundsGraphAndDisplaysStoredEvents()
    {
        await using var fixture = new Fixture();
        var session = await fixture.Engine.StartTargetAsync("127.0.0.8", new ProbeOptions
        { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false });
        await Until(() => session.LatestSnapshot is not null);
        await fixture.Engine.StopAsync(session.Id);
        var end = DateTimeOffset.UtcNow;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Engine.Storage.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status) VALUES($id,$round,0,'127.0.0.8',1,$at,$latency,$status)";
            command.Parameters.AddWithValue("$id", session.Id.ToString());
            var round = command.Parameters.Add("$round", SqliteType.Integer);
            var at = command.Parameters.Add("$at", SqliteType.Text);
            var latency = command.Parameters.Add("$latency", SqliteType.Real);
            var status = command.Parameters.Add("$status", SqliteType.Integer);
            for (var i = 0; i < 7200; i++)
            {
                round.Value = i + 100; at.Value = end.AddSeconds(i - 7200).ToString("O");
                latency.Value = i % 10 == 0 ? DBNull.Value : 20d + i % 5;
                status.Value = i % 10 == 0 ? (int)ProbeStatus.Timeout : (int)ProbeStatus.Success;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        var markerTime = end.AddMinutes(-40);
        await fixture.Engine.Storage.AppendEventAsync(new(Guid.NewGuid(), session.Id, markerTime, "RouteChanged", "Measured route changed"));
        var stored = (await fixture.Engine.Storage.LoadSessionsAsync()).Single(s => s.Id == session.Id);
        await InvokeTask(fixture.Window, "LoadHistory", stored);
        var timeline = Control<LatencyTimeline>(fixture.Window, "Timeline");
        Assert.True(Assert.IsType<TargetItem>(Control<ListBox>(fixture.Window, "TargetList").SelectedItem).IsHistorical);
        timeline.Horizon = TimeSpan.FromHours(1); timeline.EndTime = end;
        await InvokeTask(fixture.Window, "LoadDiskGraphAsync", true);
        Assert.InRange(timeline.Samples.Count, 1, 2000);
        Assert.Contains(timeline.Samples, sample => sample.LossPercent > 0);
        Assert.Contains(timeline.Markers, marker => marker.Kind == "RouteChanged" && marker.Time == markerTime);
        var recentTimes = timeline.Samples.Select(s => s.Time).ToArray();
        timeline.EndTime = end.AddHours(-1);
        await InvokeTask(fixture.Window, "LoadDiskGraphAsync", true);
        // Navigation also queues the normal debounced history load. Wait for
        // its UI result if it superseded this explicit asynchronous query.
        await Until(() => timeline.Samples.Count > 0 && timeline.Samples.Max(s => s.Time) < recentTimes.Max());
        Assert.InRange(timeline.Samples.Count, 1, 2000);
        Assert.True(timeline.Samples.Max(s => s.Time) < recentTimes.Max());
        Assert.DoesNotContain(timeline.Markers, marker => marker.Kind == "RouteChanged");
    }

    private static T Control<T>(MainWindow window, string name) where T : Control => window.FindControl<T>(name)!;
    private static object? Invoke(MainWindow window, string method, params object?[] arguments)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static Task InvokeTask(MainWindow window, string method, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(Invoke(window, method, arguments));
    private static async Task Until(Func<bool> predicate)
    {
        var expires = DateTime.UtcNow.AddSeconds(6);
        while (!predicate()) { if (DateTime.UtcNow > expires) throw new TimeoutException("UI condition did not complete."); await Task.Delay(20); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public ConfiguredTransport Transport { get; } = new();
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate project test artifacts.");
            Directory = Path.Combine(root.FullName, "artifacts", "tests", "professional-ui", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: Transport);
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Control<NumericUpDown>(Window, "MaxHopsInput").Value = 2;
            Window.Show();
        }
        public async Task Add(string target)
        {
            await InvokeTask(Window, "AddTarget", target, new ProbeOptions
            { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            await Until(() => Engine.Sessions.Any(s => s.Target == target && s.LatestSnapshot is not null));
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ConfiguredTransport : IConfiguredProbeTransport
    {
        public System.Collections.Concurrent.ConcurrentBag<ProbeOptions> Configurations { get; } = [];
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options, CancellationToken cancellationToken)
        {
            Configurations.Add(options);
            return ProbeAsync(destination, ttl, options.Timeout, cancellationToken);
        }
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
