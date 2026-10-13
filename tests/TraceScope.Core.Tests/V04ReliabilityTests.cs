using System.Net;
using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class V04ReliabilityTests
{
    [Fact]
    public async Task EventAcknowledgmentPersistsAcrossStoreReopenAndRetentionCleansIt()
    {
        var path = Database();
        await using var engine = new TraceEngine(path, Options(), new RouteTransport());
        var session = await engine.StartTargetAsync("127.0.0.1");
        await Until(() => session.LatestSnapshot?.Round >= 2);
        await engine.StopAsync(session.Id);
        var item = new MonitoringEvent(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow, "Alert", "Actual event fixture");
        await engine.Storage.AppendEventAsync(item);
        Assert.False(await engine.Storage.AcknowledgeEventAsync(Guid.NewGuid()));
        Assert.True(await engine.Storage.AcknowledgeEventAsync(item.Id, "Reviewed"));
        var reopened = new HistoryStore(path);
        var saved = Assert.Single(await reopened.LoadEventsAsync(session.Id));
        Assert.NotNull(saved.AcknowledgedAt); Assert.Equal("Reviewed", saved.AcknowledgmentNote);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.AcknowledgeEventAsync(item.Id, new string('x', 1025)));
        await reopened.CompactAsync();
        Assert.NotNull(Assert.Single(await reopened.LoadEventsAsync(session.Id)).AcknowledgedAt);
        var old = item with { Id = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow.AddDays(-10) };
        await reopened.AppendEventAsync(old); await reopened.AcknowledgeEventAsync(old.Id);
        await reopened.RetainAsync(TimeSpan.FromDays(1));
        Assert.Equal(item.Id, Assert.Single(await reopened.LoadEventsAsync(session.Id)).Id);
        Assert.NotEmpty(await reopened.LoadSamplesAsync(session.Id));
    }

    [Fact]
    public async Task ClosedTargetsReleaseEngineReferencesButKeepStoredMeasurements()
    {
        await using var engine = new TraceEngine(Database(), Options(), new RouteTransport());
        for (var i = 0; i < 12; i++)
        {
            var session = await engine.StartTargetAsync("127.0.0.1");
            await Until(() => session.LatestSnapshot?.Round >= 2);
            Assert.Throws<InvalidOperationException>(() => engine.ForgetSession(session.Id));
            await engine.StopAsync(session.Id);
            Assert.True(engine.ForgetSession(session.Id));
            Assert.Empty(engine.Sessions);
            Assert.False(engine.ForgetSession(session.Id));
        }
        var stored = await engine.Storage.LoadSessionsAsync();
        Assert.Equal(12, stored.Count);
        Assert.All(stored, s => { Assert.NotNull(s.StoppedAt); Assert.True(s.SampleCount > 0); });
    }

    [Fact]
    public async Task ManyTargetsContinueIndependentlyAndHistoryRemainsBounded()
    {
        await using var engine = new TraceEngine(Database(), Options(), new RouteTransport());
        var sessions = await Task.WhenAll(Enumerable.Range(1, 32).Select(i => engine.StartTargetAsync($"127.0.0.{i}")));
        await Until(() => sessions.All(s => s.LatestSnapshot?.Round >= 15));
        Assert.Equal(32, engine.Sessions.Count);
        Assert.All(sessions, s =>
        {
            Assert.Equal(0, s.LatestSnapshot!.Destination.LossPercent);
            Assert.InRange(s.LatestSnapshot.Destination.History.Count, 1, 10);
            Assert.All(s.LatestSnapshot.Hops, h => Assert.InRange(h.History.Count, 1, 10));
        });
        await Task.WhenAll(sessions.Select(s => engine.StopAsync(s.Id)));
        Assert.All(await engine.Storage.LoadSessionsAsync(), s => Assert.True(s.SampleCount >= 14 * 3));
    }

    [Fact]
    public async Task V03ConfigurationLoadsWithActionsDisabledAndV04ViewsRoundTrip()
    {
        await using var oldStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Version\":1,\"Targets\":[],\"Alerts\":[]}"));
        var old = await WorkspaceStore.ImportAsync(oldStream);
        Assert.Empty(old.SummaryViews); Assert.False(old.Actions.NativeNotificationsEnabled);
        Assert.False(old.Actions.Script.Enabled); Assert.False(old.Actions.Webhook.Enabled); Assert.False(old.Actions.Smtp.Enabled);
        var summary = new SummaryDefinition { Name = "Office", Targets = ["1.1.1.1", "8.8.8.8"], Layout = SummaryLayout.Graphs, FocusMinutes = 15 };
        var configured = old with { SummaryViews = [summary], Actions = new() { IncludeRecovery = false, MinimumActionSpacingSeconds = 90 } };
        var store = new WorkspaceStore(Database() + ".json"); await store.SaveAsync(configured);
        var loaded = await store.LoadAsync();
        Assert.Equal(summary.Id, Assert.Single(loaded!.SummaryViews).Id);
        Assert.Equal(summary.Targets, loaded.SummaryViews[0].Targets);
        Assert.Equal(SummaryLayout.Graphs, loaded.SummaryViews[0].Layout);
        Assert.Equal(configured.Actions.IncludeRecovery, loaded.Actions.IncludeRecovery);
        Assert.Equal(configured.Actions.MinimumActionSpacingSeconds, loaded.Actions.MinimumActionSpacingSeconds);
        Assert.Equal(configured.Actions.Smtp, loaded.Actions.Smtp);
        Assert.Equal(configured.Actions.Webhook, loaded.Actions.Webhook);
        Assert.Equal(configured.Actions.Script.Arguments, loaded.Actions.Script.Arguments);
    }

    private static string Database() => Path.Combine(AppContext.BaseDirectory, "test-data", "v04-reliability", Guid.NewGuid()+".db");
    private static ProbeOptions Options() => new() { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false, HistoryLimit = 10 };
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class RouteTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress address, int ttl, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded) : new ProbeResult(address.ToString(), 12, ProbeStatus.Success));
    }
}
