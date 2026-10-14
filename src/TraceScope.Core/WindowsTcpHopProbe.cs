using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TraceScope.Core;

/// <summary>
/// Windows TCP hop discovery: ordinary TTL-limited TCP connects, with a raw ICMP
/// receiver for router replies. No raw TCP transmission, capture driver, or service.
/// Raw ICMP reception requires an explicitly elevated application on Windows.
/// </summary>
internal static class WindowsTcpHopProbe
{
    private static readonly object PortsGate = new();
    private static readonly Dictionary<int, DateTimeOffset> RetiredPorts = [];
    // A retired source port cannot match a late quote from a preceding probe.
    private static readonly TimeSpan PortQuarantine = TimeSpan.FromMinutes(2);

    internal static string CapabilityFailure(SocketError error) => error == SocketError.AccessDenied
        ? "Windows TCP hop discovery requires raw ICMP reception. Run Pngr explicitly as administrator to enable it; TCP endpoint monitoring remains available. No driver or service is installed."
        : $"Windows TCP hop discovery is unavailable ({error}); TCP endpoint monitoring remains available.";

    public static async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl,
        ProbeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new ProbeResult(null, null, ProbeStatus.Error, "Windows TCP hop receiver is only available on Windows.")
                { Transmission = ProbeTransmission.NotSent };
        if (options.Protocol != ProbeProtocol.Tcp) throw new ArgumentException("TCP protocol required.", nameof(options));
        if (ttl is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (!string.IsNullOrWhiteSpace(options.InterfaceId) && string.IsNullOrWhiteSpace(options.SourceAddress))
            return new ProbeResult(null, null, ProbeStatus.Error, "Select a local source address to bind this interface.")
                { Transmission = ProbeTransmission.NotSent };

        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ProbeResult>? receive = null;
        Task<ProbeResult>? connect = null;
        Socket? sender = null;
        Socket? receiver = null;
        var port = 0;
        var transmitted = false;
        try
        {
            // Capability is established before transmitting a diagnostic probe.
            try
            {
                receiver = new Socket(destination.AddressFamily, SocketType.Raw,
                    ipv6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
            }
            catch (SocketException ex)
            {
                return new ProbeResult(null, null, ProbeStatus.Error, CapabilityFailure(ex.SocketErrorCode))
                    { Transmission = ProbeTransmission.NotSent };
            }
            IPAddress source;
            if (!string.IsNullOrWhiteSpace(options.SourceAddress))
            {
                if (!IPAddress.TryParse(options.SourceAddress, out source!) || source.AddressFamily != destination.AddressFamily)
                    return new ProbeResult(null, null, ProbeStatus.Error, "Source address must match the destination address family.")
                        { Transmission = ProbeTransmission.NotSent };
            }
            else
            {
                // UDP connect only asks the local routing table for its source;
                // it does not transmit a UDP probe or substitute an ICMP measurement.
                using var route = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                route.Connect(new IPEndPoint(destination, options.DestinationPort));
                source = ((IPEndPoint)route.LocalEndPoint!).Address;
            }
            receiver.Bind(new IPEndPoint(source, 0));
            sender = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            sender.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
                SocketOptionName.IpTimeToLive, ttl); // Windows IPV6_UNICAST_HOPS is 4, not HopLimit (21).
            port = BindUniquePort(sender, source);
            deadline.CancelAfter(options.Timeout);
            var clock = Stopwatch.StartNew();
            receive = ReceiveMatchedAsync(receiver, source, destination, port, options, clock, deadline.Token);
            transmitted = true; // Connect was submitted to the TCP stack; immediate setup errors are classified below.
            connect = ConnectAsync(sender, destination, options, clock, deadline.Token);
            var first = await Task.WhenAny(receive, connect);
            if (first == receive) return await receive;
            var direct = await connect;
            if (direct.Status == ProbeStatus.Success || direct.Status == ProbeStatus.Error) return direct;
            // Socket refusal/reset alone does not identify whether a router sent
            // an ICMP rejection. Prefer the validated quote, otherwise retain the
            // conservative network failure and its diagnostic detail.
            try { return await receive; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return direct; }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(null, null, ProbeStatus.Timeout, "No validated TCP or ICMP response before the Windows hop deadline.")
                { Transmission = transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent };
        }
        catch (OperationCanceledException) { throw; }
        catch (SocketException ex)
        {
            return SocketFailure(ex.SocketErrorCode, transmitted, ex.Message);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new ProbeResult(null, null, ProbeStatus.Error, "TCP setup failure: " + ex.Message)
                { Transmission = transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent };
        }
        finally
        {
            await deadline.CancelAsync();
            receiver?.Dispose();
            sender?.Dispose();
            await ObserveAsync(receive);
            await ObserveAsync(connect);
            if (port != 0) lock (PortsGate) RetiredPorts[port] = DateTimeOffset.UtcNow + PortQuarantine;
        }
    }

