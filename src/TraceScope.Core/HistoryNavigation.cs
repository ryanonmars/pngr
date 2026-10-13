using System.Globalization;

namespace TraceScope.Core;

public sealed partial class HistoryStore
{
    public async Task<(DateTimeOffset First, DateTimeOffset Last)?> LoadTimeRangeAsync(Guid sessionId,
        int? hop = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(timestamp),MAX(timestamp) FROM samples WHERE session_id=$id AND ($hop IS NULL OR hop=$hop)";
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            command.Parameters.AddWithValue("$hop", (object?)hop ?? DBNull.Value);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;
            return (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        }
        finally { _gate.Release(); }
    }
}
