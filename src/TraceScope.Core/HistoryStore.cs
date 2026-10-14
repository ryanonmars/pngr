using System.Globalization;
using System.Text.Json;
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
                    timestamp TEXT NOT NULL, latency_ms REAL NULL, status INTEGER NOT NULL,
                    detail TEXT NULL, response_kind INTEGER NOT NULL DEFAULT 0, transmission INTEGER NOT NULL DEFAULT 0, measurement_epoch INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS measurement_configurations (
                    session_id TEXT NOT NULL, epoch INTEGER NOT NULL, options_json TEXT NOT NULL,
                    PRIMARY KEY(session_id,epoch));
                CREATE TABLE IF NOT EXISTS probe_attempts (
                    id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, round INTEGER NOT NULL,
                    measurement_epoch INTEGER NOT NULL DEFAULT 0, hop INTEGER NOT NULL, timestamp TEXT NOT NULL,
                    options_json TEXT NOT NULL, result_json TEXT NOT NULL, canceled INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS probe_attempts_session_round ON probe_attempts(session_id,round);
                CREATE INDEX IF NOT EXISTS probe_attempts_time ON probe_attempts(timestamp);
                CREATE INDEX IF NOT EXISTS samples_session_time ON samples(session_id, timestamp);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            // Add evidence columns without replacing tables or rewriting older measurements.
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            command.CommandText = "PRAGMA table_info(samples)";
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
            foreach (var (name, definition) in new[] { ("detail", "TEXT NULL"),
                ("response_kind", "INTEGER NOT NULL DEFAULT 0"), ("transmission", "INTEGER NOT NULL DEFAULT 0"), ("measurement_epoch", "INTEGER NOT NULL DEFAULT 0") })
            {
                if (columns.Contains(name)) continue;
                command.CommandText = $"ALTER TABLE samples ADD COLUMN {name} {definition}";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            command.CommandText = "PRAGMA table_info(probe_attempts)";
            var hasCanceled = false;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) hasCanceled |= reader.GetString(1) == "canceled";
            if (!hasCanceled)
            {
                command.CommandText = "ALTER TABLE probe_attempts ADD COLUMN canceled INTEGER NOT NULL DEFAULT 0";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            command.CommandText = "CREATE INDEX IF NOT EXISTS samples_session_epoch ON samples(session_id,measurement_epoch); CREATE INDEX IF NOT EXISTS probe_attempts_session_epoch ON probe_attempts(session_id,measurement_epoch)";
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
                INSERT INTO samples(session_id,round,hop,address,is_destination,timestamp,latency_ms,status,detail,response_kind,transmission,measurement_epoch)
                VALUES($session,$round,$hop,$address,$destination,$at,$latency,$status,$detail,$kind,$transmission,$epoch)
                """;
            command.Parameters.AddWithValue("$session", snapshot.SessionId.ToString());
            command.Parameters.AddWithValue("$round", snapshot.Round);
            command.Parameters.AddWithValue("$at", snapshot.Timestamp.ToString("O"));
            var hopParameter = command.Parameters.Add("$hop", SqliteType.Integer);
            var addressParameter = command.Parameters.Add("$address", SqliteType.Text);
            var destinationParameter = command.Parameters.Add("$destination", SqliteType.Integer);
            var latencyParameter = command.Parameters.Add("$latency", SqliteType.Real);
            var statusParameter = command.Parameters.Add("$status", SqliteType.Integer);
            var detailParameter = command.Parameters.Add("$detail", SqliteType.Text);
            var kindParameter = command.Parameters.Add("$kind", SqliteType.Integer);
            var transmissionParameter = command.Parameters.Add("$transmission", SqliteType.Integer);
            var epochParameter = command.Parameters.Add("$epoch", SqliteType.Integer);
            foreach (var hop in snapshot.Hops.Append(snapshot.Destination))
            {
                hopParameter.Value = hop.Hop;
                command.Parameters["$at"].Value = (hop.History.LastOrDefault()?.Timestamp ?? snapshot.Timestamp).ToUniversalTime().ToString("O");
                epochParameter.Value = hop.MeasurementEpoch;
                addressParameter.Value = (object?)hop.Address ?? DBNull.Value;
                destinationParameter.Value = hop.IsDestination ? 1 : 0;
                latencyParameter.Value = (object?)hop.LastMs ?? DBNull.Value;
                statusParameter.Value = (int)hop.Status;
                detailParameter.Value = (object?)hop.Detail ?? DBNull.Value;
                kindParameter.Value = (int)hop.ResponseKind;
                transmissionParameter.Value = (int)hop.Transmission;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await WriteProbeAuditAsync(command, snapshot.SessionId, snapshot.Round, snapshot.MeasurementEpoch,
                snapshot.MeasurementOptions, snapshot.Measurements, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Preserve admitted attempts from an interrupted round without inventing route or loss samples.</summary>
    internal async Task AppendProbeAuditAsync(Guid sessionId, long round, int epoch, ProbeOptions options,
        IReadOnlyList<ProbeMeasurement> measurements)
    {
        if (measurements.Count == 0) return;
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            await WriteProbeAuditAsync(command, sessionId, round, epoch, options, measurements, CancellationToken.None);
            await transaction.CommitAsync();
        }
        finally { _gate.Release(); }
    }

    private static async Task WriteProbeAuditAsync(SqliteCommand command, Guid sessionId, long round, int epoch,
        ProbeOptions? options, IReadOnlyList<ProbeMeasurement> measurements, CancellationToken cancellationToken)
    {
        command.Parameters.Clear();
        if (options is not null)
        {
            command.CommandText = "INSERT OR IGNORE INTO measurement_configurations(session_id,epoch,options_json) VALUES($session,$epoch,$options)";
            command.Parameters.AddWithValue("$session", sessionId.ToString());
            command.Parameters.AddWithValue("$epoch", epoch);
            command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(options));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (measurements.Count == 0) return;
        // Use the immutable stored epoch as the base, including when a caller reuses an epoch.
        command.Parameters.Clear();
        command.CommandText = "SELECT options_json FROM measurement_configurations WHERE session_id=$session AND epoch=$epoch";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        command.Parameters.AddWithValue("$epoch", epoch);
        var storedOptions = await command.ExecuteScalarAsync(cancellationToken) as string;
        var epochOptions = storedOptions is null ? null : JsonSerializer.Deserialize<ProbeOptions>(storedOptions);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO probe_attempts(session_id,round,measurement_epoch,hop,timestamp,options_json,result_json,canceled) VALUES($session,$round,$epoch,$hop,$at,$options,$result,$canceled)";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        command.Parameters.AddWithValue("$round", round);
        command.Parameters.AddWithValue("$epoch", epoch);
        var auditHop = command.Parameters.Add("$hop", SqliteType.Integer);
        var auditAt = command.Parameters.Add("$at", SqliteType.Text);
        var auditOptions = command.Parameters.Add("$options", SqliteType.Text);
        var auditResult = command.Parameters.Add("$result", SqliteType.Text);
        var auditCanceled = command.Parameters.Add("$canceled", SqliteType.Integer);
        foreach (var measurement in measurements)
        {
            auditHop.Value = measurement.Hop;
            auditAt.Value = measurement.Timestamp.ToUniversalTime().ToString("O");
            auditOptions.Value = epochOptions is not null && measurement.Options == epochOptions with
                { DestinationPort = measurement.Options.DestinationPort, UdpPortMode = measurement.Options.UdpPortMode }
                ? JsonSerializer.Serialize(new { portOverrides = true, measurement.Options.DestinationPort, measurement.Options.UdpPortMode })
                : JsonSerializer.Serialize(measurement.Options);
            auditResult.Value = JsonSerializer.Serialize(measurement.Result);
            auditCanceled.Value = measurement.Canceled ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
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
                SELECT session_id,round,hop,address,is_destination,timestamp,latency_ms,status,detail,response_kind,transmission,measurement_epoch
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
                reader.IsDBNull(6) ? null : reader.GetDouble(6), (ProbeStatus)reader.GetInt32(7))
                { Detail = reader.IsDBNull(8) ? null : reader.GetString(8), ResponseKind = (ProbeResponseKind)reader.GetInt32(9),
                    Transmission = (ProbeTransmission)reader.GetInt32(10), MeasurementEpoch = reader.GetInt32(11) });
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stored configurations by measurement epoch. Older histories have no recoverable configuration.</summary>
    public async Task<IReadOnlyDictionary<int, ProbeOptions>> LoadMeasurementOptionsAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenHistoryReadAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT epoch,options_json FROM measurement_configurations WHERE session_id=$id ORDER BY epoch";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<int, ProbeOptions>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetInt32(0), JsonSerializer.Deserialize<ProbeOptions>(reader.GetString(1))
                ?? throw new InvalidDataException("Stored measurement configuration is missing."));
        return result;
    }

    /// <summary>Actual probe attempts, including concurrent responses beyond the displayed terminal.
    /// This audit does not invent additional route rows or affect destination loss denominators.</summary>
    public async Task<IReadOnlyList<ProbeMeasurement>> LoadProbeMeasurementsAsync(Guid sessionId,
        long? round = null, int limit = 100_000, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenHistoryReadAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.hop,p.timestamp,p.result_json,p.options_json,p.canceled,c.options_json FROM probe_attempts p LEFT JOIN measurement_configurations c ON c.session_id=p.session_id AND c.epoch=p.measurement_epoch WHERE p.session_id=$id AND ($round IS NULL OR p.round=$round) ORDER BY p.timestamp,p.id LIMIT $limit";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$round", (object?)round ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProbeMeasurement>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt32(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                JsonSerializer.Deserialize<ProbeResult>(reader.GetString(2))
                    ?? throw new InvalidDataException("Stored probe result is missing."),
                ReadProbeOptions(reader.GetString(3), reader.IsDBNull(5) ? null : reader.GetString(5))) { Canceled = reader.GetInt32(4) != 0 });
        return result;
    }

    private static ProbeOptions ReadProbeOptions(string auditJson, string? epochJson)
    {
        using var document = JsonDocument.Parse(auditJson);
        if (!document.RootElement.TryGetProperty("portOverrides", out _))
            return JsonSerializer.Deserialize<ProbeOptions>(auditJson)
                ?? throw new InvalidDataException("Stored probe configuration is missing.");
        if (epochJson is null)
            throw new InvalidDataException("Stored probe port overrides have no configuration epoch.");
        var options = JsonSerializer.Deserialize<ProbeOptions>(epochJson)
            ?? throw new InvalidDataException("Stored measurement configuration is missing.");
        return options with
        {
            DestinationPort = document.RootElement.GetProperty(nameof(ProbeOptions.DestinationPort)).GetInt32(),
            UdpPortMode = (UdpPortMode)document.RootElement.GetProperty(nameof(ProbeOptions.UdpPortMode)).GetInt32()
        };
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
                SELECT session_id,round,timestamp,hop,address,is_destination,latency_ms,status,detail,response_kind,transmission,measurement_epoch
                FROM samples WHERE session_id=$id ORDER BY timestamp,hop
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await writer.WriteLineAsync("Session,Round,Timestamp,Hop,Address,Destination,LatencyMs,Status,Detail,ResponseKind,Transmission,MeasurementEpoch");
            while (await reader.ReadAsync(cancellationToken))
            {
                await writer.WriteLineAsync(string.Join(',', reader.GetString(0),
                    reader.GetInt64(1).ToString(CultureInfo.InvariantCulture), reader.GetString(2),
                    reader.GetInt32(3).ToString(CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetInt32(5) == 0 ? "0" : "1",
                    reader.IsDBNull(6) ? "" : reader.GetDouble(6).ToString("0.###", CultureInfo.InvariantCulture),
                    (ProbeStatus)reader.GetInt32(7),
                    reader.IsDBNull(8) ? "" : "\"" + reader.GetString(8).Replace("\"", "\"\"") + "\"",
                    (ProbeResponseKind)reader.GetInt32(9), (ProbeTransmission)reader.GetInt32(10), reader.GetInt32(11)));
            }
            await writer.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
