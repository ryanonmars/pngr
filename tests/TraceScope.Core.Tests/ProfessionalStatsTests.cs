using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class ProfessionalStatsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowedStatisticsExcludeErrorsAndDoNotBridgeLossForJitter()
    {
        LatencySample[] samples = [new(At.AddMinutes(-2), 1000, ProbeStatus.Success),
            new(At.AddSeconds(-4), 10, ProbeStatus.Success), new(At.AddSeconds(-3), null, ProbeStatus.Timeout),
            new(At.AddSeconds(-2), 100, ProbeStatus.Success), new(At.AddSeconds(-1), 120, ProbeStatus.Success),
            new(At, null, ProbeStatus.Error)];
        var result = MonitoringStatistics.Calculate(samples, TimeSpan.FromSeconds(30), At, TimeSpan.FromSeconds(2));
        Assert.Equal(4, result.Sent);
        Assert.Equal(3, result.Received);
        Assert.Equal(1, result.Errors);
        Assert.Equal(25, result.LossPercent);
        Assert.Equal(230.0 / 3, result.AverageMs);
        Assert.Equal(20, result.JitterMs);
        Assert.Equal(3, result.AggregatedPoints);
        Assert.InRange(result.Mos!.Value, 1, 4.5);
    }

    [Fact]
    public void MosEstimateFallsWithMeasuredDelayJitterAndLoss()
    {
        var healthy = MonitoringStatistics.EstimateMos(20, 0, 0);
        Assert.True(healthy > MonitoringStatistics.EstimateMos(500, 100, 10));
        Assert.True(healthy > MonitoringStatistics.EstimateMos(20, 0, 30));
        Assert.Null(MonitoringStatistics.Calculate([new(At, null, ProbeStatus.Timeout)], TimeSpan.FromMinutes(1), At).Mos);
    }

    [Fact]
    public void AlertsRequireSustainedBreachRespectCooldownAndRecover()
    {
        var engine = new AlertEngine();
        var id = Guid.NewGuid();
        var rule = new AlertRule { Condition = AlertCondition.Latency, Threshold = 50,
            EvaluationPeriod = TimeSpan.FromSeconds(5), Cooldown = TimeSpan.FromSeconds(20), RecoveryPeriod = TimeSpan.FromSeconds(3) };
        Assert.Empty(engine.Evaluate(Snapshot(id, At, 100), [rule]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(4), 100), [rule]));
        Assert.Equal("Alert", Assert.Single(engine.Evaluate(Snapshot(id, At.AddSeconds(5), 100), [rule])).Kind);
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(6), 100), [rule]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(7), 10), [rule]));
        Assert.Equal("Recovery", Assert.Single(engine.Evaluate(Snapshot(id, At.AddSeconds(10), 10), [rule])).Kind);
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(11), 100), [rule]));
        engine.ClearSession(id);
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(50), 100), [rule]));
    }

    [Fact]
    public void ProbeErrorsCannotCreateOutageOrPrematurelyRecover()
    {
        var engine = new AlertEngine();
        var id = Guid.NewGuid();
        var rule = new AlertRule { Condition = AlertCondition.Unreachable, EvaluationPeriod = TimeSpan.FromSeconds(2),
            RecoveryPeriod = TimeSpan.FromSeconds(2), Cooldown = TimeSpan.FromMinutes(1) };
        Assert.Empty(engine.Evaluate(Snapshot(id, At, null, ProbeStatus.Error), [rule]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(20), null, ProbeStatus.Timeout), [rule]));
        Assert.Single(engine.Evaluate(Snapshot(id, At.AddSeconds(22), null, ProbeStatus.Timeout), [rule]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(23), null, ProbeStatus.Error), [rule]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(24), 10), [rule]));
        Assert.Equal("Recovery", Assert.Single(engine.Evaluate(Snapshot(id, At.AddSeconds(26), 10), [rule])).Kind);
    }

    [Fact]
    public void RecoveryOnlyRulesAndRouteChangesHaveIndependentState()
    {
        var engine = new AlertEngine();
        var id = Guid.NewGuid();
        var recovery = new AlertRule { Condition = AlertCondition.Recovery, EvaluationPeriod = TimeSpan.FromSeconds(2),
            RecoveryPeriod = TimeSpan.FromSeconds(2) };
        var route = new AlertRule { Condition = AlertCondition.RouteChanged, Cooldown = TimeSpan.FromSeconds(5) };
        Assert.Empty(engine.Evaluate(Snapshot(id, At, null, ProbeStatus.Timeout), [recovery, route]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(2), null, ProbeStatus.Timeout), [recovery, route]));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(3), 10), [recovery, route]));
        var events = engine.Evaluate(Snapshot(id, At.AddSeconds(5), 10) with
            { RouteChanged = true, RouteDescription = "Hop 2 changed" }, [recovery, route]);
        Assert.Contains(events, e => e.Kind == "Recovery");
        Assert.Contains(events, e => e.Kind == "RouteChange" && e.Message.Contains("Hop 2"));
        Assert.Empty(engine.Evaluate(Snapshot(id, At.AddSeconds(6), 10) with { RouteChanged = true }, [route]));
    }

    [Fact]
    public async Task ExistingHistorySupportsEventsAggregationHopFilteringAndRetention()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TraceScope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        try
        {
            var store = new HistoryStore(path);
            // Initialize only the original schema first, then reopen and migrate additively.
            Assert.Empty(await store.LoadSessionsAsync());
            store = new HistoryStore(path);
            var id = Guid.NewGuid();
            var current = DateTimeOffset.UtcNow;
            for (var i = 0; i < 30; i++)
                await store.AppendAsync(Snapshot(id, current.AddSeconds(i - 30), i % 3 == 0 ? null : i,
                    i % 3 == 0 ? ProbeStatus.Timeout : ProbeStatus.Success) with { Round = i }, default);
            await store.AppendAsync(Snapshot(id, current.AddDays(-5), 500) with { Round = 100 }, default);
            var item = new MonitoringEvent(Guid.NewGuid(), id, current, "Alert", "Measured loss threshold", "test");
            await store.AppendEventAsync(item);
            await store.AppendEventAsync(item); // IDs make replay idempotent.
            Assert.Equal(item, Assert.Single(await store.LoadEventsAsync(id)));
            var buckets = await store.LoadAggregatedSamplesAsync(id, 0, current.AddMinutes(-1), current, 5);
            Assert.InRange(buckets.Count, 1, 5);
            Assert.Equal(30, buckets.Sum(b => b.Count));
            Assert.Equal(20, buckets.Sum(b => b.Received));
            Assert.Equal(29, buckets.Max(b => b.MaximumMs));
            Assert.DoesNotContain(buckets, b => b.AverageMs > 29);
            Assert.Equal(31, (await store.LoadSamplesForHopAsync(id, 0)).Count);
            Assert.Empty(await store.LoadSamplesForHopAsync(id, 2));
            Assert.Equal(1, await store.RetainAsync(TimeSpan.FromDays(1)));
            Assert.Equal(30, (await new HistoryStore(path).LoadSamplesAsync(id)).Count);
            Assert.Single(await new HistoryStore(path).LoadEventsAsync(id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AggregationSeparatesLocalErrorsFromRealPacketLoss()
    {
        var path = Path.Combine(Path.GetTempPath(), "TraceScope-tests", Guid.NewGuid() + ".db");
        var store = new HistoryStore(path);
        var id = Guid.NewGuid();
        try
        {
            await store.AppendAsync(Snapshot(id, At, 10), default);
            await store.AppendAsync(Snapshot(id, At.AddSeconds(1), null, ProbeStatus.Error), default);
            await store.AppendAsync(Snapshot(id, At.AddSeconds(2), null, ProbeStatus.Timeout), default);
            var point = Assert.Single(await store.LoadAggregatedSamplesAsync(id, 0, At, At.AddSeconds(3), 1));
            Assert.Equal(2, point.Count);
            Assert.Equal(1, point.Errors);
            Assert.Equal(50, point.LossPercent);
            Assert.Equal(10, point.AverageMs);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task LargeSessionAggregationRemainsBoundedAndCountsEveryMeasurement()
    {
        var path = Path.Combine(Path.GetTempPath(), "TraceScope-tests", Guid.NewGuid() + ".db");
        var store = new HistoryStore(path);
        var id = Guid.NewGuid();
        try
        {
            await store.InitializeProfessionalAsync();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status) VALUES($session,$round,0,'127.0.0.1',1,$at,$latency,$status)";
                command.Parameters.AddWithValue("$session", id.ToString());
                var round = command.Parameters.Add("$round", Microsoft.Data.Sqlite.SqliteType.Integer);
                var time = command.Parameters.Add("$at", Microsoft.Data.Sqlite.SqliteType.Text);
                var latency = command.Parameters.Add("$latency", Microsoft.Data.Sqlite.SqliteType.Real);
                var status = command.Parameters.Add("$status", Microsoft.Data.Sqlite.SqliteType.Integer);
                for (var i = 0; i < 10000; i++)
                {
                    round.Value = i;
                    time.Value = At.AddSeconds(i).ToString("O");
                    var isError = i % 100 == 0;
                    var isTimeout = i % 20 == 1;
                    latency.Value = isError || isTimeout ? DBNull.Value : (double)(i % 50);
                    status.Value = (int)(isError ? ProbeStatus.Error : isTimeout ? ProbeStatus.Timeout : ProbeStatus.Success);
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }
            var points = await store.LoadAggregatedSamplesAsync(id, 0, At, At.AddSeconds(10000), 100);
            Assert.InRange(points.Count, 1, 100);
            Assert.Equal(9900, points.Sum(p => p.Count));
            Assert.Equal(9400, points.Sum(p => p.Received));
            Assert.Equal(100, points.Sum(p => p.Errors));
            Assert.All(points, p => Assert.InRange(p.MaximumMs!.Value, 0, 49));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    private static RoundSnapshot Snapshot(Guid id, DateTimeOffset timestamp, double? latency,
        ProbeStatus status = ProbeStatus.Success)
    {
        var sample = new LatencySample(timestamp, latency, status);
        var destination = new HopSnapshot(0, "127.0.0.1", null, latency, latency, latency, latency,
            latency is null ? null : 0, 1, latency is null ? 0 : 1, latency is null ? 100 : 0, true, [sample], status);
        return new(id, "127.0.0.1", "127.0.0.1", timestamp, 1, latency is not null,
            [], destination, TimeSpan.FromMilliseconds(1));
    }
}