    private static int BindUniquePort(Socket socket, IPAddress source)
    {
        lock (PortsGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var port in RetiredPorts.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                RetiredPorts.Remove(port);
            // Binding an explicit random unprivileged port also prevents collisions
            // between concurrent targets. The retired map remains bounded by the
            // supported port pool and expires after two minutes.
            for (var attempt = 0; attempt < 256; attempt++)
            {
                var port = Random.Shared.Next(49152, 65536);
                if (RetiredPorts.ContainsKey(port)) continue;
                try { socket.Bind(new IPEndPoint(source, port)); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { continue; }
                RetiredPorts[port] = DateTimeOffset.MaxValue;
                return port;
            }
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static async Task<ProbeResult> ConnectAsync(Socket sender, IPAddress destination,
        ProbeOptions options, Stopwatch clock, CancellationToken token)
    {
        try
        {
            await sender.ConnectAsync(new IPEndPoint(destination, options.DestinationPort), token);
            ProbeDeadline.Verify(clock.Elapsed, options.Timeout, token);
            return new ProbeResult(destination.ToString(), clock.Elapsed.TotalMilliseconds, ProbeStatus.Success,
                "Validated TCP connect at requested TTL; RTT includes connect-call overhead.")
                { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.TcpConnected };
        }
        catch (SocketException ex)
        {
            var local = ex.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressNotAvailable
                or SocketError.InvalidArgument or SocketError.NotSocket or SocketError.OperationNotSupported;
            return SocketFailure(ex.SocketErrorCode, !local, ex.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw; }
    }

    internal static ProbeResult SocketFailure(SocketError error, bool transmitted, string? detail = null)
    {
        var status = error switch
        {
            SocketError.TimedOut => ProbeStatus.Timeout,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown
                or SocketError.HostDown or SocketError.ConnectionRefused or SocketError.ConnectionReset
                => ProbeStatus.Unreachable,
            _ => transmitted ? ProbeStatus.Timeout : ProbeStatus.Error
        };
        return new ProbeResult(null, null, status,
            $"TCP socket {error}; {(transmitted ? "connect submitted" : "probe not transmitted")}. "
            + "No independently validated destination response is implied. " + detail)
            { Transmission = transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent,
                ResponseKind = status == ProbeStatus.Unreachable ? ProbeResponseKind.NetworkRejection : ProbeResponseKind.None };
    }

    private static async Task<ProbeResult> ReceiveMatchedAsync(Socket receiver, IPAddress source,
        IPAddress destination, int sourcePort, ProbeOptions options, Stopwatch clock, CancellationToken token)
    {
        var buffer = new byte[65535];
        EndPoint remote = new IPEndPoint(destination.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (true)
        {
            var packet = await receiver.ReceiveFromAsync(buffer, SocketFlags.None, remote, token);
            ProbeDeadline.Verify(clock.Elapsed, options.Timeout, token);
            var response = ParseIcmp(buffer.AsSpan(0, packet.ReceivedBytes), ((IPEndPoint)packet.RemoteEndPoint).Address,
                source, destination, sourcePort, options.DestinationPort, clock.Elapsed.TotalMilliseconds);
            if (response is not null) return response;
        }
    }

    internal static ProbeResult? ParseIcmp(ReadOnlySpan<byte> packet, IPAddress responder, IPAddress source,
        IPAddress destination, int sourcePort, int destinationPort, double latency)
    {
        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        if (source.AddressFamily != destination.AddressFamily || responder.AddressFamily != destination.AddressFamily) return null;
        var offset = 0;
        if (!ipv6)
        {
            if (packet.Length < 20 || packet[0] >> 4 != 4) return null;
            offset = (packet[0] & 15) * 4;
            // The provider's source and the included outer header must agree.
            if (!packet.Slice(12, 4).SequenceEqual(responder.GetAddressBytes())
                || !packet.Slice(16, 4).SequenceEqual(source.GetAddressBytes())) return null;
        }
        else if (packet.Length >= 40 && packet[0] >> 4 == 6)
        {
            if (!packet.Slice(8, 16).SequenceEqual(responder.GetAddressBytes())
                || !packet.Slice(24, 16).SequenceEqual(source.GetAddressBytes())) return null;
            offset = 40;
        }
        if (offset < (ipv6 ? 0 : 20) || packet.Length < offset + 8 + (ipv6 ? 40 : 20)) return null;
        var quote = packet[(offset + 8)..];
        if (!quote.Slice(ipv6 ? 8 : 12, ipv6 ? 16 : 4).SequenceEqual(source.GetAddressBytes())) return null;
        var result = SocketHopProbe.ParseIcmp(packet, responder, destination, sourcePort, destinationPort, ProbeProtocol.Tcp, latency, expectedSource: source);
        return result is null ? null : result with
        {
            Transmission = ProbeTransmission.Sent,
            ResponseKind = result.Status == ProbeStatus.TimeExceeded ? ProbeResponseKind.IcmpTimeExceeded : ProbeResponseKind.NetworkRejection
        };
    }

    private static async Task ObserveAsync(Task<ProbeResult>? task)
    {
        if (task is null) return;
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
}
