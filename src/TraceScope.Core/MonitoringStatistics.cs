namespace TraceScope.Core;

public sealed record MeasurementSummary(long Sent, long Received, double LossPercent,
    double? AverageMs, double? MinimumMs, double? MaximumMs, double? JitterMs, double? Mos)
{
    public long Errors { get; init; }
    public int AggregatedPoints { get; init; }
}

public static class MonitoringStatistics
{
    public static bool IsResponse(LatencySample sample) => sample.LatencyMs is >= 0 and < double.PositiveInfinity
        && sample.Status is ProbeStatus.Success or ProbeStatus.TimeExceeded;

    public static MeasurementSummary Calculate(IReadOnlyList<LatencySample> samples, TimeSpan window,
        DateTimeOffset now, TimeSpan? aggregation = null)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (aggregation is { } step && step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(aggregation));
        var start = now - window;
        var selected = samples.Where(s => s.Timestamp >= start && s.Timestamp <= now).OrderBy(s => s.Timestamp).ToArray();
        var measured = selected.Where(s => s.Status != ProbeStatus.Error).ToArray();
        var values = measured.Where(IsResponse).Select(s => s.LatencyMs!.Value).ToArray();
        // Jitter is mean absolute difference between consecutive responding probes; loss breaks a pair.
        double variation = 0;
        int pairs = 0;
        for (var i = 1; i < selected.Length; i++)
            if (IsResponse(selected[i - 1]) && IsResponse(selected[i]))
            { variation += Math.Abs(selected[i].LatencyMs!.Value - selected[i - 1].LatencyMs!.Value); pairs++; }
        var loss = measured.Length == 0 ? 0 : 100.0 * (measured.Length - values.Length) / measured.Length;
        double? average = values.Length == 0 ? null : values.Average();
        double? jitter = values.Length == 0 ? null : pairs == 0 ? 0 : variation / pairs;
        var pointCount = aggregation is null ? selected.Length : selected
            .Select(s => (s.Timestamp.UtcTicks - start.UtcTicks) / aggregation.Value.Ticks).Distinct().Count();
        return new(measured.Length, values.Length, loss, average,
            values.Length == 0 ? null : values.Min(), values.Length == 0 ? null : values.Max(), jitter,
            average is null ? null : EstimateMos(average.Value, jitter ?? 0, loss))
        { Errors = selected.LongCount(s => s.Status == ProbeStatus.Error), AggregatedPoints = pointCount };
    }

    /// <summary>Approximate G.711 conversational quality, not a voice measurement. Assumes symmetric RTT/2,
    /// 20 ms terminal delay, jitter buffering of twice observed jitter and independent loss with
    /// G.711 Appendix I concealment (Bpl=25.1, ITU-T G.113 Table I.4). This is a planning approximation.
    /// R-to-MOS polynomial: ITU-T G.107 Annex B, https://www.itu.int/rec/T-REC-G.107.</summary>
    public static double EstimateMos(double roundTripMs, double jitterMs, double lossPercent)
    {
        if (!double.IsFinite(roundTripMs) || !double.IsFinite(jitterMs) || !double.IsFinite(lossPercent))
            throw new ArgumentOutOfRangeException(nameof(roundTripMs));
        var delay = Math.Max(0, roundTripMs) / 2 + 20 + 2 * Math.Max(0, jitterMs);
        var delayImpairment = 0.024 * delay + 0.11 * Math.Max(0, delay - 177.3);
        var loss = Math.Clamp(lossPercent, 0, 100);
        var lossImpairment = 95 * loss / (loss + 25.1);
        var r = Math.Clamp(93.2 - delayImpairment - lossImpairment, 0, 100);
        return Math.Clamp(1 + 0.035 * r + r * (r - 60) * (100 - r) * 7e-6, 1, 4.5);
    }
}
