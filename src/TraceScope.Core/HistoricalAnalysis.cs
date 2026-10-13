using Microsoft.Data.Sqlite;

namespace TraceScope.Core;

/// <summary>A bounded graph window. Aggregated samples are explicitly distinct from raw measurements.</summary>
public sealed record HistoricalGraphWindow(IReadOnlyList<StoredSample> RawSamples,
    IReadOnlyList<AggregatedSample> AggregatedSamples, bool IsAggregated, long TotalSamples);

public sealed partial class HistoryStore
{
    // Prepare the existing schema under the writer gate, then release it before any
    // historical scan. WAL readers use independent snapshots and do not block probes.
    private async Task<SqliteConnection> OpenHistoryReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized || !_professionalInitialized)
            {
                await using var setup = await OpenAsync(cancellationToken);
                await EnsureProfessionalAsync(setup, cancellationToken);
            }
        }
        finally { _gate.Release(); }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Exact statistics for stored probes, with separate rows for changing hop addresses.
    /// No missing probes are inferred, and local errors are excluded from packet loss.</summary>
    public async Task<IReadOnlyList<HopSnapshot>> LoadSelectionStatisticsAsync(Guid sessionId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to));
        await using var connection = await OpenHistoryReadAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH filtered AS (
              SELECT *,CASE WHEN status IN (0,2) AND latency_ms >= 0 THEN latency_ms END AS response
              FROM samples WHERE session_id=$id AND timestamp >= $from AND timestamp <= $to
            ), sequenced AS (
              SELECT *,LAG(response) OVER (PARTITION BY hop ORDER BY timestamp,id) AS previous_response,
                LAG(address) OVER (PARTITION BY hop ORDER BY timestamp,id) AS previous_address,
                ROW_NUMBER() OVER (PARTITION BY hop,address ORDER BY timestamp DESC,id DESC) AS last_row
              FROM filtered
            )
            SELECT hop,address,MAX(is_destination),
              SUM(CASE WHEN status<>4 THEN 1 ELSE 0 END),COUNT(response),
              AVG(response),MIN(response),MAX(response),
              AVG(CASE WHEN address IS previous_address AND response IS NOT NULL AND previous_response IS NOT NULL
                THEN ABS(response-previous_response) END),
              MAX(CASE WHEN last_row=1 THEN response END),MAX(CASE WHEN last_row=1 THEN status END)
            FROM sequenced GROUP BY hop,address ORDER BY hop,address
            """;
        AddRange(command, sessionId, from, to);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HopSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var sent = reader.GetInt64(3);
            var received = reader.GetInt64(4);
            double? Value(int index) => reader.IsDBNull(index) ? null : reader.GetDouble(index);
            result.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), null,
                Value(9), Value(5), Value(6), Value(7), Value(8), sent, received,
                sent == 0 ? 0 : 100d * (sent - received) / sent, reader.GetInt32(2) != 0,
                Array.Empty<LatencySample>(), (ProbeStatus)reader.GetInt32(10)));
        }
        return result;
    }

    public async Task<HistoricalGraphWindow> LoadGraphWindowAsync(Guid sessionId, int hop,
        DateTimeOffset from, DateTimeOffset to, int maxPoints = 1600, CancellationToken cancellationToken = default)
    {
        if (maxPoints is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to));
        var raw = await LoadSamplesForHopAsync(sessionId, hop, from, to, maxPoints + 1, cancellationToken);
        if (raw.Count <= maxPoints) return new(raw, Array.Empty<AggregatedSample>(), false, raw.Count);
        var aggregated = await LoadAggregatedSamplesAsync(sessionId, hop, from, to, maxPoints, cancellationToken);
        return new(Array.Empty<StoredSample>(), aggregated, true, aggregated.Sum(x => x.Count + x.Errors));
    }
}
