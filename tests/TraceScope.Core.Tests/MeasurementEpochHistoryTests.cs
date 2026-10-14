using Microsoft.Data.Sqlite;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class MeasurementEpochHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TraceScope-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();
    private readonly HistoryStore _store;
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    public MeasurementEpochHistoryTests() => _store = new(Path.Combine(_directory, "history.db"));
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task AggregatedJitterRemainsUnknownWithoutAnyConsecutiveResponsePair()
    {
        await AppendAsync(0, 0, 10, ProbeStatus.Success);
        await AppendAsync(1, 0, null, ProbeStatus.Timeout);
        var bucket = Assert.Single(await _store.LoadAggregatedSamplesAsync(_id, 0, At, At.AddSeconds(1), 1));
        Assert.Equal(2, bucket.Count); Assert.Equal(50, bucket.LossPercent);
        Assert.Null(bucket.JitterMs);
    }

    [Fact]
    public async Task SelectedStatisticsSeparateConfigurationEpochsAndPreserveProbeTimestamps()
    {
        await AppendAsync(0, 0, 10, ProbeStatus.Success);
        await AppendAsync(1, 0, null, ProbeStatus.Timeout);
        await AppendAsync(2, 1, 100, ProbeStatus.Success);
        await AppendAsync(3, 1, 120, ProbeStatus.Success);
        var rows = await _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(3));
        Assert.Equal(2, rows.Count);
        var first = Assert.Single(rows, x => x.MeasurementEpoch == 0);
        Assert.Equal(2, first.Sent); Assert.Equal(1, first.Received);
        Assert.Equal(50, first.LossPercent); Assert.Equal(10, first.AverageMs);
        Assert.Null(first.LastMs); Assert.Null(first.JitterMs);
        var second = Assert.Single(rows, x => x.MeasurementEpoch == 1);
        Assert.Equal(2, second.Sent); Assert.Equal(0, second.LossPercent);
        Assert.Equal(110, second.AverageMs); Assert.Equal(20, second.JitterMs);
        var samples = await _store.LoadSamplesAsync(_id);
        Assert.Equal(new[] { At, At.AddSeconds(1), At.AddSeconds(2), At.AddSeconds(3) }, samples.Select(s => s.Timestamp));
        Assert.Equal(new[] { 0, 0, 1, 1 }, samples.Select(s => s.MeasurementEpoch));
        Assert.Equal(samples, await _store.LoadSamplesForHopAsync(_id, 0));
        var narrow = Assert.Single(await _store.LoadSelectionStatisticsAsync(_id, At.AddSeconds(1), At.AddSeconds(1)));
        Assert.Equal(100, narrow.LossPercent);
        using var export = new StringWriter();
        await _store.ExportCsvAsync(_id, export);
        Assert.Contains("Transmission,MeasurementEpoch", export.ToString());
    }

    [Fact]
    public async Task ProbeAuditPreservesActualProbesBeyondDisplayedRouteAndConfiguration()
    {
        var range = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, UdpPortMode = UdpPortMode.Range, UdpEndPort = 33625 };
        var sent = new ProbeResult("1.1.1.1", 18, ProbeStatus.Success)
            { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.UdpPortUnreachable, MeasuredAt = At };
        var attempts = new[] { new ProbeMeasurement(8, At, sent, range with { DestinationPort = 33434 }),
            new ProbeMeasurement(9, At.AddMilliseconds(1), sent, range with { DestinationPort = 33435 }),
            new ProbeMeasurement(255, At.AddMilliseconds(2), sent, range with { DestinationPort = 33436 }) };
        var destination = Hop(0, 2, At, sent);
        await _store.AppendAsync(new(_id, "1.1.1.1", "1.1.1.1", At.AddSeconds(4), 1, true,
            [Hop(8, 2, At, sent)], destination, TimeSpan.FromSeconds(4))
            { MeasurementEpoch = 2, MeasurementOptions = range, Measurements = attempts }, default);
        var reopened = new HistoryStore(_store.DatabasePath);
        Assert.Equal(attempts, await reopened.LoadProbeMeasurementsAsync(_id, 1));
        Assert.Empty(await reopened.LoadProbeMeasurementsAsync(_id, 2));
        Assert.Equal(2, (await reopened.LoadSamplesAsync(_id)).Count);
        Assert.Equal(range, (await reopened.LoadMeasurementOptionsAsync(_id))[2]);
        Assert.Single(await reopened.LoadProbeMeasurementsAsync(_id, limit: 1));
    }

    [Fact]
    public async Task AggregationStaysBoundedAndMarksMixedConfigurationBuckets()
    {
        for (var i = 0; i < 20; i++) await AppendAsync(i, i / 2, i, ProbeStatus.Success);
        var buckets = await _store.LoadAggregatedSamplesAsync(_id, 0, At, At.AddSeconds(19), 1);
        var bucket = Assert.Single(buckets);
        Assert.Equal(20, bucket.Count); Assert.Equal(20, bucket.Received);
        Assert.Equal(-1, bucket.MeasurementEpoch);
        Assert.Equal(1, bucket.JitterMs); // Adjacent values within an epoch; epoch boundaries excluded.
        Assert.Equal(10, (await _store.LoadSelectionStatisticsAsync(_id, At, At.AddSeconds(19))).Count);
    }

    [Fact]
    public async Task LegacySchemaMigratesWithoutChangingMeasurementsOrInventingConfiguration()
    {
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE samples(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL,round INTEGER NOT NULL,hop INTEGER NOT NULL,address TEXT NULL,is_destination INTEGER NOT NULL,timestamp TEXT NOT NULL,latency_ms REAL NULL,status INTEGER NOT NULL); INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status) VALUES($id,1,0,'1.1.1.1',1,$at,18,0)";
            command.Parameters.AddWithValue("$id", _id.ToString()); command.Parameters.AddWithValue("$at", At.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var row = Assert.Single(await _store.LoadSamplesAsync(_id));
        Assert.Equal(At, row.Timestamp); Assert.Equal(18, row.LatencyMs); Assert.Equal(0, row.MeasurementEpoch);
        Assert.Empty(await _store.LoadMeasurementOptionsAsync(_id)); Assert.Empty(await _store.LoadProbeMeasurementsAsync(_id));
    }

    [Fact]
    public async Task RetentionRemovesExpiredAuditAndOrphanConfigurationsTogether()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var result = new ProbeResult(null, null, ProbeStatus.Timeout) { MeasuredAt = old };
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp };
        await _store.AppendAsync(new(_id, "1.1.1.1", "1.1.1.1", old, 1, false, [],
            Hop(0, 4, old, result), TimeSpan.FromSeconds(1))
            { MeasurementEpoch = 4, MeasurementOptions = options, Measurements = [new(255, old, result, options)] }, default);
        Assert.Equal(1, await _store.RetainAsync(TimeSpan.FromDays(1)));
        Assert.Empty(await _store.LoadSamplesAsync(_id));
        Assert.Empty(await _store.LoadProbeMeasurementsAsync(_id));
        Assert.Empty(await _store.LoadMeasurementOptionsAsync(_id));
    }

    [Fact]
    public async Task InterruptedRoundStoresAuditWithoutInventingSamplesAndHonorsRetention()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33550 };
        var result = new ProbeResult(null, null, ProbeStatus.Error, "Probe canceled by monitoring stop")
            { Transmission = ProbeTransmission.Unknown, MeasuredAt = old };
        var measurement = new ProbeMeasurement(8, old, result, options) { Canceled = true };
        await _store.AppendProbeAuditAsync(_id, 7, 2, options, [measurement]);
        Assert.Equal(measurement, Assert.Single(await _store.LoadProbeMeasurementsAsync(_id, 7)));
        Assert.Equal(options, (await _store.LoadMeasurementOptionsAsync(_id))[2]);
        Assert.Empty(await _store.LoadSamplesAsync(_id));
        Assert.Empty(await _store.LoadSelectionStatisticsAsync(_id, old, old));
        Assert.Equal(0, await _store.RetainAsync(TimeSpan.FromDays(1)));
        Assert.Empty(await _store.LoadProbeMeasurementsAsync(_id));
        Assert.Empty(await _store.LoadMeasurementOptionsAsync(_id));
    }

    [Fact]
    public async Task CompactPortOverridesRoundTripWrappingAndPreserveExceptionalFullOptions()
    {
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434,
            UdpPortMode = UdpPortMode.Range, UdpEndPort = 33625, SourceAddress = "192.0.2.3", InterfaceId = "test-interface" };
        var result = new ProbeResult(null, null, ProbeStatus.Timeout);
        var ports = new[] { 33625, 33434, 33435 };
        var measurements = ports.Select((port, i) => new ProbeMeasurement(i + 1, At.AddSeconds(i), result,
            options with { DestinationPort = port, UdpPortMode = UdpPortMode.Fixed })).ToList();
        measurements.Add(new(255, At.AddSeconds(3), result, options with { UdpPacketSize = 120 }));
        await _store.AppendProbeAuditAsync(_id, 1, 0, options, measurements);
        Assert.Equal(measurements, await _store.LoadProbeMeasurementsAsync(_id));
        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT options_json FROM probe_attempts ORDER BY id";
        using (var reader = await command.ExecuteReaderAsync())
        {
            for (var index = 0; index < measurements.Count; index++)
            {
                Assert.True(await reader.ReadAsync());
                var json = reader.GetString(0);
                var full = JsonSerializer.Serialize(measurements[index].Options);
                if (index < ports.Length)
                {
                    Assert.Contains("portOverrides", json);
                    Assert.True(json.Length < full.Length / 4);
                }
                else Assert.Equal(full, json);
            }
        }
        // Previously written full configurations remain readable without an epoch definition.
        command.CommandText = "UPDATE probe_attempts SET options_json=$full; DELETE FROM measurement_configurations";
        command.Parameters.AddWithValue("$full", JsonSerializer.Serialize(options));
        await command.ExecuteNonQueryAsync();
        Assert.All(await _store.LoadProbeMeasurementsAsync(_id), x => Assert.Equal(options, x.Options));
    }

    [Fact]
    public async Task MissingCompactBaseConfigurationIsReportedInsteadOfInvented()
    {
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp };
        await _store.AppendProbeAuditAsync(_id, 1, 0, options,
            [new(8, At, new(null, null, ProbeStatus.Timeout), options)]);
        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM measurement_configurations";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.LoadProbeMeasurementsAsync(_id));
    }

    private Task AppendAsync(int round, int epoch, double? latency, ProbeStatus status)
    {
        var at = At.AddSeconds(round);
        var result = new ProbeResult("1.1.1.1", latency, status) { MeasuredAt = at };
        return _store.AppendAsync(new(_id, "1.1.1.1", "1.1.1.1", at.AddSeconds(10), round, false, [],
            Hop(0, epoch, at, result), TimeSpan.FromSeconds(10))
            { MeasurementEpoch = epoch, MeasurementOptions = new() { Protocol = ProbeProtocol.Udp, DestinationPort = epoch == 0 ? 33434 : 33550 } }, default);
    }
    private static HopSnapshot Hop(int hop, int epoch, DateTimeOffset at, ProbeResult result) => new(hop,
        result.Address, null, result.LatencyMs, result.LatencyMs, result.LatencyMs, result.LatencyMs, null,
        1, result.Responded ? 1 : 0, result.Responded ? 0 : 100, true,
        [new(at, result.LatencyMs, result.Status)], result.Status)
        { MeasurementEpoch = epoch, Transmission = result.Transmission, ResponseKind = result.ResponseKind };
}
