using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class UdpAccuracyTests
{
    private static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");

    [Fact]
    public void CompleteQuoteValidatesOriginOriginalSourceTupleLengthAndIdentity()
    {
        var payload = SocketHopProbe.CreatePayload(56, false);
        var packet = Quote(payload);
        ProbeResult? Parse(byte[] bytes, IPAddress? responder = null, int sourcePort = 50001) =>
            SocketHopProbe.ParseIcmp(bytes, responder ?? Router, Target, sourcePort, 33434,
                ProbeProtocol.Udp, 3.5, Local, payload);
        Assert.Equal(ProbeStatus.TimeExceeded, Parse(packet)!.Status);
        Assert.Null(Parse(packet, Target)); // Outer source disagrees with socket metadata.
        Assert.Null(Parse(packet, sourcePort: 50002)); // Another concurrent probe.
        var wrong = packet.ToArray(); wrong[40] ^= 1; // Quoted original source.
        Assert.Null(Parse(wrong));
        wrong = packet.ToArray(); wrong[52] ^= 1; // Destination port.
        Assert.Null(Parse(wrong));
        wrong = packet.ToArray(); wrong[56] ^= 1; // Nonce from a late/different probe.
        Assert.Null(Parse(wrong));
        wrong = packet.ToArray(); wrong[54] ^= 1; // UDP checksum (not used as an identifier).
        Assert.NotNull(Parse(wrong));
        // RFC 792 minimum quote has no payload. The uniquely reserved source port
        // and full IP/UDP tuple still identify the probe.
        Assert.NotNull(Parse(packet[..56]));
    }

    [Fact]
    public void Ipv6IncludedOuterHeaderMustMatchOriginAndLocalDestination()
    {
        var local = IPAddress.Parse("2001:db8::10");
        var router = IPAddress.Parse("2001:db8::1");
        var destination = IPAddress.Parse("2001:db8::2");
        var payload = SocketHopProbe.CreatePayload(56, true);
        var packet = new byte[104];
        packet[0] = 0x60; packet[6] = 58;
        router.GetAddressBytes().CopyTo(packet, 8);
        local.GetAddressBytes().CopyTo(packet, 24);
        packet[40] = 3;
        packet[48] = 0x60; packet[54] = 17;
        local.GetAddressBytes().CopyTo(packet, 56);
        destination.GetAddressBytes().CopyTo(packet, 72);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(88, 2), 50001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(90, 2), 33434);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(92, 2), 16);
        payload.CopyTo(packet, 96);
        ProbeResult? Parse(byte[] bytes) => SocketHopProbe.ParseIcmp(bytes, router,
            destination, 50001, 33434, ProbeProtocol.Udp, 1, local, payload);
        Assert.Equal(ProbeStatus.TimeExceeded, Parse(packet)!.Status);
        var wrong = packet.ToArray(); wrong[8] ^= 1;
        Assert.Null(Parse(wrong));
        wrong = packet.ToArray(); wrong[24] ^= 1;
        Assert.Null(Parse(wrong));
        wrong = packet.ToArray(); wrong[56] ^= 1;
        Assert.Null(Parse(wrong));
        // ICMPv6 sockets commonly omit the outer header; source metadata plus
        // the quoted original source and full tuple are still checked.
        Assert.NotNull(Parse(packet[40..]));
    }

    [Fact]
    public void OnlyDestinationPortUnreachableEstablishesUdpHostReachability()
    {
        var payload = SocketHopProbe.CreatePayload(56, false);
        var packet = Quote(payload);
        packet[20] = 3; packet[21] = 3;
        Assert.Equal(ProbeStatus.Unreachable, Parse(packet, Router, payload)!.Status);
        Target.GetAddressBytes().CopyTo(packet, 12);
        var reachable = Parse(packet, Target, payload)!;
        Assert.Equal(ProbeStatus.Success, reachable.Status);
        Assert.Contains("not application service availability", reachable.Detail);
        packet[21] = 1; // Host unreachable, even from the expected destination.
        Assert.Equal(ProbeStatus.Unreachable, Parse(packet, Target, payload)!.Status);
        packet[20] = 11; packet[21] = 1; // Fragment reassembly timeout isn't TTL transit expiry.
        Assert.Null(Parse(packet, Target, payload));
    }

    [Theory]
    [InlineData(SocketError.NetworkUnreachable, false, ProbeStatus.Unreachable)]
    [InlineData(SocketError.HostUnreachable, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.ConnectionRefused, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.ConnectionReset, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.TimedOut, true, ProbeStatus.Timeout)]
    [InlineData(SocketError.AccessDenied, false, ProbeStatus.Error)]
    [InlineData(SocketError.AddressNotAvailable, false, ProbeStatus.Error)]
    [InlineData(SocketError.MessageSize, false, ProbeStatus.Error)]
    [InlineData(SocketError.OperationAborted, true, ProbeStatus.Timeout)]
    [InlineData(SocketError.AccessDenied, true, ProbeStatus.Error)]
    [InlineData(SocketError.NoBufferSpaceAvailable, true, ProbeStatus.Error)]
    [InlineData(SocketError.InvalidArgument, true, ProbeStatus.Error)]
    [InlineData(SocketError.ProtocolNotSupported, true, ProbeStatus.Error)]
    [InlineData(SocketError.NetworkReset, false, ProbeStatus.Unreachable)]
    [InlineData(SocketError.ConnectionAborted, true, ProbeStatus.Unreachable)]
    public void SocketErrorsNeverInventResponses(SocketError error, bool sent, ProbeStatus status)
    {
        var result = SocketHopProbe.SocketFailure(error, sent);
        Assert.Equal(status, result.Status);
        Assert.False(result.Responded);
        Assert.Null(result.LatencyMs);
    }

    [Theory]
    [InlineData(56, false, 28)]
    [InlineData(56, true, 8)]
    [InlineData(65535, false, 65507)]
    [InlineData(65535, true, 65487)]
    public void PacketSizeIsExactIncludingIpAndUdpHeaders(int size, bool ipv6, int payloadLength)
    {
        Assert.Equal(payloadLength, SocketHopProbe.CreatePayload(size, ipv6).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => SocketHopProbe.CreatePayload(55, ipv6));
        Assert.Throws<ArgumentOutOfRangeException>(() => SocketHopProbe.CreatePayload(65536, ipv6));
    }

    [Fact]
    public async Task SourceAllocatorRejectsARealSelfEchoTupleBeforeItCanFabricateServiceAvailability()
    {
        var payload = SocketHopProbe.CreatePayload(56, false);
        int destinationPort;
        // Prove the kernel behavior with a real socket: this is the sent probe, not a server reply.
        using (var self = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            self.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)self.LocalEndPoint!; destinationPort = endpoint.Port;
            self.Connect(endpoint);
            await self.SendAsync(payload, SocketFlags.None);
            var buffer = new byte[payload.Length];
            Assert.Equal(payload.Length, await self.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(payload, buffer);
        }
        var binds = 0;
        Socket? rejected = null;
        using var sender = SocketHopProbe.BindFreshSender(AddressFamily.InterNetwork, IPAddress.Loopback,
            destinationPort, (socket, endpoint) =>
            {
                if (++binds == 1) { rejected = socket; socket.Bind(new IPEndPoint(endpoint.Address, destinationPort)); }
                else socket.Bind(endpoint);
            });
        Assert.True(binds >= 2);
        Assert.NotEqual(destinationPort, ((IPEndPoint)sender.LocalEndPoint!).Port);
        Assert.Throws<ObjectDisposedException>(() => rejected!.Send(payload));
    }

    [Fact]
    public async Task SuccessfulUdpSendsWithoutRepliesTimeOutAndConcurrentRepliesRemainSeparate()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp,
            DestinationPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port,
            Timeout = TimeSpan.FromMilliseconds(120) };
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
            options, CancellationToken.None);
        Assert.Equal(ProbeStatus.Timeout, result.Status);
        Assert.False(result.Responded);
        // Drain the deliberately unanswered packet before testing new probes.
        await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var endpoints = new HashSet<int>();
        var echo = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var datagram = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                endpoints.Add(datagram.RemoteEndPoint.Port);
                Assert.Equal(28, datagram.Buffer.Length);
                await listener.SendAsync(datagram.Buffer, datagram.RemoteEndPoint);
            }
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
                options with { Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None)));
        await echo;
        Assert.Equal(8, endpoints.Count);
        Assert.All(results, r => { Assert.True(r.Responded, r.Detail); Assert.Contains("application datagram", r.Detail); });
    }

    [Fact]
    public async Task LateDatagramForExpiredProbeCannotCompleteANewerTtlProbe()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp,
            DestinationPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port,
            Timeout = TimeSpan.FromMilliseconds(150) };
        var transport = new AdvancedProbeTransport();
        var expired = transport.ProbeAsync(IPAddress.Loopback, 8, options, CancellationToken.None);
        var oldPacket = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProbeStatus.Timeout, (await expired).Status);
        var pending = transport.ProbeAsync(IPAddress.Loopback, 9, options, CancellationToken.None);
        var newPacket = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(oldPacket.RemoteEndPoint.Port, newPacket.RemoteEndPoint.Port);
        await listener.SendAsync(oldPacket.Buffer, oldPacket.RemoteEndPoint);
        Assert.Equal(ProbeStatus.Timeout, (await pending).Status);
    }

    [Fact]
    public async Task DestinationPortAndDuplicateRepliesRemainSpecificToEachProbe()
    {
        using var responding = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var respondingPort = ((IPEndPoint)responding.Client.LocalEndPoint!).Port;
        var silentPort = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp,
            DestinationPort = respondingPort, Timeout = TimeSpan.FromMilliseconds(250) };
        var transport = new AdvancedProbeTransport();
        var first = transport.ProbeAsync(IPAddress.Loopback, 255, options, CancellationToken.None);
        var answeredPacket = await responding.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await responding.SendAsync(answeredPacket.Buffer, answeredPacket.RemoteEndPoint);
        Assert.Equal(ProbeResponseKind.UdpReply, (await first).ResponseKind);

        var second = transport.ProbeAsync(IPAddress.Loopback, 255,
            options with { DestinationPort = silentPort }, CancellationToken.None);
        var silentPacket = await silent.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(answeredPacket.RemoteEndPoint.Port, silentPacket.RemoteEndPoint.Port);
        // Delayed duplicates addressed to the completed probe cannot answer the
        // new probe. Nor may the wrong UDP service answer its connected socket.
        await responding.SendAsync(answeredPacket.Buffer, answeredPacket.RemoteEndPoint);
        await responding.SendAsync(answeredPacket.Buffer, answeredPacket.RemoteEndPoint);
        await responding.SendAsync(silentPacket.Buffer, silentPacket.RemoteEndPoint);
        var result = await second;
        Assert.Equal(ProbeStatus.Timeout, result.Status);
        Assert.False(result.Responded);

        // Returning to the responsive port obtains a new real measurement,
        // rather than replaying its previously successful result.
        var third = transport.ProbeAsync(IPAddress.Loopback, 255, options, CancellationToken.None);
        var thirdPacket = await responding.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(answeredPacket.RemoteEndPoint.Port, thirdPacket.RemoteEndPoint.Port);
        await responding.SendAsync(thirdPacket.Buffer, thirdPacket.RemoteEndPoint);
        Assert.Equal(ProbeResponseKind.UdpReply, (await third).ResponseKind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task MacIpv4ClosedPortRequiresActualMatchedIcmp(int ttl)
    {
        if (!OperatingSystem.IsMacOS()) return;
        int port;
        using (var reserved = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)reserved.Client.LocalEndPoint!).Port;
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, ttl,
            new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = port,
                Timeout = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Assert.Equal(ProbeStatus.Success, result.Status);
        Assert.Equal(ProbeResponseKind.UdpPortUnreachable, result.ResponseKind);
        Assert.Equal(ProbeTransmission.Sent, result.Transmission);
        Assert.Contains("Matched ICMP", result.Detail);
    }

    [Fact]
    public async Task CancellingUdpProbeDoesNotLeaveOutstandingReceiveTasks()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
                new ProbeOptions { Protocol = ProbeProtocol.Udp,
                    DestinationPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port,
                    Timeout = TimeSpan.FromSeconds(30) }, cancellation.Token));
    }

    private static ProbeResult? Parse(byte[] packet, IPAddress responder, byte[] payload) =>
        SocketHopProbe.ParseIcmp(packet, responder, Target, 50001, 33434,
            ProbeProtocol.Udp, 3.5, Local, payload);

    private static byte[] Quote(byte[] payload)
    {
        var packet = new byte[56 + payload.Length];
        packet[0] = 0x45; packet[9] = 1;
        Router.GetAddressBytes().CopyTo(packet, 12); Local.GetAddressBytes().CopyTo(packet, 16);
        packet[20] = 11;
        packet[28] = 0x45; packet[37] = 17;
        Local.GetAddressBytes().CopyTo(packet, 40); Target.GetAddressBytes().CopyTo(packet, 44);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48, 2), 50001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50, 2), 33434);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(52, 2), (ushort)(payload.Length + 8));
        payload.CopyTo(packet, 56);
        return packet;
    }
}
