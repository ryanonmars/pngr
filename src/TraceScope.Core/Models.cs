namespace TraceScope.Core;

public enum SessionState { Running, Paused, Stopped, Error }
public enum ProbeStatus { Success, Timeout, TimeExceeded, Unreachable, Error }
public enum ProbeProtocol { Icmp, Tcp, Udp }
public enum UdpPortMode { Fixed, Range }
public enum ProbeResponseKind { None, IcmpEchoReply, IcmpTimeExceeded, UdpReply, UdpPortUnreachable, TcpConnected, TcpReset, NetworkRejection }
public enum ProbeTransmission { Unknown, NotSent, Sent }

public sealed record ProbeOptions
{
    public ProbeProtocol Protocol { get; init; } = ProbeProtocol.Icmp;
    public int DestinationPort { get; init; } = 443;
    public UdpPortMode UdpPortMode { get; init; }
    public int UdpEndPort { get; init; } = 33625;
    // Total unfragmented network packet: IP header + UDP header + payload.
    public int UdpPacketSize { get; init; } = 56;
    public bool UdpAllowFragmentation { get; init; }
    public string? SourceAddress { get; init; }
    public string? InterfaceId { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxHops { get; init; } = 30;
    public int HistoryLimit { get; init; } = 1800;
    public int Concurrency { get; init; } = 6;
    public bool ResolveHostnames { get; init; } = true;

    internal void Validate()
    {
        if (!Enum.IsDefined(Protocol)) throw new ArgumentOutOfRangeException(nameof(Protocol));
        if (DestinationPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(DestinationPort));
        if (!Enum.IsDefined(UdpPortMode)) throw new ArgumentOutOfRangeException(nameof(UdpPortMode));
        if (UdpEndPort is < 1 or > 65535 || Protocol == ProbeProtocol.Udp && UdpPortMode == UdpPortMode.Range && UdpEndPort < DestinationPort)
            throw new ArgumentOutOfRangeException(nameof(UdpEndPort));
        if (UdpPacketSize is < 56 or > 65535) throw new ArgumentOutOfRangeException(nameof(UdpPacketSize));
        if (SourceAddress is not null && !System.Net.IPAddress.TryParse(SourceAddress, out _))
            throw new ArgumentException("Source address must be an IP address.", nameof(SourceAddress));
        if (Interval < TimeSpan.FromMilliseconds(100) || Interval > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(Interval));
        if (Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaxHops is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxHops));
        if (HistoryLimit is < 10 or > 100_000) throw new ArgumentOutOfRangeException(nameof(HistoryLimit));
        if (Concurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(Concurrency));
    }
}

public sealed record LatencySample(DateTimeOffset Timestamp, double? LatencyMs, ProbeStatus Status)
{
    public string? Detail { get; init; }
    public ProbeResponseKind ResponseKind { get; init; }
    public ProbeTransmission Transmission { get; init; }
}

public sealed record ProbeMeasurement(int Hop, DateTimeOffset Timestamp, ProbeResult Result, ProbeOptions Options)
{ public bool Canceled { get; init; } }

public sealed record HopSnapshot(
    int Hop, string? Address, string? Hostname, double? LastMs, double? AverageMs,
    double? MinimumMs, double? MaximumMs, double? JitterMs, long Sent, long Received,
    double LossPercent, bool IsDestination, IReadOnlyList<LatencySample> History,
    ProbeStatus Status)
{
    public int MeasurementEpoch { get; init; }
    public string? Detail { get; init; }
    public ProbeResponseKind ResponseKind { get; init; }
    public ProbeTransmission Transmission { get; init; }
}

public sealed record RoundSnapshot(
    Guid SessionId, string Target, string DestinationAddress, DateTimeOffset Timestamp,
    long Round, bool DestinationReached, IReadOnlyList<HopSnapshot> Hops,
    HopSnapshot Destination, TimeSpan RoundDuration, string? Warning = null)
{
    public int MeasurementEpoch { get; init; }
    public ProbeOptions? MeasurementOptions { get; init; }
    public IReadOnlyList<ProbeMeasurement> Measurements { get; init; } = [];
    public TimeSpan? SamplingInterval { get; init; }
    public bool RouteBoundIsHistorical { get; init; }
    public bool RouteChanged { get; init; }
    public string? RouteDescription { get; init; }
}

public sealed class TargetSession
{
    internal TargetSession(Guid id, string target, string resolvedAddress, ProbeOptions options)
    { Id = id; Target = target; ResolvedAddress = resolvedAddress; Options = options; }
    public Guid Id { get; }
    public string Target { get; }
    public string ResolvedAddress { get; }
    public ProbeOptions Options { get; internal set; }
    public ProtocolCapability? Capability { get; internal set; }
    public string Group { get; set; } = "Default";
    public SessionState State { get; internal set; } = SessionState.Running;
    public string? Error { get; internal set; }
    public RoundSnapshot? LatestSnapshot { get; internal set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task? Worker { get; set; }
    internal object StatisticsGate { get; } = new();
    internal long StatisticsGeneration { get; set; }
}

public sealed record StoredSession(Guid Id, string Target, string DestinationAddress,
    DateTimeOffset StartedAt, DateTimeOffset? StoppedAt, long SampleCount);
public sealed record StoredSample(Guid SessionId, long Round, int Hop, string? Address,
    bool IsDestination, DateTimeOffset Timestamp, double? LatencyMs, ProbeStatus Status)
{
    public int MeasurementEpoch { get; init; }
    public string? Detail { get; init; }
    public ProbeResponseKind ResponseKind { get; init; }
    public ProbeTransmission Transmission { get; init; }
}

public sealed record ProbeResult(string? Address, double? LatencyMs, ProbeStatus Status, string? Detail = null)
{
    public ProbeResponseKind ResponseKind { get; init; }
    public ProbeTransmission Transmission { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }
    public bool Responded => LatencyMs is >= 0 and < double.PositiveInfinity && Status is ProbeStatus.Success or ProbeStatus.TimeExceeded;
}

public interface IProbeTransport
{
    Task<ProbeResult> ProbeAsync(System.Net.IPAddress destination, int ttl, TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IConfiguredProbeTransport : IProbeTransport
{
    Task<ProbeResult> ProbeAsync(System.Net.IPAddress destination, int ttl, ProbeOptions options,
        CancellationToken cancellationToken);
}
