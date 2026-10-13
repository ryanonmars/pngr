using Microsoft.Data.Sqlite;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class HistoricalAnalysisTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TraceScope-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();
    private readonly HistoryStore _store;
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    public HistoricalAnalysisTests() => _store = new(Path.Combine(_directory, "history.db"));
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task SelectedStatisticsAreExactAndDoNotBridgeLossErrorsOrAddressChanges()
    {
        await SeedAsync([(1,"a",10,ProbeStatus.TimeExceeded), (1,"a",20,ProbeStatus.TimeExceeded),
            (1,"a",null,ProbeStatus.Timeout), (1,"a",100,ProbeStatus.TimeExceeded),
            (1,"b",500,ProbeStatus.TimeExceeded), (1,"a",120,ProbeStatus.TimeExceeded),
            (1,"a",null,ProbeStatus.Error), (1,"a",130,ProbeStatus.TimeExceeded),
            (1,"a",null,ProbeStatus.Unreachable)]);
        var rows = await _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(8));
        var a = Assert.Single(rows, r => r.Address == "a");
        Assert.Equal(7, a.Sent);
        Assert.Equal(5, a.Received);
        Assert.Equal(100d * 2 / 7, a.LossPercent);
        Assert.Equal(76, a.AverageMs);
        Assert.Equal(10, a.MinimumMs);
        Assert.Equal(130, a.MaximumMs);
        Assert.Equal(10, a.JitterMs);
        Assert.Null(a.LastMs);
        Assert.Equal(ProbeStatus.Unreachable, a.Status);
        Assert.Empty(a.History);
        var b = Assert.Single(rows, r => r.Address == "b");
        Assert.Null(b.JitterMs);
        Assert.Equal(500, b.LastMs);
        var narrow = Assert.Single(await _store.LoadSelectionStatisticsAsync(_id, At.AddSeconds(1), At.AddSeconds(3)));
        Assert.Equal(3, narrow.Sent);
        Assert.Equal(60, narrow.AverageMs);
        Assert.Null(narrow.JitterMs);
        Assert.Empty(await _store.LoadSelectionStatisticsAsync(_id, At.AddDays(1), At.AddDays(2)));
        Assert.Equal(9, (await _store.LoadSamplesAsync(_id)).Count);
    }

    [Fact]
    public async Task DestinationAndEmptyResponseSelectionsUseMeasuredStatuses()
    {
        await SeedAsync([(0,"dest",20,ProbeStatus.Success), (0,"dest",null,ProbeStatus.Timeout),
            (2,null,null,ProbeStatus.Timeout), (2,null,null,ProbeStatus.Error)]);
        var result = await _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(3));
        var destination = Assert.Single(result, h => h.Hop == 0);
        Assert.True(destination.IsDestination);
        Assert.Equal(50, destination.LossPercent);
        Assert.Null(destination.LastMs);
        var absent = Assert.Single(result, h => h.Hop == 2);
        Assert.Equal(100, absent.LossPercent);
        Assert.Null(absent.AverageMs);
        Assert.Null(absent.JitterMs);
        var bounds = await _store.LoadTimeRangeAsync(_id, 0);
        Assert.Equal((At, At.AddSeconds(1)), bounds!.Value);
        Assert.Null(await _store.LoadTimeRangeAsync(_id, 99));
    }

    [Fact]
    public async Task LargeSavedHistoryReturnsBoundedWindowsAndExactSelectionStatistics()
    {
        await SeedAsync(Enumerable.Range(0, 20_000).Select(i =>
            (0, (string?)"dest", i % 4 == 0 ? (double?)null : 10d, i % 4 == 0 ? ProbeStatus.Timeout : ProbeStatus.Success)));
        var reopened = new HistoryStore(_store.DatabasePath);
        var window = await reopened.LoadGraphWindowAsync(_id, 0, At, At.AddSeconds(19_999), 100);
        Assert.True(window.IsAggregated);
        Assert.Empty(window.RawSamples);
        Assert.InRange(window.AggregatedSamples.Count, 1, 100);
        Assert.Equal(20_000, window.TotalSamples);
        Assert.Equal(15_000, window.AggregatedSamples.Sum(x => x.Received));
        var stats = Assert.Single(await reopened.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(19_999)));
        Assert.Equal(20_000, stats.Sent);
        Assert.Equal(25, stats.LossPercent);
        var detailed = await reopened.LoadGraphWindowAsync(_id, 0, At.AddSeconds(19_980), At.AddSeconds(19_999), 100);
        Assert.False(detailed.IsAggregated);
        Assert.Equal(20, detailed.RawSamples.Count);
        Assert.Empty(detailed.AggregatedSamples);
        Assert.Equal(At.AddSeconds(19_980), detailed.RawSamples[0].Timestamp);
        Assert.Equal(20_000, (await reopened.LoadTimeRangeAsync(_id))!.Value.Last.Subtract(At).TotalSeconds + 1);
    }

    [Fact]
    public async Task LargeHistoricalReadsAndMonitoringWritesCanRunTogether()
    {
        await SeedAsync(Enumerable.Range(0, 100_000).Select(i =>
            (0, (string?)"dest", (double?)10, ProbeStatus.Success)));
        var selection = Task.Run(() => _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(99_999)));
        var graph = Task.Run(() => _store.LoadGraphWindowAsync(_id, 0, At, At.AddSeconds(99_999), 200));
        var writes = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var at = At.AddSeconds(200_000 + i);
                var destination = new HopSnapshot(0, "dest", null, 20, 20, 20, 20, null,
                    1, 1, 0, true, [], ProbeStatus.Success);
                await _store.AppendAsync(new(_id, "dest", "dest", at, 100_000 + i,
                    true, [], destination, TimeSpan.Zero), default);
            }
        });
        await writes.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100_000, Assert.Single(await selection).Sent);
        var window = await graph;
        Assert.Equal(100_000, window.TotalSamples);
        Assert.InRange(window.AggregatedSamples.Count, 1, 200);
        Assert.Empty(window.RawSamples);
        Assert.Equal(20, (await _store.LoadSamplesForHopAsync(_id, 0, At.AddSeconds(200_000), At.AddSeconds(200_020))).Count);
    }

    [Fact]
    public async Task InvalidRangesAndCancellationAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.LoadGraphWindowAsync(_id, 0, At, At, 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.LoadSelectionStatisticsAsync(_id, At, At, cancellation.Token));
    }

    private async Task SeedAsync(IEnumerable<(int Hop, string? Address, double? Latency, ProbeStatus Status)> rows)
    {
        await _store.InitializeProfessionalAsync();
        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status) VALUES($session,$round,$hop,$address,$destination,$time,$latency,$status)";
        command.Parameters.AddWithValue("$session", _id.ToString());
        foreach (var name in new[] { "$round", "$hop", "$address", "$destination", "$time", "$latency", "$status" }) command.Parameters.AddWithValue(name, DBNull.Value);
        var round = 0;
        foreach (var row in rows)
        {
            command.Parameters["$round"].Value = round;
            command.Parameters["$hop"].Value = row.Hop;
            command.Parameters["$address"].Value = (object?)row.Address ?? DBNull.Value;
            command.Parameters["$destination"].Value = row.Hop == 0 ? 1 : 0;
            command.Parameters["$time"].Value = At.AddSeconds(round++).ToString("O");
            command.Parameters["$latency"].Value = (object?)row.Latency ?? DBNull.Value;
            command.Parameters["$status"].Value = (int)row.Status;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
