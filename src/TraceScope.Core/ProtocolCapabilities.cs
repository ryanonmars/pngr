using System.Net.Sockets;

namespace TraceScope.Core;

/// <summary>Local transport capabilities; availability never guarantees that routers will respond.</summary>
public sealed record ProtocolCapability(bool HopDiscoveryAvailable, bool EndpointMonitoringAvailable,
    bool RequiresElevation, string Detail);

public static class ProtocolCapabilities
{
    /// <summary>Checks local socket support without transmitting traffic or requesting elevation.</summary>
    public static ProtocolCapability Get(ProbeProtocol protocol, AddressFamily family)
    {
        if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return new(false, false, false, "Only IPv4 and IPv6 are supported.");
        if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            return new(false, false, false, "IPv6 is unavailable on this operating system.");
        if (protocol == ProbeProtocol.Icmp)
            return new(true, true, false, "Native ICMP probing; network filtering can suppress replies.");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var receiver = new Socket(family, SocketType.Raw,
                    family == AddressFamily.InterNetworkV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
                return WindowsPolicy(protocol, null);
            }
            catch (SocketException ex) { return WindowsPolicy(protocol, ex.SocketErrorCode); }
            catch (NotSupportedException) { return WindowsPolicy(protocol, SocketError.OperationNotSupported); }
        }
        if (OperatingSystem.IsMacOS())
        {
            if (protocol == ProbeProtocol.Tcp && MacTcpHopProbe.FindHelper() is null)
                return new(false, true, false, "Bundled TCP hop helper is missing; TCP endpoint monitoring is available.");
            return new(true, true, false, "macOS protocol hop receiver is available; network filtering can suppress replies.");
        }
        return new(false, true, false, $"{protocol} endpoint monitoring is available; intermediate-hop discovery is unsupported on this platform.");
    }

    internal static ProtocolCapability WindowsPolicy(ProbeProtocol protocol, SocketError? receiverError)
    {
        if (receiverError is null)
            return new(true, true, true, $"Windows {protocol} raw ICMP receiver is available in this process; network filtering can suppress hop replies.");
        if (receiverError == SocketError.AccessDenied)
            return new(false, true, true, $"Windows {protocol} hop discovery requires an explicitly elevated application; endpoint monitoring remains available. No automatic elevation, driver, or service.");
        return new(false, true, false, $"Windows {protocol} hop receiver is unavailable ({receiverError}); endpoint monitoring remains available.");
    }
}
