using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.Sqlite;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class MeasurementIntegrityTests
{
    [Theory]
    [InlineData(4, 0, 0, 0)]
    [InlineData(0, 4, 0, 100)]
    [InlineData(2, 2, 1, 50)]
    [InlineData(0, 0, 4, 0)]
    public void LossUsesEligibleAttemptsAndNeverTurnsLocalFailuresIntoRemoteLoss(int replies, int failures, int errors, double expectedLoss)
    {
        var stats = new HopStatistics();
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < replies; i++) stats.Add(at, new("1.1.1.1", 20, ProbeStatus.Success), 100);
        for (var i = 0; i < failures; i++) stats.Add(at, new(null, null, i % 2 == 0 ? ProbeStatus.Timeout : ProbeStatus.Unreachable), 100);
        for (var i = 0; i < errors; i++) stats.Add(at, new(null, null, ProbeStatus.Error), 100);
        var hop = stats.Snapshot(0, "1.1.1.1", null, true);
        var window = MonitoringStatistics.Calculate(hop.History, TimeSpan.FromSeconds(1), at);
        Assert.Equal(replies + failures, hop.Sent);
        Assert.Equal(replies, hop.Received);
        Assert.Equal(expectedLoss, hop.LossPercent);
        Assert.Equal(hop.Sent, window.Sent);
        Assert.Equal(hop.Received, window.Received);
        Assert.Equal(hop.LossPercent, window.LossPercent);
        Assert.Equal(errors, window.Errors);
    }

    [Fact]
    public async Task MacIcmpEndpointUsesNativePacketRttAndRetainsIndependentProbeIdentity()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var result = await new SystemProbeTransport().ProbeAsync(IPAddress.Loopback, 255, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(ProbeStatus.Success, result.Status);
        Assert.Equal(ProbeResponseKind.IcmpEchoReply, result.ResponseKind);
        Assert.Equal(ProbeTransmission.Sent, result.Transmission);
        Assert.Contains("native ICMP packet RTT", result.Detail);
        Assert.True(result.Responded);
    }

    [Fact]
    public void LateResponseIsRejectedEvenBeforeCancellationTimerDispatches()
    {
        ProbeDeadline.Verify(TimeSpan.FromMilliseconds(999), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => ProbeDeadline.Verify(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => ProbeDeadline.Verify(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task NetworkDiagnosticsAndLossAgreeAcrossLiveStoredSelectedAggregatedAndExportedMeasurements()
    {
        using var directory = new TestDirectory();
        var store = new HistoryStore(Path.Combine(directory.Path, "history.db"));
        var id = Guid.NewGuid();
        var stats = new HopStatistics();
        var at = DateTimeOffset.UtcNow.AddSeconds(-10);
        ProbeResult[] outcomes =
        [
            new("1.1.1.1", 10, ProbeStatus.Success, "Matched ICMP type 3, code 3") { ResponseKind = ProbeResponseKind.UdpPortUnreachable, Transmission = ProbeTransmission.Sent },
            new(null, null, ProbeStatus.Timeout, "Kernel timeout") { Transmission = ProbeTransmission.Sent },
            new("192.0.2.1", null, ProbeStatus.Unreachable, "ICMP network unreachable, code 0") { ResponseKind = ProbeResponseKind.NetworkRejection, Transmission = ProbeTransmission.Sent },
            new(null, null, ProbeStatus.Unreachable, "Local routing table: network unreachable") { Transmission = ProbeTransmission.NotSent },
            new(null, null, ProbeStatus.Error, "Local socket setup failed") { Transmission = ProbeTransmission.NotSent },
            new("1.1.1.1", 20, ProbeStatus.Success, "UDP datagram") { ResponseKind = ProbeResponseKind.UdpReply, Transmission = ProbeTransmission.Sent }
        ];
        for (var i = 0; i < outcomes.Length; i++)
        {
            stats.Add(at.AddSeconds(i), outcomes[i], 100);
            await store.AppendAsync(new(id, "1.1.1.1", "1.1.1.1", at.AddSeconds(i), i + 1,
                false, [], stats.Snapshot(0, "1.1.1.1", null, true), TimeSpan.Zero), CancellationToken.None);
        }
        var live = stats.Snapshot(0, "1.1.1.1", null, true);
        var stored = await store.LoadSamplesForHopAsync(id, 0);
        Assert.Equal(outcomes.Select(o => o.Status), stored.Select(s => s.Status));
        Assert.Equal(outcomes.Select(o => o.ResponseKind), stored.Select(s => s.ResponseKind));
        Assert.Equal(outcomes.Select(o => o.Transmission), stored.Select(s => s.Transmission));
        Assert.Equal(outcomes.Select(o => o.Detail), stored.Select(s => s.Detail));
        var selected = Assert.Single(await store.LoadSelectionStatisticsAsync(id, at, at.AddSeconds(10)));
        Assert.Equal(live.Sent, selected.Sent);
        Assert.Equal(live.Received, selected.Received);
        Assert.Equal(60, live.LossPercent);
        Assert.Equal(live.LossPercent, selected.LossPercent);
        Assert.Equal(live.AverageMs, selected.AverageMs);
        var aggregate = Assert.Single(await store.LoadAggregatedSamplesAsync(id, 0, at, at.AddSeconds(10), 1));
        Assert.Equal(live.Sent, aggregate.Count);
        Assert.Equal(live.Received, aggregate.Received);
        Assert.Equal(live.LossPercent, aggregate.LossPercent);
        Assert.Equal(1, aggregate.Errors);
        using var csv = new StringWriter();
        await store.ExportCsvAsync(id, csv);
        Assert.Contains("Detail,ResponseKind,Transmission", csv.ToString());
        Assert.Contains("UdpPortUnreachable,Sent", csv.ToString());
        Assert.Contains("Local routing table: network unreachable", csv.ToString());
        Assert.Contains("ICMP network unreachable, code 0", csv.ToString());
    }

    [Fact]
    public async Task ExistingDatabaseMigrationPreservesRowsAndDoesNotInventResponseEvidence()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Path, "old.db");
        var id = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE samples(id INTEGER PRIMARY KEY,session_id TEXT,round INTEGER,hop INTEGER,address TEXT,is_destination INTEGER,timestamp TEXT,latency_ms REAL,status INTEGER); INSERT INTO samples VALUES(1,$id,1,0,'1.1.1.1',1,'2026-10-13T12:00:00.0000000+00:00',20,0)";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        var store = new HistoryStore(database);
        var old = Assert.Single(await store.LoadSamplesAsync(id));
        Assert.Equal(20, old.LatencyMs);
        Assert.Equal(ProbeStatus.Success, old.Status);
        Assert.Equal(ProbeResponseKind.None, old.ResponseKind);
        Assert.Equal(ProbeTransmission.Unknown, old.Transmission);
        Assert.Null(old.Detail);
        Assert.Single(await new HistoryStore(database).LoadSamplesAsync(id));
    }

    [Theory]
    [InlineData(UdpPortMode.Fixed, 33434, 33625)]
    [InlineData(UdpPortMode.Range, 33434, 33434)]
    [InlineData(UdpPortMode.Range, 33434, 33436)]
    public void PortSequenceWrapsAndSinglePortRangesBehaveAsFixed(UdpPortMode mode, int start, int end)
    {
        var sequence = new UdpPortSequence();
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = start, UdpPortMode = mode, UdpEndPort = end };
        for (var i = 0; i < 200; i++) Assert.Equal(mode == UdpPortMode.Fixed ? start : start + i % (end - start + 1), sequence.Next(options));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EndpointSharesConcurrencyBudgetAndRouterReplyCannotBecomeDestinationSuccess(int concurrency)
    {
        using var directory = new TestDirectory();
        var transport = new ScheduledTransport();
        await using var engine = new TraceEngine(Path.Combine(directory.Path, "engine.db"), transport: transport);
        var completed = new TaskCompletionSource<RoundSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) => completed.TrySetResult(snapshot);
        var target = await engine.StartTargetAsync("127.0.0.1", new ProbeOptions()
        {
            Protocol = ProbeProtocol.Udp, DestinationPort = 33434, UdpPortMode = UdpPortMode.Range, UdpEndPort = 33436,
            Concurrency = concurrency, MaxHops = 3, ResolveHostnames = false, Interval = TimeSpan.FromSeconds(10)
        });
        var snapshot = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync(target.Id);
        Assert.InRange(transport.Peak, 1, concurrency);
        Assert.Equal(33435, transport.Ports[1]);
        Assert.Equal(33436, transport.Ports[2]);
        Assert.Equal(33434, transport.Ports[3]);
        Assert.Equal(33434, transport.Ports[255]);
        Assert.Equal(ProbeStatus.Unreachable, snapshot.Destination.Status);
        Assert.Equal(100, snapshot.Destination.LossPercent);
        Assert.Equal(0, snapshot.Destination.Received);
        Assert.Contains("192.0.2.254", snapshot.Destination.Detail);
        Assert.Equal(0, transport.Active);
        Assert.Equal(ProbeStatus.Unreachable, Assert.Single((await engine.Storage.LoadSamplesForHopAsync(target.Id, 0))).Status);
    }

    private sealed class ScheduledTransport : IConfiguredProbeTransport
    {
        public readonly ConcurrentDictionary<int, int> Ports = new();
        public int Active, Peak;
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token) => throw new NotSupportedException();
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options, CancellationToken token)
        {
            Ports[ttl] = options.DestinationPort;
            var active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = Peak; } while (active > previous && Interlocked.CompareExchange(ref Peak, active, previous) != previous);
            try
            {
                await Task.Delay(30, token);
                return new(ttl == 255 ? "192.0.2.254" : ttl == 3 ? destination.ToString() : $"192.0.2.{ttl}", 10,
                    ttl == 3 ? ProbeStatus.Success : ProbeStatus.TimeExceeded)
                { Transmission = ProbeTransmission.Sent, ResponseKind = ttl == 3 ? ProbeResponseKind.UdpPortUnreachable : ProbeResponseKind.IcmpTimeExceeded };
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TraceScope-v07-integrity", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, true); }
    }
}
