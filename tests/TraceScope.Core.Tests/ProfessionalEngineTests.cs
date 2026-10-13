using System.Collections.Concurrent;
using System.Net;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class ProfessionalEngineTests
{
    private static string Database() => Path.Combine(AppContext.BaseDirectory,
        "test-data", "professional-engine", Guid.NewGuid() + ".db");

    private static ProbeOptions Options() => new()
    {
        Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100),
        MaxHops = 2, HistoryLimit = 20, Concurrency = 2, ResolveHostnames = false
    };

    [Fact]
    public async Task SimultaneousTargetsDispatchTheirOwnOptionsAndApplyUpdatedSettings()
    {
        var transport = new ConfiguredTransport();
        await using var engine = new TraceEngine(Database(), Options(), transport);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new ConcurrentDictionary<Guid, byte>();
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.Round == 2)
            {
                engine.Pause(snapshot.SessionId);
                paused.TryAdd(snapshot.SessionId, 0);
                if (paused.Count == 2) completed.TrySetResult();
            }
        };
        var firstOptions = Options() with { Protocol = ProbeProtocol.Tcp, DestinationPort = 8443,
            SourceAddress = "127.0.0.1", InterfaceId = "lo0" };
        var secondOptions = Options() with { Protocol = ProbeProtocol.Udp, DestinationPort = 33434,
            SourceAddress = "127.0.0.2" };
        var first = await engine.StartTargetAsync("127.0.0.1", firstOptions);
        var second = await engine.StartTargetAsync("127.0.0.2", secondOptions);
        first.Group = "Services";
        second.Group = "Routers";
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionState.Paused, first.State);
        Assert.Equal(SessionState.Paused, second.State);
        Assert.Equal("Services", first.Group);
        Assert.Equal("Routers", second.Group);
        Assert.Equal(2, first.LatestSnapshot!.Destination.Sent);
        Assert.Equal(2, second.LatestSnapshot!.Destination.Sent);
        var calls = transport.Calls.ToArray();
        Assert.All(calls.Where(c => c.Address == "127.0.0.1"), c => Assert.Equal(firstOptions, c.Options));
        Assert.All(calls.Where(c => c.Address == "127.0.0.2"), c => Assert.Equal(secondOptions, c.Options));
        Assert.Contains(calls, c => c.Address == "127.0.0.1" && c.Ttl == 255);
        Assert.Contains(calls, c => c.Address == "127.0.0.2" && c.Ttl == 1);
        Assert.Equal(0, transport.LegacyCalls);

        var next = new TaskCompletionSource<RoundSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.SessionId == first.Id && snapshot.Round == 3)
            { engine.Pause(first.Id); next.TrySetResult(snapshot); }
        };
        var updated = firstOptions with { Protocol = ProbeProtocol.Icmp, DestinationPort = 443,
            Interval = TimeSpan.FromMilliseconds(200), Timeout = TimeSpan.FromMilliseconds(250) };
        engine.UpdateOptions(first.Id, updated);
        engine.Resume(first.Id);
        var afterUpdate = await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, afterUpdate.Destination.Sent); // Protocol changes start fresh measurements.
        Assert.Equal(1, afterUpdate.Destination.Received);
        Assert.Equal(2, second.LatestSnapshot!.Round);
        Assert.Equal(secondOptions, second.Options);
        Assert.Equal(updated, first.Options);
        Assert.Equal("Services", first.Group);
        Assert.Contains(transport.Calls, c => c.Address == "127.0.0.1" && c.Options == updated);
        Assert.False(afterUpdate.RouteChanged);
        await engine.StopAsync(first.Id);
        await engine.StopAsync(second.Id);
        Assert.Equal(2, (await engine.Storage.LoadSessionsAsync()).Count);
    }

    [Fact]
    public async Task ResetStatisticsPreservesSettingsHistoryAndRoundIdsWithoutAffectingOtherTargets()
    {
        await using var engine = new TraceEngine(Database(), Options(), new ConfiguredTransport());
        var paused = new ConcurrentDictionary<Guid, byte>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.Round != 2) return;
            engine.Pause(snapshot.SessionId);
            paused.TryAdd(snapshot.SessionId, 0);
            if (paused.Count == 2) ready.TrySetResult();
        };
        var options = Options() with { Protocol = ProbeProtocol.Udp, DestinationPort = 33434,
            SourceAddress = "127.0.0.1", InterfaceId = "lo0" };
        var first = await engine.StartTargetAsync("127.0.0.1", options);
        var second = await engine.StartTargetAsync("127.0.0.2");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = await engine.Storage.LoadSamplesAsync(first.Id);
        var otherSnapshot = second.LatestSnapshot;
        engine.ResetStatistics(first.Id);
        Assert.Equal(SessionState.Paused, first.State);
        Assert.Equal(options, first.Options);
        Assert.Equal(0, first.LatestSnapshot!.Destination.Sent);
        Assert.Null(first.LatestSnapshot.Destination.AverageMs);
        Assert.Equal(2, first.LatestSnapshot.Destination.History.Count);
        Assert.All(first.LatestSnapshot.Hops, hop => Assert.Equal(0, hop.Sent));
        Assert.Same(otherSnapshot, second.LatestSnapshot);
        Assert.Equal(saved, await engine.Storage.LoadSamplesAsync(first.Id));

        var resumed = new TaskCompletionSource<RoundSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.SessionId != first.Id || snapshot.Round != 3) return;
            engine.Pause(first.Id);
            resumed.TrySetResult(snapshot);
        };
        engine.Resume(first.Id);
        var next = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, next.Destination.Sent);
        Assert.Equal(1, next.Destination.Received);
        Assert.Equal(3, next.Destination.History.Count);
        Assert.All(next.Hops, hop => Assert.Equal(1, hop.Sent));
        Assert.False(next.RouteChanged);
        var persisted = await engine.Storage.LoadSamplesAsync(first.Id);
        Assert.Equal(new long[] { 1, 2, 3 }, persisted.Select(s => s.Round).Distinct().Order().ToArray());
        Assert.All(saved, sample => Assert.Contains(sample, persisted));
        Assert.Equal(SessionState.Paused, second.State);

        await engine.StopAsync(first.Id);
        Assert.True(engine.ForgetSession(first.Id));
        Assert.Same(second, Assert.Single(engine.Sessions));
        Assert.Same(otherSnapshot, second.LatestSnapshot);
        Assert.Equal(2, (await engine.Storage.LoadSessionsAsync()).Count);
        Assert.Equal(persisted, await engine.Storage.LoadSamplesAsync(first.Id));
    }

    [Fact]
    public async Task ResetDuringRunningProbeKeepsWorkerAndCountsNextResponseAsFirstSample()
    {
        var transport = new GatedTransport();
        await using var engine = new TraceEngine(Database(), Options(), transport);
        var session = await engine.StartTargetAsync("127.0.0.1");
        await transport.SecondEndpointStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.LatestSnapshot!.Destination.Sent);
        var ready = new TaskCompletionSource<RoundSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.Round != 2) return;
            engine.Pause(snapshot.SessionId);
            ready.TrySetResult(snapshot);
        };
        engine.ResetStatistics(session.Id);
        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal(0, session.LatestSnapshot!.Destination.Sent);
        transport.Release.TrySetResult();
        var next = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, next.Destination.Sent);
        Assert.Equal(2, next.Destination.History.Count);
        Assert.Equal(new long[] { 1, 2 }, (await engine.Storage.LoadSamplesAsync(session.Id))
            .Select(s => s.Round).Distinct().Order().ToArray());
    }

    [Fact]
    public async Task ConfirmedRouterChangeEmitsOnceAndPersistsEventInSqlite()
    {
        var database = Database();
        await using var engine = new TraceEngine(database, Options(), new ChangingRouterTransport());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<RoundSnapshot>();
        var notifications = new ConcurrentQueue<MonitoringEvent>();
        engine.MonitoringEventOccurred += (_, item) => notifications.Enqueue(item);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            snapshots.Enqueue(snapshot);
            if (snapshot.Round == 4) { engine.Pause(snapshot.SessionId); completed.TrySetResult(); }
        };
        var session = await engine.StartTargetAsync("127.0.0.1");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(session.Id);
        var rounds = snapshots.ToArray();
        Assert.False(rounds[0].RouteChanged);
        Assert.False(rounds[1].RouteChanged);
        Assert.True(rounds[2].RouteChanged);
        Assert.False(rounds[3].RouteChanged);
        Assert.Equal("Hop 1: 192.0.2.1 → 192.0.2.2", rounds[2].RouteDescription);
        var notified = Assert.Single(notifications);
        Assert.Equal("RouteChange", notified.Kind);
        Assert.Single(engine.RecentEvents);
        var persisted = Assert.Single(await new HistoryStore(database).LoadEventsAsync(session.Id));
        Assert.Equal(notified, persisted);
        Assert.Equal(3, rounds[3].Hops[0].Sent); // Changed router has independent statistics.
    }

    [Fact]
    public async Task UnsupportedHopCaptureRemainsDiagnosticAndDoesNotCreatePacketLoss()
    {
        await using var engine = new TraceEngine(Database(), Options(), new UnsupportedHopTransport());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.Round == 3) { engine.Pause(snapshot.SessionId); completed.TrySetResult(); }
        };
        var session = await engine.StartTargetAsync("127.0.0.1",
            Options() with { Protocol = ProbeProtocol.Tcp });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(session.Id);
        var snapshot = session.LatestSnapshot!;
        Assert.Contains("capture unavailable", snapshot.Warning);
        Assert.Equal(0, snapshot.Destination.LossPercent);
        Assert.Equal(3, snapshot.Destination.Sent);
        var hop = snapshot.Hops[0];
        Assert.Equal(0, hop.Sent);
        Assert.Equal(0, hop.Received);
        Assert.Equal(0, hop.LossPercent);
        Assert.Null(hop.AverageMs);
        var summary = MonitoringStatistics.Calculate(hop.History, TimeSpan.FromMinutes(1), snapshot.Timestamp);
        Assert.Equal(0, summary.Sent);
        Assert.Equal(3, summary.Errors);
        Assert.Equal(0, summary.LossPercent);
        var aggregate = Assert.Single(await engine.Storage.LoadAggregatedSamplesAsync(session.Id, 1,
            session.StartedAt, snapshot.Timestamp.AddSeconds(1), 1));
        Assert.Equal(0, aggregate.Count);
        Assert.Equal(3, aggregate.Errors);
        Assert.Equal(0, aggregate.LossPercent);
        Assert.Null(aggregate.AverageMs);
        Assert.Empty(await engine.Storage.LoadEventsAsync(session.Id));
    }

    private sealed class ConfiguredTransport : IConfiguredProbeTransport
    {
        public ConcurrentQueue<(string Address, int Ttl, ProbeOptions Options)> Calls { get; } = new();
        public int LegacyCalls;
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
            CancellationToken cancellationToken)
        { Interlocked.Increment(ref LegacyCalls); throw new InvalidOperationException("Configured overload required."); }
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Enqueue((destination.ToString(), ttl, options));
            await Task.Delay(5, cancellationToken);
            return ttl == 1 ? new("192.0.2.1", 5, ProbeStatus.TimeExceeded)
                : new(destination.ToString(), 10, ProbeStatus.Success);
        }
    }

    private sealed class GatedTransport : IProbeTransport
    {
        private int _endpoints;
        public TaskCompletionSource SecondEndpointStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (ttl == 255 && Interlocked.Increment(ref _endpoints) == 2)
            {
                SecondEndpointStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(destination.ToString(), 10, ProbeStatus.Success);
        }
    }

    private sealed class ChangingRouterTransport : IProbeTransport
    {
        private int _round;
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (ttl == 1)
            {
                var address = Interlocked.Increment(ref _round) == 1 ? "192.0.2.1" : "192.0.2.2";
                return Task.FromResult(new ProbeResult(address, 5, ProbeStatus.TimeExceeded));
            }
            return Task.FromResult(new ProbeResult(destination.ToString(), 10, ProbeStatus.Success));
        }
    }

    private sealed class UnsupportedHopTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(ttl == 255
                ? new ProbeResult(destination.ToString(), 10, ProbeStatus.Success)
                : new ProbeResult(null, null, ProbeStatus.Error, "capture unavailable"));
    }
}
