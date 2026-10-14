namespace TraceScope.Core;

/// <summary>Per-session allocation in dispatch order: independent endpoint first, then ascending TTL waves.
/// Unscheduled probes consume no ports.
/// Wraparound changes the port only: outstanding probes are matched by their independent source-port identities.</summary>
internal sealed class UdpPortSequence
{
    private long _sequence;
    public int Next(ProbeOptions options)
    {
        if (options.UdpPortMode == UdpPortMode.Fixed) return options.DestinationPort;
        var width = options.UdpEndPort - options.DestinationPort + 1;
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(options.UdpEndPort));
        var position = Interlocked.Increment(ref _sequence) - 1;
        return options.DestinationPort + (int)((ulong)position % (uint)width);
    }
}
