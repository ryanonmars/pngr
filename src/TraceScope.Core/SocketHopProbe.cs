using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TraceScope.Core;

/// <summary>Independent Darwin UDP hop discovery without packet capture, helpers or privileges.</summary>
internal static class SocketHopProbe
{
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
                return new ProbeResult(null, null, ProbeStatus.Error,
                    "Source address must match the destination address family.");
        }
        else if (!string.IsNullOrWhiteSpace(options.InterfaceId))
            return new ProbeResult(null, null, ProbeStatus.Error,
                "Select a local source address to bind this interface.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        Task<ProbeResult>? received = null;
        Task<ProbeResult>? sent = null;
        try
        {
            using var receiver = new Socket(destination.AddressFamily, SocketType.Dgram,
                ipv6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
            using var sender = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            sender.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
                ipv6 ? SocketOptionName.HopLimit : SocketOptionName.IpTimeToLive, ttl);
            sender.Bind(new IPEndPoint(source, 0));
            // Binding the ICMP socket restricts reception when a source was selected.
            receiver.Bind(new IPEndPoint(source, 0));
            var sourcePort = ((IPEndPoint)sender.LocalEndPoint!).Port;
            var stopwatch = Stopwatch.StartNew();
            received = ReceiveMatchedAsync(receiver, destination, sourcePort, options,
                stopwatch, deadline.Token);
            sent = SendAsync(sender, destination, options, stopwatch, deadline.Token);
            try
            {
                var first = await Task.WhenAny(received, sent);
                if (first == received) return await received;
                var direct = await sent;
                if (direct.Responded) return direct;
                // A socket-level unreachable error does not identify its router.
                // Give the quoted ICMP response a chance to identify the real hop.
                return await received;
            }
            finally
            {
                await deadline.CancelAsync();
                // No pending receive/connect is allowed to outlive the probe.
                await ObserveCompletionAsync(received);
                await ObserveCompletionAsync(sent);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new ProbeResult(null, null, ProbeStatus.Timeout, $"No {options.Protocol} response within timeout."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        { return new ProbeResult(null, null, ProbeStatus.Error, $"{options.Protocol} hop socket unavailable: {ex.Message}"); }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static async Task<ProbeResult> SendAsync(Socket sender, IPAddress destination,
        ProbeOptions options, Stopwatch stopwatch, CancellationToken token)
    {
        try
        {
            await sender.ConnectAsync(new IPEndPoint(destination, options.DestinationPort), token);
            await sender.SendAsync(new byte[16], SocketFlags.None, token);
            var buffer = new byte[2048];
            await sender.ReceiveAsync(buffer, SocketFlags.None, token);
            return new ProbeResult(destination.ToString(), stopwatch.Elapsed.TotalMilliseconds,
                ProbeStatus.Success, $"UDP application response at requested TTL, port {options.DestinationPort}.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
        {
            return new ProbeResult(destination.ToString(), stopwatch.Elapsed.TotalMilliseconds,
                ProbeStatus.Success, "ICMP UDP port-unreachable at requested TTL; host responded.");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(null, null, ProbeStatus.Error, ex.Message);
        }
    }

    private static async Task<ProbeResult> ReceiveMatchedAsync(Socket receiver, IPAddress destination,
        int sourcePort, ProbeOptions options, Stopwatch stopwatch, CancellationToken token)
    {
        var buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(destination.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (true)
        {
            var received = await receiver.ReceiveFromAsync(buffer, SocketFlags.None, remote, token);
            var response = ParseIcmp(buffer.AsSpan(0, received.ReceivedBytes),
                ((IPEndPoint)received.RemoteEndPoint).Address, destination, sourcePort,
                options.DestinationPort, options.Protocol, stopwatch.Elapsed.TotalMilliseconds);
            if (response is not null) return response;
        }
    }

    /// <summary>Match RFC 792/4443 quoted packets; unrelated traffic never becomes a measurement.</summary>
    internal static ProbeResult? ParseIcmp(ReadOnlySpan<byte> packet, IPAddress responder,
        IPAddress destination, int sourcePort, int destinationPort, ProbeProtocol protocol, double latencyMs)
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
        var quoted = packet[(offset + 8)..];
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
        var portUnreachable = unreachable && (ipv6 ? code == 4 : code == 3)
            && protocol == ProbeProtocol.Udp
            && responder.GetAddressBytes().AsSpan().SequenceEqual(destination.GetAddressBytes());
        var status = timeExceeded ? ProbeStatus.TimeExceeded
            : portUnreachable ? ProbeStatus.Success : ProbeStatus.Unreachable;
        return new ProbeResult(responder.ToString(), status == ProbeStatus.Unreachable ? null : latencyMs,
            status, $"Matched ICMP{(ipv6 ? "v6" : "")} type {type}, code {code} for {protocol} port {destinationPort}.");
    }
}
