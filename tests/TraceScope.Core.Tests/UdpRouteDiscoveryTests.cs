using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class UdpRouteDiscoveryTests
{
    private const string Destination = "1.1.1.1";
    private static ProbeResult Reply(int hop, int terminal = 8) => hop < terminal
        ? new("192.0.2." + hop, 10, ProbeStatus.TimeExceeded)
        : new(Destination, 20, ProbeStatus.Success);
    private static ProbeResult Silence => new(null, null, ProbeStatus.Timeout);

    [Fact]
    public async Task DelayedLowerDestinationWinsAndUnscheduledHigherProbesNeverRun()
    {
        var discovery = new UdpRouteDiscovery();
        var transmitted = new List<int>();
        var visible = await discovery.DiscoverAsync(30, 3, Destination, async hop =>
        {
            transmitted.Add(hop);
            await Task.Delay(hop == 8 ? 40 : 1);
            return Reply(hop);
        });
        Assert.Equal(8, discovery.TerminalHop);
        Assert.Equal(Enumerable.Range(1, 9), transmitted);
        Assert.Equal(Enumerable.Range(1, 8), visible.Select(x => x.Hop));
        Assert.Single(visible, x => x.Result.Address == Destination);
    }

    [Fact]
    public async Task SilenceKeepsKnownBoundaryAndDoesNotCreateThirtyPositions()
    {
        var discovery = new UdpRouteDiscovery();
        await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(Reply(hop)));
        for (var round = 0; round < UdpRouteDiscovery.SilentRoundsBeforeRediscovery - 1; round++)
        {
            var sent = new List<int>();
            var visible = await discovery.DiscoverAsync(30, 3, Destination, hop =>
            { sent.Add(hop); return Task.FromResult(hop < 8 ? Reply(hop) : Silence); });
            Assert.Equal(8, discovery.TerminalHop);
            Assert.Equal(8, sent.Count);
            Assert.Equal(8, visible.Length);
            Assert.DoesNotContain(visible, x => x.Result.Status == ProbeStatus.Success);
        }
    }

    [Fact]
    public async Task PersistentlySilentKnownTerminalEventuallyTriggersRealRediscovery()
    {
        var discovery = new UdpRouteDiscovery();
        await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(Reply(hop)));
        for (var i = 0; i < UdpRouteDiscovery.SilentRoundsBeforeRediscovery; i++)
            await discovery.DiscoverAsync(30, 3, Destination, _ => Task.FromResult(Silence));
        Assert.Null(discovery.TerminalHop);
        var sent = new List<int>();
        var observations = await discovery.DiscoverAsync(30, 3, Destination, hop =>
        { sent.Add(hop); return Task.FromResult(Silence); });
        Assert.Equal(Enumerable.Range(1, 30), sent);
        Assert.Equal(30, observations.Length);
        Assert.DoesNotContain(observations, x => x.Result.Responded);
    }

    [Fact]
    public async Task PositiveRouterEvidenceDiscoversLongerRouteThenShorterRoute()
    {
        var discovery = new UdpRouteDiscovery();
        await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(Reply(hop)));
        var longer = await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(Reply(hop, 11)));
        Assert.Equal(11, discovery.TerminalHop);
        Assert.Equal(11, longer.Length);
        Assert.Equal(ProbeStatus.TimeExceeded, longer.Single(x => x.Hop == 8).Result.Status);
        var shorter = await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(Reply(hop, 5)));
        Assert.Equal(5, discovery.TerminalHop);
        Assert.Equal(5, shorter.Length);
    }

    [Fact]
    public async Task AlternateRouterBeyondDestinationIsRetainedAndDiscoveryContinues()
    {
        var discovery = new UdpRouteDiscovery();
        var visible = await discovery.DiscoverAsync(30, 3, Destination, hop => Task.FromResult(
            hop == 8 ? Reply(8) : Reply(hop, 11)));
        Assert.Equal(11, discovery.TerminalHop);
        Assert.Contains(visible, x => x.Hop == 9 && x.Result.Status == ProbeStatus.TimeExceeded);
        Assert.Contains(visible, x => x.Hop == 8 && x.Result.Status == ProbeStatus.Success);
    }

    [Fact]
    public async Task EngineKeepsSilentTerminalWithoutDuplicateDestinationRowsAndPersistsFailures()
    {
        var database = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid() + ".db");
        var transport = new RoundTransport();
        await using var engine = new TraceEngine(database, new ProbeOptions
        { Protocol = ProbeProtocol.Udp, MaxHops = 30, Concurrency = 3, ResolveHostnames = false,
            Interval = TimeSpan.FromMilliseconds(100) }, transport);
        var snapshots = new List<RoundSnapshot>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        {
            snapshots.Add(snapshot);
            if (snapshot.Round == 5) { engine.Pause(snapshot.SessionId); ready.TrySetResult(); }
        };
        var session = await engine.StartTargetAsync(Destination);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(session.Id);
        Assert.Equal(new[] { 8, 8, 8, 11, 5 }, snapshots.Select(x => x.Hops.Count));
        Assert.All(snapshots, x => Assert.Single(x.Hops, h => h.IsDestination));
        Assert.False(snapshots[1].DestinationReached);
        Assert.Equal(ProbeStatus.Timeout, snapshots[1].Hops[7].Status);
        Assert.Equal(50, snapshots[1].Hops[7].LossPercent);
        var stored = await engine.Storage.LoadSamplesAsync(session.Id);
        Assert.Contains(stored, x => x.Hop == 8 && x.Status == ProbeStatus.Timeout);
        Assert.DoesNotContain(stored, x => x.Hop > 11);
    }

    private sealed class RoundTransport : IProbeTransport
    {
        private int _round;
        public async Task<ProbeResult> ProbeAsync(System.Net.IPAddress destination, int ttl,
            TimeSpan timeout, CancellationToken token)
        {
            if (ttl == 255) { Interlocked.Increment(ref _round); return Reply(255); }
            await Task.Delay(ttl == 8 ? 15 : 1, token);
            var round = _round;
            return round is 2 or 3 && ttl == 8 ? Silence : Reply(ttl, round == 4 ? 11 : round == 5 ? 5 : 8);
        }
    }

    [Fact]
    public async Task PortRangeWrapsOnlyForActualDispatchesAcrossRounds()
    {
        var discovery = new UdpRouteDiscovery();
        var sequence = new UdpPortSequence();
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434,
            UdpPortMode = UdpPortMode.Range, UdpEndPort = 33436 };
        var ports = new List<int>();
        for (var round = 0; round < 3; round++)
            await discovery.DiscoverAsync(30, 3, Destination, hop =>
            { ports.Add(sequence.Next(options)); return Task.FromResult(Reply(hop)); });
        Assert.Equal(25, ports.Count); // First wave crosses terminal; later rounds end exactly at 8.
        Assert.Equal(Enumerable.Range(0, 25).Select(i => 33434 + i % 3), ports);
    }
}
