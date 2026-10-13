using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TraceScope.Core;

public sealed partial class HistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public HistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }
    public string DatabasePath { get; }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!_initialized)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY, target TEXT NOT NULL, address TEXT NOT NULL,
                    started_at TEXT NOT NULL, stopped_at TEXT NULL);
                CREATE TABLE IF NOT EXISTS samples (
                    id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, round INTEGER NOT NULL,
                    hop INTEGER NOT NULL, address TEXT NULL, is_destination INTEGER NOT NULL,
                    timestamp TEXT NOT NULL, latency_ms REAL NULL, status INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS samples_session_time ON samples(session_id, timestamp);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        return connection;
    }

    internal async Task AddSessionAsync(TargetSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO sessions(id,target,address,started_at) VALUES($id,$target,$address,$at)";
            command.Parameters.AddWithValue("$id", session.Id.ToString());
            command.Parameters.AddWithValue("$target", session.Target);
            command.Parameters.AddWithValue("$address", session.ResolvedAddress);
            command.Parameters.AddWithValue("$at", session.StartedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task StopSessionAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET stopped_at=$at WHERE id=$id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    internal async Task AppendAsync(RoundSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status)
                VALUES($session,$round,$hop,$address,$destination,$at,$latency,$status)
                """;
            command.Parameters.AddWithValue("$session", snapshot.SessionId.ToString());
            command.Parameters.AddWithValue("$round", snapshot.Round);
            command.Parameters.AddWithValue("$at", snapshot.Timestamp.ToString("O"));
            var hopParameter = command.Parameters.Add("$hop", SqliteType.Integer);
            var addressParameter = command.Parameters.Add("$address", SqliteType.Text);
            var destinationParameter = command.Parameters.Add("$destination", SqliteType.Integer);
            var latencyParameter = command.Parameters.Add("$latency", SqliteType.Real);
            var statusParameter = command.Parameters.Add("$status", SqliteType.Integer);
            foreach (var hop in snapshot.Hops.Append(snapshot.Destination))
            {
                hopParameter.Value = hop.Hop;
                addressParameter.Value = (object?)hop.Address ?? DBNull.Value;
                destinationParameter.Value = hop.IsDestination ? 1 : 0;
                latencyParameter.Value = (object?)hop.LastMs ?? DBNull.Value;
                statusParameter.Value = (int)hop.Status;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<StoredSession>> LoadSessionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.id,s.target,s.address,s.started_at,s.stopped_at,COUNT(p.id)
                FROM sessions s LEFT JOIN samples p ON p.session_id=s.id
                GROUP BY s.id ORDER BY s.started_at DESC
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<StoredSession>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(new StoredSession(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetInt64(5)));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<StoredSample>> LoadSamplesAsync(Guid sessionId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 100_000,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id,round,hop,address,is_destination,timestamp,latency_ms,status
                FROM samples WHERE session_id=$id AND timestamp >= $from AND timestamp <= $to
                ORDER BY timestamp,hop LIMIT $limit
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            command.Parameters.AddWithValue("$from", (from ?? DateTimeOffset.MinValue).ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$to", (to ?? DateTimeOffset.MaxValue).ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<StoredSample>();
            while (await reader.ReadAsync(cancellationToken)) result.Add(new StoredSample(
                Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4) != 0,
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetDouble(6), (ProbeStatus)reader.GetInt32(7)));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ExportCsvAsync(Guid sessionId, string filePath, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(filePath, false, new System.Text.UTF8Encoding(true));
        await ExportCsvAsync(sessionId, writer, cancellationToken);
    }

    public async Task ExportCsvAsync(Guid sessionId, TextWriter writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id,round,timestamp,hop,address,is_destination,latency_ms,status
                FROM samples WHERE session_id=$id ORDER BY timestamp,hop
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await writer.WriteLineAsync("Session,Round,Timestamp,Hop,Address,Destination,LatencyMs,Status");
            while (await reader.ReadAsync(cancellationToken))
            {
                await writer.WriteLineAsync(string.Join(',', reader.GetString(0),
                    reader.GetInt64(1).ToString(CultureInfo.InvariantCulture), reader.GetString(2),
                    reader.GetInt32(3).ToString(CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetInt32(5) == 0 ? "0" : "1",
                    reader.IsDBNull(6) ? "" : reader.GetDouble(6).ToString("0.###", CultureInfo.InvariantCulture),
                    (ProbeStatus)reader.GetInt32(7)));
            }
            await writer.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
