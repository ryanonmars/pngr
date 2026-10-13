using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TraceScope.Core;
using Xunit;
using Xunit.Abstractions;

namespace TraceScope.Core.Tests;

public sealed class SocketHopTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ProbeProtocol.Tcp)]
    [InlineData(ProbeProtocol.Udp)]
    public void IcmpQuotesMatchOnlyTheExactProtocolTargetAndPorts(ProbeProtocol protocol)
    {
        var target = IPAddress.Parse("1.1.1.1");
        var router = IPAddress.Parse("192.168.30.1");
        var packet = Quote(target, protocol);
        var response = SocketHopProbe.ParseIcmp(packet, router, target, 50123, 443, protocol, 12.345);
        Assert.Equal(ProbeStatus.TimeExceeded, response!.Status);
        Assert.Equal("192.168.30.1", response.Address);
        Assert.Equal(12.345, response.LatencyMs);
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, target, 50124, 443, protocol, 1));
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, target, 50123, 444, protocol, 1));
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, IPAddress.Parse("8.8.8.8"), 50123, 443, protocol, 1));
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, target, 50123, 443,
            protocol == ProbeProtocol.Tcp ? ProbeProtocol.Udp : ProbeProtocol.Tcp, 1));
        packet[34] = 0; packet[35] = 1; // Noninitial fragment, no transport header.
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, target, 50123, 443, protocol, 1));
    }

    [Fact]
    public void PortUnreachableFromDestinationIsARealUdpResponseButRouterDenialIsNot()
    {
        var target = IPAddress.Parse("1.1.1.1");
        var packet = Quote(target, ProbeProtocol.Udp);
        packet[20] = 3; packet[21] = 3;
        Assert.Equal(ProbeStatus.Success, SocketHopProbe.ParseIcmp(packet, target, target, 50123, 443, ProbeProtocol.Udp, 1)!.Status);
        var denied = SocketHopProbe.ParseIcmp(packet, IPAddress.Parse("192.168.30.1"), target, 50123, 443, ProbeProtocol.Udp, 1)!;
        Assert.Equal(ProbeStatus.Unreachable, denied.Status);
        Assert.Null(denied.LatencyMs);
    }

    [Fact]
    public void Ipv6QuotesAndExtensionHeadersMatchAndMalformedPacketsAreIgnored()
    {
        var target = IPAddress.Parse("2001:db8::1");
        var router = IPAddress.Parse("2001:db8::2");
        var packet = Quote(target, ProbeProtocol.Udp, ipv6Extension: true);
        Assert.Equal(ProbeStatus.TimeExceeded,
            SocketHopProbe.ParseIcmp(packet, router, target, 50123, 443, ProbeProtocol.Udp, 2)!.Status);
        for (var length = 0; length < packet.Length - 4; length++)
            Assert.Null(SocketHopProbe.ParseIcmp(packet.AsSpan(0, length), router, target, 50123, 443, ProbeProtocol.Udp, 2));
        packet[49] = 255; // Impossible extension size exceeds quoted packet.
        Assert.Null(SocketHopProbe.ParseIcmp(packet, router, target, 50123, 443, ProbeProtocol.Udp, 2));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task NativeMacTcpHopFindsLiveListenerAndClosedPortWithoutPrivileges(string address)
    {
        if (!OperatingSystem.IsMacOS() || address == "::1" && !Socket.OSSupportsIPv6) return;
        var ip = IPAddress.Parse(address);
        var listener = new TcpListener(ip, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var result = await new AdvancedProbeTransport().ProbeAsync(ip, 1,
                new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = port,
                    SourceAddress = address, Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.Responded, result.Detail);
            Assert.Equal(address, result.Address);
            Assert.Equal(ProbeStatus.Success, result.Status);
            Assert.True(result.LatencyMs >= 0);
        }
        finally { listener.Stop(); }
        var closed = await new AdvancedProbeTransport().ProbeAsync(ip, 1,
            new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = port,
                Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        Assert.True(closed.Responded, closed.Detail);
    }

    [Fact]
    public async Task MacUdpIpv6HopUsesTheSelectedLocalSourceAndActualClosedPortReply()
    {
        if (!OperatingSystem.IsMacOS() || !Socket.OSSupportsIPv6) return;
        int port;
        using (var reserved = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)))
            port = ((IPEndPoint)reserved.Client.LocalEndPoint!).Port;
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.IPv6Loopback, 1,
            new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = port, SourceAddress = "::1",
                Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        Assert.True(result.Responded, result.Detail);
        Assert.Equal("::1", result.Address);
    }

    [Fact]
    public async Task NativeHelperRejectsWrongParentBeforeProbingAndCancellationLeavesNoChild()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var helper = MacTcpHopProbe.FindHelper();
        Assert.NotNull(helper);
        using var process = new Process { StartInfo = new ProcessStartInfo(helper)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in new[] { "127.0.0.1", "1", "443", "30000", "-", "1" }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, process.ExitCode);
        var baseline = Process.GetProcessesByName("tracescope-tcp-hop").Select(p => p.Id).ToHashSet();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdvancedProbeTransport().ProbeAsync(
            IPAddress.Parse("192.0.2.1"), 64,
            new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = 443, Timeout = TimeSpan.FromSeconds(30) }, cancellation.Token));
        Assert.All(Process.GetProcessesByName("tracescope-tcp-hop"), p => Assert.Contains(p.Id, baseline));
    }

    [LiveNetworkFact]
    public async Task PublicTcpRouteDiscoversAnIntermediateRouterAndDestinationWithActualResponses()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var transport = new AdvancedProbeTransport();
        var target = IPAddress.Parse("1.1.1.1");
        var options = new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = 443,
            Timeout = TimeSpan.FromSeconds(1) };
        var hops = new List<ProbeResult>();
        for (var ttl = 1; ttl <= 12; ttl++)
        {
            var result = await transport.ProbeAsync(target, ttl, options, CancellationToken.None);
            hops.Add(result);
            output.WriteLine($"TCP443 TTL{ttl}: {result.Status}; {result.Address}; {result.LatencyMs:F3}ms");
            if (result.Status == ProbeStatus.Success) break;
            await Task.Delay(50);
        }
        Assert.Contains(hops, r => r.Status == ProbeStatus.TimeExceeded);
        Assert.Contains(hops, r => r.Status == ProbeStatus.Success && r.Address == target.ToString());
    }

    private static byte[] Quote(IPAddress destination, ProbeProtocol protocol, bool ipv6Extension = false)
    {
        var v6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        var outer = v6 ? 0 : 20;
        var inner = v6 ? 40 : 20;
        var extension = ipv6Extension ? 8 : 0;
        var buffer = new byte[outer + 8 + inner + extension + 8];
        if (!v6) { buffer[0] = 0x45; buffer[9] = 1; }
        buffer[outer] = v6 ? (byte)3 : (byte)11;
        var start = outer + 8;
        buffer[start] = v6 ? (byte)0x60 : (byte)0x45;
        buffer[start + (v6 ? 6 : 9)] = ipv6Extension ? (byte)60 : protocol == ProbeProtocol.Tcp ? (byte)6 : (byte)17;
        destination.GetAddressBytes().CopyTo(buffer, start + (v6 ? 24 : 16));
        if (ipv6Extension) buffer[start + inner] = protocol == ProbeProtocol.Tcp ? (byte)6 : (byte)17;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(start + inner + extension, 2), 50123);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(start + inner + extension + 2, 2), 443);
        return buffer;
    }

    private sealed class LiveNetworkFactAttribute : FactAttribute
    {
        public LiveNetworkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("TRACESCOPE_LIVE_NETWORK_TESTS") != "1")
                Skip = "Set TRACESCOPE_LIVE_NETWORK_TESTS=1 for public TCP route measurements.";
        }
    }
}
