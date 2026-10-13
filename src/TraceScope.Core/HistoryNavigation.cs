using System.Globalization;

namespace TraceScope.Core;

public sealed partial class HistoryStore
{
    public async Task<(DateTimeOffset First, DateTimeOffset Last)?> LoadTimeRangeAsync(Guid sessionId,
        int? hop = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenHistoryReadAsync(cancellationToken);
        using var command = connection.CreateCommand();
        var filter = hop.HasValue ? "session_id=$id AND hop=$hop" : "session_id=$id";
        command.CommandText = $"SELECT (SELECT timestamp FROM samples WHERE {filter} ORDER BY timestamp LIMIT 1),(SELECT timestamp FROM samples WHERE {filter} ORDER BY timestamp DESC LIMIT 1)";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$hop", (object?)hop ?? DBNull.Value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;
        return (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }
}
