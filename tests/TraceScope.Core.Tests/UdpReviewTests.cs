using System.Net;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class UdpReviewTests
{
    private static string Database() => Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid() + ".db");

    [Fact]
    public async Task RouterRevisitsRetainCountersHistoryAndDoNotBridgeJitter()
    {
        var transport = new ReviewTransport(1, changingRouter: true);
        await using var engine = new TraceEngine(Database(), transport: transport);
        var snapshots = new List<RoundSnapshot>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            snapshots.Add(snapshot);
            if (snapshot.Round == 4) { engine.Pause(snapshot.SessionId); ready.TrySetResult(); }
        };
        var target = await engine.StartTargetAsync("127.0.0.1", Options(100, 2));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(target.Id);
        Assert.Equal(new long[] { 1, 1, 2, 3 }, snapshots.Select(s => s.Hops[0].Sent));
        var hop = snapshots[^1].Hops[0];
        Assert.Equal(2, hop.Received);
        Assert.Equal(100d / 3, hop.LossPercent, 8);
        Assert.Null(hop.JitterMs);
        var samples = await engine.Storage.LoadSamplesForHopAsync(target.Id, 1);
        Assert.Equal(4, samples.Count);
        var selected = await engine.Storage.LoadSelectionStatisticsAsync(target.Id, target.StartedAt, DateTimeOffset.UtcNow);
        var router = Assert.Single(selected, h => h.Hop == 1 && h.Address == "192.0.2.1");
        Assert.Equal(hop.Sent, router.Sent); Assert.Equal(hop.LossPercent, router.LossPercent);
        Assert.Null(router.JitterMs);
    }

    [Theory]
    [InlineData(120, 1, 3, 100)]
    [InlineData(100, 30, 30, 140)]
    public async Task MinimumStartIntervalOverrunsAndNonOverlappingRoundsAreExplicit(int interval, int delay, int hops, int minimum)
    {
        var transport = new ReviewTransport(delay);
        await using var engine = new TraceEngine(Database(), transport: transport);
        var snapshots = new List<RoundSnapshot>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            snapshots.Add(snapshot);
            if (snapshot.Round == 3) { engine.Pause(snapshot.SessionId); ready.TrySetResult(); }
        };
        var target = await engine.StartTargetAsync("127.0.0.1", Options(interval, hops));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(target.Id);
        Assert.All(snapshots.Skip(1), s => Assert.True(s.SamplingInterval!.Value.TotalMilliseconds >= minimum));
        Assert.False(transport.OverlappedRounds);
        Assert.All(snapshots, s => Assert.False(s.DestinationReached));
        Assert.Equal(100, snapshots[^1].Destination.LossPercent);
        var attempts = await engine.Storage.LoadProbeMeasurementsAsync(target.Id);
        Assert.Equal(3 * (hops + 1), attempts.Count);
        var stored = await engine.Storage.LoadSamplesAsync(target.Id);
        Assert.All(stored, s => Assert.True(s.Timestamp < snapshots[(int)s.Round - 1].Timestamp));
        Assert.All(snapshots, s => Assert.Equal(hops, s.Hops.Count));
    }

    [Fact]
    public async Task ChangingDestinationPortCreatesDistinctStoredConfigurationAndResetsLiveCounters()
    {
        await using var engine = new TraceEngine(Database(), transport: new PortTransport());
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        { engine.Pause(snapshot.SessionId); (snapshot.Round == 1 ? first : second).TrySetResult(); };
        var options = Options(100, 2);
        var target = await engine.StartTargetAsync("127.0.0.1", options);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, target.LatestSnapshot!.Destination.LossPercent);
        engine.UpdateOptions(target.Id, options with { DestinationPort = 33550 }); engine.Resume(target.Id);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(target.Id);
        Assert.Equal(1, target.LatestSnapshot!.Destination.Sent);
        Assert.Equal(100, target.LatestSnapshot.Destination.LossPercent);
        var selected = (await engine.Storage.LoadSelectionStatisticsAsync(target.Id, target.StartedAt, DateTimeOffset.UtcNow)).Where(h => h.Hop == 0).ToArray();
        Assert.Equal(2, selected.Length);
        Assert.Equal(new double[] { 0, 100 }, selected.OrderBy(h => h.MeasurementEpoch).Select(h => h.LossPercent));
        var configurations = await engine.Storage.LoadMeasurementOptionsAsync(target.Id);
        Assert.Equal(33434, configurations[0].DestinationPort); Assert.Equal(33550, configurations[1].DestinationPort);
    }

    private sealed class PortTransport : IConfiguredProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options, CancellationToken token) =>
            Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 10, ProbeStatus.TimeExceeded)
                : options.DestinationPort == 33434 ? new(destination.ToString(), 20, ProbeStatus.Success)
                : new(null, null, ProbeStatus.Timeout));
    }

    [Fact]
    public async Task StoppingAnIncompleteRoundKeepsActualRepliesAndExplicitCancellationAuditWithoutInventingLoss()
    {
        var transport = new InterruptedTransport();
        await using var engine = new TraceEngine(Database(), transport: transport);
        var target = await engine.StartTargetAsync("127.0.0.1", Options(100, 3));
        await transport.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(target.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(target.LatestSnapshot);
        Assert.Empty(await engine.Storage.LoadSamplesAsync(target.Id));
        var audit = await engine.Storage.LoadProbeMeasurementsAsync(target.Id);
        Assert.Equal(4, audit.Count);
        Assert.Single(audit, a => a.Result.Status == ProbeStatus.TimeExceeded && !a.Canceled);
        Assert.Equal(3, audit.Count(a => a.Canceled));
        Assert.DoesNotContain(audit, a => a.Result.Status == ProbeStatus.Timeout);
        Assert.Equal(SessionState.Stopped, target.State);
    }

    private sealed class InterruptedTransport : IProbeTransport
    {
        private int _started;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
        {
            if (Interlocked.Increment(ref _started) == 4) Ready.TrySetResult();
            if (ttl == 1) return new("192.0.2.1", 1, ProbeStatus.TimeExceeded);
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new(null, null, ProbeStatus.Timeout);
        }
    }

    [Theory]
    [InlineData(ProbeStatus.Timeout, ProbeResponseKind.None, "NoResponse")]
    [InlineData(ProbeStatus.Unreachable, ProbeResponseKind.NetworkRejection, "ProbeRejected")]
    [InlineData(ProbeStatus.Success, ProbeResponseKind.UdpPortUnreachable, "Reachable")]
    [InlineData(ProbeStatus.Success, ProbeResponseKind.UdpReply, "Responded")]
    public void ApiUdpInterpretationDoesNotClaimUnreachableOrServiceHealthWithoutEvidence(
        ProbeStatus status, ProbeResponseKind kind, string expected)
    {
        var session = new TargetSession(Guid.NewGuid(), "1.1.1.1", "1.1.1.1", Options(2500, 30));
        var statistics = new HopStatistics();
        statistics.Add(DateTimeOffset.UtcNow, new("1.1.1.1", status == ProbeStatus.Success ? 10 : null, status)
            { ResponseKind = kind }, 10);
        var destination = statistics.Snapshot(0, "1.1.1.1", null, true);
        session.LatestSnapshot = new(session.Id, session.Target, session.ResolvedAddress,
            DateTimeOffset.UtcNow, 1, false, [], destination, TimeSpan.Zero);
        var health = typeof(LocalWebServer).GetMethod("GetHealth", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(expected, health.Invoke(null, new object[] { session }));
    }

    [Fact]
    public void ApiInterpretsLastMeasurementConfigurationUntilNewSettingsProduceANewRound()
    {
        var measuredOptions = Options(2500, 30);
        var session = new TargetSession(Guid.NewGuid(), "1.1.1.1", "1.1.1.1", measuredOptions with { Protocol = ProbeProtocol.Tcp });
        var stats = new HopStatistics();
        stats.Add(DateTimeOffset.UtcNow, new("1.1.1.1", 18, ProbeStatus.Success)
            { ResponseKind = ProbeResponseKind.UdpPortUnreachable }, 10);
        session.LatestSnapshot = new(session.Id, session.Target, session.ResolvedAddress,
            DateTimeOffset.UtcNow, 1, true, [], stats.Snapshot(0, "1.1.1.1", null, true), TimeSpan.Zero)
            { MeasurementOptions = measuredOptions };
        Assert.Equal("Reachable", typeof(LocalWebServer).GetMethod("GetHealth",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { session }));
    }

    [Fact]
    public void EndpointSubsequenceDependsOnNumberOfActuallyDispatchedProbes()
    {
        var sequence = new UdpPortSequence();
        var options = Options(2500, 30) with { DestinationPort = 33434, UdpPortMode = UdpPortMode.Range, UdpEndPort = 33625 };
        var endpoints = new List<int>();
        for (var round = 0; round < 65; round++)
        {
            endpoints.Add(sequence.Next(options));
            for (var hop = 1; hop <= 8; hop++) sequence.Next(options);
        }
        Assert.Equal(64, endpoints.Take(64).Distinct().Count());
        Assert.Equal(endpoints[0], endpoints[64]);
        Assert.Equal(33434 + 9, endpoints[1]);
    }

    private static ProbeOptions Options(int interval, int hops) => new()
    { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, MaxHops = hops, Concurrency = 6,
        Interval = TimeSpan.FromMilliseconds(interval), ResolveHostnames = false };

    private sealed class ReviewTransport(int delay, bool changingRouter = false) : IProbeTransport
    {
        private int _round, _active;
        public bool OverlappedRounds;
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
        {
            if (ttl == 255)
            {
                if (Volatile.Read(ref _active) != 0) OverlappedRounds = true;
                Interlocked.Increment(ref _round);
            }
            Interlocked.Increment(ref _active);
            try
            {
                await Task.Delay(delay, token);
                if (!changingRouter || ttl == 255) return new(null, null, ProbeStatus.Timeout);
                if (ttl == 2) return new(destination.ToString(), 20, ProbeStatus.Success);
                return _round == 4 ? new(null, null, ProbeStatus.Timeout)
                    : new(_round == 2 ? "192.0.2.2" : "192.0.2.1", _round * 10, ProbeStatus.TimeExceeded);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
