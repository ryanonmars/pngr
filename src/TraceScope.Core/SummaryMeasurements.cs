namespace TraceScope.Core;

public sealed record SummaryBucket(DateTimeOffset Timestamp, double? AverageMs, double LossPercent, long Sent, long Errors);

public static class SummaryMeasurements
{
    /// <summary>A bounded series on the same wall-clock range for every monitored target.</summary>
    public static IReadOnlyList<SummaryBucket> Aggregate(IReadOnlyList<LatencySample> samples,
        DateTimeOffset end, TimeSpan window, int maximumPoints = 300)
    {
        if (window <= TimeSpan.Zero || maximumPoints is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        var first = end - window;
        var interval = Math.Max(1, (long)Math.Ceiling(window.Ticks / (double)maximumPoints));
        var buckets = new SortedDictionary<long, Accumulator>();
        foreach (var sample in samples)
        {
            if (sample.Timestamp < first || sample.Timestamp > end) continue;
            var index = Math.Min(maximumPoints - 1, (sample.Timestamp - first).Ticks / interval);
            if (!buckets.TryGetValue(index, out var value)) buckets[index] = value = new();
            if (sample.Status == ProbeStatus.Error) { value.Errors++; continue; }
            value.Sent++;
            if (sample.Status is ProbeStatus.Success or ProbeStatus.TimeExceeded
                && sample.LatencyMs is >= 0 && double.IsFinite(sample.LatencyMs.Value))
            { value.Received++; value.Latency += sample.LatencyMs.Value; }
        }
        return buckets.Select(pair => new SummaryBucket(first.AddTicks(Math.Min(window.Ticks, pair.Key * interval + interval / 2)),
            pair.Value.Received > 0 ? pair.Value.Latency / pair.Value.Received : null,
            pair.Value.Sent > 0 ? 100d * (pair.Value.Sent - pair.Value.Received) / pair.Value.Sent : 0,
            pair.Value.Sent, pair.Value.Errors)).ToArray();
    }

    private sealed class Accumulator
    { public long Sent; public long Received; public long Errors; public double Latency; }
}
