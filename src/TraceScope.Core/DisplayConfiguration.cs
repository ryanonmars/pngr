namespace TraceScope.Core;

public sealed record DisplayConfiguration
{
    public double WarningLatencyMs { get; init; } = 100;
    public double CriticalLatencyMs { get; init; } = 200;
    public double FixedMaximumMs { get; init; }
    public int DefaultTimeRangeMinutes { get; init; } = 10;
    public bool ShowJitter { get; init; } = true;
    public bool ShowPacketLoss { get; init; } = true;
    public bool ShowEventMarkers { get; init; } = true;

    public void Validate()
    {
        if (!double.IsFinite(WarningLatencyMs) || !double.IsFinite(CriticalLatencyMs)
            || WarningLatencyMs < 1 || CriticalLatencyMs <= WarningLatencyMs || CriticalLatencyMs > 60000)
            throw new InvalidDataException("Critical latency must exceed warning latency (1–60000 ms).");
        if (!double.IsFinite(FixedMaximumMs) || FixedMaximumMs < 0 || FixedMaximumMs > 60000
            || FixedMaximumMs is > 0 and < 1)
            throw new InvalidDataException("Graph maximum must be 0 (automatic) or 1–60000 ms.");
        if (!new[] { 1, 5, 10, 15, 60, 360, 1440, 10080 }.Contains(DefaultTimeRangeMinutes))
            throw new InvalidDataException("Unsupported default graph time range.");
    }
}
