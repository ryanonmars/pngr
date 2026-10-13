namespace TraceScope.Core;

public sealed class HopStatistics
{
    private readonly Queue<LatencySample> _history = new();
    private double _sum, _min = double.PositiveInfinity, _max, _jitterSum;
    private double? _previous;
    private long _jitterCount;
    private LatencySample? _last;
    public long Sent { get; private set; }
    public long Received { get; private set; }

    public void ResetCounters()
    {
        Sent = Received = _jitterCount = 0;
        _sum = _max = _jitterSum = 0;
        _min = double.PositiveInfinity;
        _previous = null;
        _last = null;
    }

    public void Add(DateTimeOffset timestamp, ProbeResult result, int historyLimit)
    {
        if (result.Status != ProbeStatus.Error) Sent++;
        var latency = result.Responded ? result.LatencyMs : null;
        if (latency is { } ms)
        {
            Received++; _sum += ms; _min = Math.Min(_min, ms); _max = Math.Max(_max, ms);
            if (_previous is { } previous) { _jitterSum += Math.Abs(ms - previous); _jitterCount++; }
            _previous = ms;
        }
        _last = new LatencySample(timestamp, latency, result.Status);
        _history.Enqueue(_last);
        while (_history.Count > historyLimit) _history.Dequeue();
    }

    public HopSnapshot Snapshot(int hop, string? address, string? hostname, bool isDestination) => new(
        hop, address, hostname, _last?.LatencyMs,
        Received == 0 ? null : _sum / Received, Received == 0 ? null : _min,
        Received == 0 ? null : _max, _jitterCount == 0 ? null : _jitterSum / _jitterCount,
        Sent, Received, Sent == 0 ? 0 : 100d * (Sent - Received) / Sent,
        isDestination, _history.ToArray(), _last?.Status ?? ProbeStatus.Timeout);
}
