using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TraceScope.Core;

/// <summary>Independent UDP probes; only matched ICMP or a received UDP datagram establishes a response.</summary>
internal static class SocketHopProbe
{
    private static readonly object PortGate = new();
    private static readonly Dictionary<int, DateTimeOffset> RecentPorts = [];

    public static async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl,
        ProbeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Protocol != ProbeProtocol.Udp) throw new ArgumentException("UDP protocol required.", nameof(options));
        if (ttl is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(ttl));
        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        var source = ipv6 ? IPAddress.IPv6Any : IPAddress.Any;
        if (!string.IsNullOrWhiteSpace(options.SourceAddress))
        {
            if (!IPAddress.TryParse(options.SourceAddress, out source)
                || source.AddressFamily != destination.AddressFamily)
                return new ProbeResult(null, null, ProbeStatus.Error, "Source address must match the destination address family.") { Transmission = ProbeTransmission.NotSent };
        }
        else if (!string.IsNullOrWhiteSpace(options.InterfaceId))
            return new ProbeResult(null, null, ProbeStatus.Error, "Select a local source address to bind this interface.") { Transmission = ProbeTransmission.NotSent };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Socket? sender = null;
        Socket? receiver = null;
        Task<ProbeResult>? received = null;
        Task<ProbeResult>? direct = null;
        var transmitted = false;
        string? capability = null;
        try
        {
            sender = BindFreshSender(destination.AddressFamily, source, options.DestinationPort);
            if (ipv6) UdpFragmentation.ConfigureIpv6HopLimit(sender, ttl);
            else sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            if (!ipv6) sender.DontFragment = !options.UdpAllowFragmentation;
            else UdpFragmentation.ConfigureIpv6(sender, !options.UdpAllowFragmentation);
            await sender.ConnectAsync(new IPEndPoint(destination, options.DestinationPort), cancellationToken);
            var local = (IPEndPoint)sender.LocalEndPoint!;
            try
            {
                receiver = new Socket(destination.AddressFamily,
                    OperatingSystem.IsWindows() ? SocketType.Raw : SocketType.Dgram,
                    ipv6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
                receiver.Bind(new IPEndPoint(local.Address, 0));
            }
            catch (SocketException ex)
            {
                receiver?.Dispose(); receiver = null;
                capability = $"ICMP validation unavailable ({ex.SocketErrorCode}); "
                    + (OperatingSystem.IsWindows() ? "Windows raw ICMP requires administrator privileges. " : "")
                    + "Only received UDP application datagrams can establish reachability; hop discovery is incomplete.";
            }
            var payload = CreatePayload(options.UdpPacketSize, ipv6);
            var stopwatch = Stopwatch.StartNew();
            deadline.CancelAfter(options.Timeout);
            if (receiver is not null)
                received = ReceiveMatchedAsync(receiver, destination, local, options, payload,
                    stopwatch, deadline.Token);
            var count = await sender.SendAsync(payload, SocketFlags.None, deadline.Token);
            if (count != payload.Length)
                return new ProbeResult(null, null, ProbeStatus.Error, "UDP datagram was not transmitted in full.") { Transmission = ProbeTransmission.NotSent };
            transmitted = true;
            direct = ReceiveDirectAsync(sender, destination, options, stopwatch, deadline.Token);
            if (received is null)
            {
                var result = await direct;
                return result with { Detail = result.Detail + " " + capability };
            }
            var first = await Task.WhenAny(received, direct);
            if (first == received) return await received;
            var application = await direct;
            if (application.Responded) return application;
            // A connected-socket error cannot establish the ICMP origin. Continue
            // waiting for its quoted packet until the original deadline.
            try { return await received; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return application; }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new ProbeResult(null, null, ProbeStatus.Timeout, "No qualifying UDP response within timeout. " + capability) { Transmission = transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent }; }
        catch (OperationCanceledException) { throw; }
        catch (SocketException ex)
        { return SocketFailure(ex.SocketErrorCode, transmitted, ex.Message); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        { return new ProbeResult(null, null, ProbeStatus.Error, "UDP setup failure: " + ex.Message) { Transmission = ProbeTransmission.NotSent }; }
        finally
        {
            await deadline.CancelAsync();
            sender?.Dispose(); receiver?.Dispose();
            if (received is not null) await ObserveCompletionAsync(received);
            if (direct is not null) await ObserveCompletionAsync(direct);
        }
    }

    internal static byte[] CreatePayload(int totalPacketSize, bool ipv6)
    {
        if (totalPacketSize is < 56 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(totalPacketSize), "UDP packet size includes IP/UDP headers and must be 56–65535 bytes.");
        var payload = new byte[totalPacketSize - (ipv6 ? 48 : 28)];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 8));
        return payload;
    }

    internal static Socket BindFreshSender(AddressFamily family, IPAddress source, int destinationPort,
        Action<Socket, IPEndPoint>? bind = null)
    {
        lock (PortGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var port in RecentPorts.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) RecentPorts.Remove(port);
            // Keep recently used source ports out of outstanding tuples even after
            // the socket closes. RFC 792 quotes may contain only the UDP header.
            for (var attempt = 0; attempt < 512; attempt++)
            {
                var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    var endpoint = new IPEndPoint(source, 0);
                    if (bind is null) socket.Bind(endpoint); else bind(socket, endpoint);
                }
                catch { socket.Dispose(); throw; }
                var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                // Never create a local self-connected UDP tuple: its own outgoing probe
                // can otherwise be received as an apparent application response.
                if (port != destinationPort && !RecentPorts.ContainsKey(port))
                { RecentPorts[port] = now.AddMinutes(2); return socket; }
                socket.Dispose();
            }
            throw new SocketException((int)SocketError.TooManyOpenSockets);
        }
    }

    internal static ProbeResult SocketFailure(SocketError error, bool transmitted, string? detail = null)
    {
        var status = error switch
        {
            SocketError.TimedOut => ProbeStatus.Timeout,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown
                or SocketError.HostDown or SocketError.ConnectionRefused or SocketError.ConnectionReset
                or SocketError.NetworkReset or SocketError.ConnectionAborted
                => ProbeStatus.Unreachable,
            // A failed local receiver is unavailable measurement, even after a send.
            SocketError.AccessDenied or SocketError.AddressNotAvailable or SocketError.InvalidArgument
                or SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported
                or SocketError.OperationNotSupported or SocketError.ProtocolOption or SocketError.NotSocket
                or SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets or SocketError.MessageSize
                or SocketError.Fault or SocketError.Interrupted or SocketError.Shutdown
                => ProbeStatus.Error,
            _ => transmitted ? ProbeStatus.Timeout : ProbeStatus.Error
        };
        return new ProbeResult(null, null, status,
            $"UDP socket {error}; {(transmitted ? "datagram transmitted" : "datagram not transmitted")}. "
            + "No validated ICMP origin or successful delivery is implied. " + detail)
            { Transmission = transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent,
                ResponseKind = status == ProbeStatus.Unreachable ? ProbeResponseKind.NetworkRejection : ProbeResponseKind.None };
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException
            or ObjectDisposedException or ArgumentException or InvalidOperationException or NotSupportedException) { }
    }

    private static async Task<ProbeResult> ReceiveDirectAsync(Socket sender, IPAddress destination,
        ProbeOptions options, Stopwatch stopwatch, CancellationToken token)
    {
        try
        {
            await sender.ReceiveAsync(new byte[65535], SocketFlags.None, token);
            ProbeDeadline.Verify(stopwatch.Elapsed, options.Timeout, token);
            return new ProbeResult(destination.ToString(), stopwatch.Elapsed.TotalMilliseconds,
                ProbeStatus.Success, $"Received UDP application datagram from port {options.DestinationPort}.")
                { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.UdpReply };
        }
        catch (SocketException ex) { return SocketFailure(ex.SocketErrorCode, true, ex.Message); }
    }

    private static async Task<ProbeResult> ReceiveMatchedAsync(Socket receiver, IPAddress destination,
        IPEndPoint local, ProbeOptions options, byte[] payload, Stopwatch stopwatch, CancellationToken token)
    {
        var buffer = new byte[65535];
        EndPoint remote = new IPEndPoint(destination.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (true)
        {
            var received = await receiver.ReceiveFromAsync(buffer, SocketFlags.None, remote, token);
            ProbeDeadline.Verify(stopwatch.Elapsed, options.Timeout, token);
            var response = ParseIcmp(buffer.AsSpan(0, received.ReceivedBytes),
                ((IPEndPoint)received.RemoteEndPoint).Address, destination, local.Port,
                options.DestinationPort, options.Protocol, stopwatch.Elapsed.TotalMilliseconds,
                local.Address, payload);
            if (response is not null) return response;
        }
    }

    /// <summary>Match RFC 792/4443 quoted packets; unrelated traffic never becomes a measurement.</summary>
    internal static ProbeResult? ParseIcmp(ReadOnlySpan<byte> packet, IPAddress responder,
        IPAddress destination, int sourcePort, int destinationPort, ProbeProtocol protocol, double latencyMs,
        IPAddress? expectedSource = null, byte[]? expectedPayload = null)
    {
        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        var offset = 0;
        if (!ipv6)
        {
            if (packet.Length < 20 || packet[0] >> 4 != 4 || packet[9] != 1) return null;
            offset = (packet[0] & 15) * 4;
            if (offset < 20 || packet.Length < offset + 8) return null;
        }
        else if (packet.Length >= 40 && packet[0] >> 4 == 6)
        {
            // Some socket providers include the outer IPv6 header.
            if (packet[6] != 58) return null;
            offset = 40;
        }
        if (packet.Length < offset + 8) return null;
        var type = packet[offset];
        var code = packet[offset + 1];
        var timeExceeded = ipv6 ? type == 3 && code == 0 : type == 11 && code == 0;
        var unreachable = ipv6 ? type == 1 : type == 3;
        if (!timeExceeded && !unreachable) return null;
        if (responder.AddressFamily != destination.AddressFamily) return null;
        // Raw IPv4 providers include an outer IP header; validate it against
        // ReceiveFrom's source metadata and our local address when supplied.
        if (!ipv6 && expectedSource is not null
            && (!packet.Slice(12, 4).SequenceEqual(responder.GetAddressBytes())
                || !packet.Slice(16, 4).SequenceEqual(expectedSource.GetAddressBytes()))) return null;
        if (ipv6 && offset == 40 && expectedSource is not null
            && (!packet.Slice(8, 16).SequenceEqual(responder.GetAddressBytes())
                || !packet.Slice(24, 16).SequenceEqual(expectedSource.GetAddressBytes()))) return null;
        var quoted = packet[(offset + 8)..];
        if (expectedSource is not null)
        {
            var sourceOffset = ipv6 ? 8 : 12;
            var sourceLength = ipv6 ? 16 : 4;
            if (quoted.Length < sourceOffset + sourceLength
                || !quoted.Slice(sourceOffset, sourceLength).SequenceEqual(expectedSource.GetAddressBytes())) return null;
        }
        int transportOffset;
        int quotedProtocol;
        if (ipv6)
        {
            if (quoted.Length < 40 || quoted[0] >> 4 != 6
                || !quoted.Slice(24, 16).SequenceEqual(destination.GetAddressBytes())) return null;
            transportOffset = 40;
            quotedProtocol = quoted[6];
            for (var i = 0; i < 8 && quotedProtocol is 0 or 43 or 44 or 51 or 60; i++)
            {
                if (quoted.Length < transportOffset + 2) return null;
                var next = quoted[transportOffset];
                var length = quotedProtocol switch
                { 44 => 8, 51 => (quoted[transportOffset + 1] + 2) * 4,
                    _ => (quoted[transportOffset + 1] + 1) * 8 };
                if (quoted.Length < transportOffset + length) return null;
                if (quotedProtocol == 44 && (BinaryPrimitives.ReadUInt16BigEndian(
                        quoted.Slice(transportOffset + 2, 2)) & 0xfff8) != 0) return null;
                transportOffset += length;
                quotedProtocol = next;
            }
        }
        else
        {
            if (quoted.Length < 20 || quoted[0] >> 4 != 4
                || !quoted.Slice(16, 4).SequenceEqual(destination.GetAddressBytes())) return null;
            // Ignore noninitial fragments: their bytes are not transport ports.
            if ((BinaryPrimitives.ReadUInt16BigEndian(quoted.Slice(6, 2)) & 0x1fff) != 0) return null;
            transportOffset = (quoted[0] & 15) * 4;
            if (transportOffset < 20) return null;
            quotedProtocol = quoted[9];
        }
        if (quotedProtocol != (protocol == ProbeProtocol.Tcp ? 6 : 17)
            || quoted.Length < transportOffset + 4
            || BinaryPrimitives.ReadUInt16BigEndian(quoted.Slice(transportOffset, 2)) != sourcePort
            || BinaryPrimitives.ReadUInt16BigEndian(quoted.Slice(transportOffset + 2, 2)) != destinationPort)
            return null;
        if (expectedPayload is not null && protocol == ProbeProtocol.Udp)
        {
            if (quoted.Length < transportOffset + 8
                || BinaryPrimitives.ReadUInt16BigEndian(quoted.Slice(transportOffset + 4, 2)) != expectedPayload.Length + 8) return null;
            var quotedPayloadLength = Math.Min(expectedPayload.Length, quoted.Length - transportOffset - 8);
            if (!quoted.Slice(transportOffset + 8, quotedPayloadLength).SequenceEqual(expectedPayload.AsSpan(0, quotedPayloadLength))) return null;
        }
        var portUnreachable = unreachable && (ipv6 ? code == 4 : code == 3)
            && protocol == ProbeProtocol.Udp
            && responder.GetAddressBytes().AsSpan().SequenceEqual(destination.GetAddressBytes());
        var status = timeExceeded ? ProbeStatus.TimeExceeded
            : portUnreachable ? ProbeStatus.Success : ProbeStatus.Unreachable;
        return new ProbeResult(responder.ToString(), status == ProbeStatus.Unreachable ? null : latencyMs,
            status, $"Matched ICMP{(ipv6 ? "v6" : "")} type {type}, code {code} for {protocol} port {destinationPort}."
                + (portUnreachable ? " Destination rejected UDP port; host reachability, not application service availability." : ""))
            { Transmission = ProbeTransmission.Sent, ResponseKind = portUnreachable
                ? ProbeResponseKind.UdpPortUnreachable : timeExceeded ? ProbeResponseKind.IcmpTimeExceeded
                : ProbeResponseKind.NetworkRejection };
    }
}
