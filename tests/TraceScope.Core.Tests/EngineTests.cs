using System.Net;
using System.Net.NetworkInformation;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class EngineTests
{
    private static string Database() => Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid() + ".db");

    [Fact]
    public void StatisticsKeepTimeoutsAndBoundHistory()
    {
        var stats = new HopStatistics();
        var at = DateTimeOffset.UtcNow;
        stats.Add(at, new ProbeResult("127.0.0.1", 10, ProbeStatus.Success), 3);
        stats.Add(at, new ProbeResult(null, null, ProbeStatus.Timeout), 3);
        stats.Add(at, new ProbeResult("127.0.0.1", 30, ProbeStatus.Success), 3);
        stats.Add(at, new ProbeResult("127.0.0.1", 20, ProbeStatus.Success), 3);
        var snapshot = stats.Snapshot(1, "127.0.0.1", null, true);
        Assert.Equal(4, snapshot.Sent);
        Assert.Equal(3, snapshot.Received);
        Assert.Equal(25, snapshot.LossPercent);
        Assert.Equal(20, snapshot.AverageMs);
        Assert.Equal(10, snapshot.MinimumMs);
        Assert.Equal(30, snapshot.MaximumMs);
        Assert.Equal(10, snapshot.JitterMs); // Lost probes break a jitter pair, consistently with historical statistics.
        Assert.Equal(3, snapshot.History.Count);
        Assert.Null(snapshot.History[0].LatencyMs);
    }

    [Theory]
    [InlineData("traceroute to 1.1.1.1 (1.1.1.1), 1 hops max\n 1  192.168.30.1  0.677 ms\n", "1.1.1.1", "192.168.30.1", 0.677, ProbeStatus.TimeExceeded)]
    [InlineData(" 2  1.1.1.1  12.340 ms\n", "1.1.1.1", "1.1.1.1", 12.34, ProbeStatus.Success)]
    [InlineData(" 2  ::1  0.030 ms\n", "::1", "::1", 0.03, ProbeStatus.Success)]
    [InlineData(" 2  1.1.1.1  12.340 ms !\n", "1.1.1.1", "1.1.1.1", 12.34, ProbeStatus.Success)]
    [InlineData(" 1  192.168.30.1  0.677 ms !\n", "1.1.1.1", "192.168.30.1", 0.677, ProbeStatus.TimeExceeded)]
    public void MacTracerouteParsesActualResponderAndRtt(string text, string target, string expectedIp,
        double expectedRtt, ProbeStatus expectedStatus)
    {
        var result = SystemProbeTransport.ParseMacTraceroute(text, target);
        Assert.Equal(expectedIp, result.Address);
        Assert.Equal(expectedRtt, result.LatencyMs);
        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public void MacTracerouteDistinguishesTimeoutAndUnreachable()
    {
        Assert.Equal(ProbeStatus.Timeout, SystemProbeTransport.ParseMacTraceroute(" 1 *\n", "1.1.1.1").Status);
        var unreachable = SystemProbeTransport.ParseMacTraceroute(" 1  192.168.1.1  1.234 ms !H", "1.1.1.1");
        Assert.Equal(ProbeStatus.Unreachable, unreachable.Status);
        Assert.Null(unreachable.LatencyMs);
        Assert.Equal(ProbeStatus.Error, SystemProbeTransport.ParseMacTraceroute("", "1.1.1.1", 1, "permission").Status);
    }

    [Fact]
    public void WindowsTtlExpiryDoesNotReportDotnetArtificialZeroRtt()
    {
        var router = IPAddress.Parse("192.0.2.1");
        var expired = SystemProbeTransport.NormalizePingReply(IPStatus.TtlExpired, router,
            0, TimeSpan.FromMilliseconds(6.25));
        Assert.Equal("192.0.2.1", expired.Address);
        Assert.Equal(6.25, expired.LatencyMs);
        Assert.Equal(ProbeStatus.TimeExceeded, expired.Status);
        var fastReply = SystemProbeTransport.NormalizePingReply(IPStatus.Success, router,
            0, TimeSpan.FromMilliseconds(6.25));
        Assert.Equal(0, fastReply.LatencyMs);
        var timeout = SystemProbeTransport.NormalizePingReply(IPStatus.TimedOut, IPAddress.Any,
            0, TimeSpan.FromMilliseconds(1000));
        Assert.Null(timeout.Address);
        Assert.Null(timeout.LatencyMs);
    }

    [Fact]
    public async Task EngineSeparatesRouterSilenceFromEndpointAndPersistsHistory()
    {
        var database = Database();
        var transport = new FakeTransport();
        await using var engine = new TraceEngine(database, TestOptions(), transport);
        var rounds = new List<RoundSnapshot>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            lock (rounds) rounds.Add(snapshot);
            if (snapshot.Round >= 3) completed.TrySetResult();
        };
        var session = await engine.StartTargetAsync("127.0.0.1");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(session.Id);
        var last = session.LatestSnapshot!;
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.True(last.DestinationReached);
        Assert.Equal(2, last.Hops.Count);
        Assert.Equal(100, last.Hops[0].LossPercent);
        Assert.Equal(0, last.Destination.LossPercent);
        Assert.Equal(0, last.Hops[1].LossPercent);
        Assert.Equal(0, transport.OverlappingProbeCount);
        var sessions = await engine.Storage.LoadSessionsAsync();
        Assert.Single(sessions);
        Assert.NotNull(sessions[0].StoppedAt);
        var samples = await engine.Storage.LoadSamplesAsync(session.Id);
        Assert.True(samples.Count >= 9);
        Assert.Contains(samples, s => s.Hop == 0 && s.IsDestination && s.LatencyMs == 8);
        var csv = Path.ChangeExtension(database, ".csv");
        await engine.Storage.ExportCsvAsync(session.Id, csv);
        Assert.Contains("LatencyMs,Status", await File.ReadAllTextAsync(csv));
        var reloaded = await new HistoryStore(database).LoadSessionsAsync();
        Assert.Equal(session.Id, Assert.Single(reloaded).Id);
    }

    [Fact]
    public async Task PausePreventsNewRoundsAndResumeContinues()
    {
        await using var engine = new TraceEngine(Database(), TestOptions(), new FakeTransport());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) => { if (snapshot.Round >= 2) ready.TrySetResult(); };
        var session = await engine.StartTargetAsync("127.0.0.1");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.Pause(session.Id);
        await Task.Delay(200);
        var round = session.LatestSnapshot!.Round;
        await Task.Delay(250);
        Assert.Equal(round, session.LatestSnapshot!.Round);
        Assert.Equal(SessionState.Paused, session.State);
        engine.Resume(session.Id);
        await Task.Delay(250);
        Assert.True(session.LatestSnapshot!.Round > round);
        await engine.StopAsync(session.Id);
    }

    [Fact]
    public async Task RouteChangeDoesNotCombineDifferentRouterStatistics()
    {
        var transport = new RouteChangeTransport();
        await using var engine = new TraceEngine(Database(), TestOptions() with { MaxHops = 2 }, transport);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) => { if (snapshot.Round == 3) { engine.Pause(snapshot.SessionId); ready.TrySetResult(); } };
        var session = await engine.StartTargetAsync("127.0.0.1");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = session.LatestSnapshot!.Hops[0];
        Assert.Equal("192.0.2.2", first.Address);
        Assert.Equal(1, first.Sent);
        Assert.Equal(30, first.AverageMs);
        await engine.StopAsync(session.Id);
    }

    [Fact]
    public async Task MultipleTargetsRunIndependentlyAndStopCancelsInFlightProbe()
    {
        var transport = new CancellationTransport();
        await using var engine = new TraceEngine(Database(), TestOptions(), transport);
        var first = await engine.StartTargetAsync("127.0.0.1");
        var second = await engine.StartTargetAsync("127.0.0.2");
        await Task.Delay(50);
        Assert.Equal(2, engine.Sessions.Count);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await engine.StopAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionState.Stopped, first.State);
        Assert.Equal(SessionState.Running, second.State);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await engine.StopAsync(second.Id);
    }

    [Fact]
    public async Task LiveSystemTransportMeasuresLoopback()
    {
        var transport = new SystemProbeTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var hop = await transport.ProbeAsync(IPAddress.Loopback, 1, TimeSpan.FromSeconds(1), timeout.Token);
        var endpoint = await transport.ProbeAsync(IPAddress.Loopback, 255, TimeSpan.FromSeconds(1), timeout.Token);
        Assert.True(hop.Responded, $"Hop: {hop.Status}, {hop.Detail}");
        Assert.Equal("127.0.0.1", hop.Address);
        Assert.True(endpoint.Responded, $"Endpoint: {endpoint.Status}, {endpoint.Detail}");
        Assert.Equal(ProbeStatus.Success, endpoint.Status);
    }

    private static ProbeOptions TestOptions() => new()
    { Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100),
        MaxHops = 4, ResolveHostnames = false, HistoryLimit = 10 };

    private sealed class FakeTransport : IProbeTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _active = new();
        public int OverlappingProbeCount;
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_active.AddOrUpdate(ttl, 1, (_, count) => count + 1) > 1) Interlocked.Increment(ref OverlappingProbeCount);
            await Task.Delay(5, cancellationToken);
            _active.AddOrUpdate(ttl, 0, (_, count) => count - 1);
            return ttl == 1 ? new ProbeResult(null, null, ProbeStatus.Timeout)
                : new ProbeResult(destination.ToString(), 8, ProbeStatus.Success);
        }
    }

    private sealed class RouteChangeTransport : IProbeTransport
    {
        private int _firstHop;
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = ttl == 1 ? Interlocked.Increment(ref _firstHop) < 3
                ? new ProbeResult("192.0.2.1", 10, ProbeStatus.TimeExceeded)
                : new ProbeResult("192.0.2.2", 30, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 40, ProbeStatus.Success);
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationTransport : IProbeTransport
    {
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ProbeResult(null, null, ProbeStatus.Timeout);
        }
    }
}
