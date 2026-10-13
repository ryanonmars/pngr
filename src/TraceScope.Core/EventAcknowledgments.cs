using Microsoft.Data.Sqlite;

namespace TraceScope.Core;

public sealed partial class HistoryStore
{
    public async Task<bool> AcknowledgeEventAsync(Guid eventId, string? note = null, CancellationToken cancellationToken = default)
    {
        if (note?.Length > 1024) throw new ArgumentOutOfRangeException(nameof(note));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureProfessionalAsync(connection, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO event_acknowledgments(event_id,acknowledged_at,note)
                SELECT id,$at,$note FROM monitoring_events WHERE id=$id
                ON CONFLICT(event_id) DO UPDATE SET acknowledged_at=excluded.acknowledged_at,note=excluded.note
                """;
            command.Parameters.AddWithValue("$id", eventId.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _gate.Release(); }
    }

    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
