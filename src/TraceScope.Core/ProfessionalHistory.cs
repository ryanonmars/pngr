using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TraceScope.Core;

public sealed record AggregatedSample(DateTimeOffset Timestamp, double? AverageMs, double? MinimumMs,
    double? MaximumMs, double LossPercent, double? JitterMs, long Count, string? Address)
{
    public long Errors { get; init; }
    public long Received { get; init; }
}

public sealed partial class HistoryStore
{
    private bool _professionalInitialized;

    private async Task EnsureProfessionalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_professionalInitialized) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS monitoring_events (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp TEXT NOT NULL,
                kind TEXT NOT NULL, message TEXT NOT NULL, alert_rule_id TEXT NULL);
            CREATE INDEX IF NOT EXISTS monitoring_events_session_time ON monitoring_events(session_id,timestamp);
            CREATE INDEX IF NOT EXISTS monitoring_events_time ON monitoring_events(timestamp);
            CREATE TABLE IF NOT EXISTS event_acknowledgments (
                event_id TEXT PRIMARY KEY, acknowledged_at TEXT NOT NULL, note TEXT NULL);
            CREATE INDEX IF NOT EXISTS samples_session_hop_time ON samples(session_id,hop,timestamp);
            CREATE INDEX IF NOT EXISTS samples_time ON samples(timestamp);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _professionalInitialized = true;
    }

    public async Task InitializeProfessionalAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendEventAsync(MonitoringEvent item, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO monitoring_events(id,session_id,timestamp,kind,message,alert_rule_id) VALUES($id,$session,$at,$kind,$message,$rule)";
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$session", item.SessionId.ToString());
            command.Parameters.AddWithValue("$at", item.Timestamp.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$message", item.Message);
            command.Parameters.AddWithValue("$rule", (object?)item.AlertRuleId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MonitoringEvent>> LoadEventsAsync(Guid sessionId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT e.id,e.session_id,e.timestamp,e.kind,e.message,e.alert_rule_id,a.acknowledged_at,a.note FROM monitoring_events e LEFT JOIN event_acknowledgments a ON a.event_id=e.id WHERE e.session_id=$id AND e.timestamp >= $from AND e.timestamp <= $to ORDER BY e.timestamp LIMIT 10000";
            AddRange(command, sessionId, from, to);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<MonitoringEvent>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5))
                { AcknowledgedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    AcknowledgmentNote = reader.IsDBNull(7) ? null : reader.GetString(7) });
            return result;
        }
        finally { _gate.Release(); }
    }

    private static void AddRange(SqliteCommand command, Guid sessionId, DateTimeOffset? from, DateTimeOffset? to)
    {
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$from", (from ?? DateTimeOffset.MinValue).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", (to ?? DateTimeOffset.MaxValue).ToUniversalTime().ToString("O"));
    }

    public async Task<IReadOnlyList<StoredSample>> LoadSamplesForHopAsync(Guid sessionId, int hop,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 100_000,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id,round,hop,address,is_destination,timestamp,latency_ms,status FROM samples WHERE session_id=$id AND hop=$hop AND timestamp >= $from AND timestamp <= $to ORDER BY timestamp LIMIT $limit";
            AddRange(command, sessionId, from, to);
            command.Parameters.AddWithValue("$hop", hop);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<StoredSample>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0, DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetDouble(6), (ProbeStatus)reader.GetInt32(7)));
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Aggregates in SQLite rather than materializing a long monitoring session in memory.
    /// Count and loss exclude local errors, which are exposed separately. Jitter pairs do not bridge lost probes.</summary>
    public async Task<IReadOnlyList<AggregatedSample>> LoadAggregatedSamplesAsync(Guid sessionId, int hop,
        DateTimeOffset from, DateTimeOffset to, int maxPoints = 1200, CancellationToken cancellationToken = default)
    {
        if (maxPoints is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to));
        var bucketSeconds = Math.Max(0.001, ((to - from).TotalSeconds + 0.001) / maxPoints);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH filtered AS (
                  SELECT *, CASE WHEN status IN (0,2) AND latency_ms >= 0 THEN latency_ms ELSE NULL END AS response,
                    MIN($maxBucket,MAX(0,CAST((unixepoch(timestamp,'subsec')-$origin)/$bucket AS INTEGER))) AS bucket
                  FROM samples WHERE session_id=$id AND hop=$hop AND timestamp >= $from AND timestamp <= $to
                ), sequenced AS (
                  SELECT *,LAG(response) OVER (ORDER BY timestamp,id) AS previous_response FROM filtered
                )
                SELECT MIN(timestamp),AVG(response),MIN(response),MAX(response),
                  SUM(CASE WHEN status<>4 THEN 1 ELSE 0 END) AS sent,COUNT(response) AS received,
                  AVG(CASE WHEN response IS NOT NULL AND previous_response IS NOT NULL THEN ABS(response-previous_response) END),
                  MAX(address),SUM(CASE WHEN status=4 THEN 1 ELSE 0 END)
                FROM sequenced GROUP BY bucket ORDER BY bucket
                """;
            AddRange(command, sessionId, from, to);
            command.Parameters.AddWithValue("$hop", hop);
            command.Parameters.AddWithValue("$origin", (from - DateTimeOffset.UnixEpoch).TotalSeconds);
            command.Parameters.AddWithValue("$bucket", bucketSeconds);
            command.Parameters.AddWithValue("$maxBucket", maxPoints - 1);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<AggregatedSample>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var sent = reader.GetInt64(4);
                var received = reader.GetInt64(5);
                result.Add(new(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? null : reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), sent == 0 ? 0 : 100.0 * (sent - received) / sent,
                    reader.IsDBNull(6) ? received == 0 ? null : 0 : reader.GetDouble(6), sent,
                    reader.IsDBNull(7) ? null : reader.GetString(7)) { Received = received, Errors = reader.GetInt64(8) });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> RetainAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention < TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(retention));
        var cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToString("O");
        var deleted = 0;
        // Release the semaphore between batches so monitoring writes can continue during cleanup.
        bool more;
        do
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await EnsureProfessionalAsync(connection, cancellationToken);
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM samples WHERE id IN (SELECT id FROM samples WHERE timestamp < $cutoff LIMIT 5000)";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                var count = await command.ExecuteNonQueryAsync(cancellationToken);
                deleted += count;
                more = count == 5000;
                command.CommandText = "DELETE FROM monitoring_events WHERE id IN (SELECT id FROM monitoring_events WHERE timestamp < $cutoff LIMIT 5000)";
                more |= await command.ExecuteNonQueryAsync(cancellationToken) == 5000;
                command.CommandText = "DELETE FROM event_acknowledgments WHERE event_id NOT IN (SELECT id FROM monitoring_events)";
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            finally { _gate.Release(); }
            await Task.Yield();
        } while (more);
        return deleted;
    }
}
