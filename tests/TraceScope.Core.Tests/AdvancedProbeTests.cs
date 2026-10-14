using System.Net;
using System.Net.Sockets;
using TraceScope.Core;
using Xunit;
using Xunit.Abstractions;

namespace TraceScope.Core.Tests;

public sealed class AdvancedProbeTests
{
    private readonly ITestOutputHelper _output;
    public AdvancedProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TracerouteParserSeparatesHostPortResponsesFromBlockedPaths()
    {
        var target = IPAddress.Parse("2001:db8::10");
        var result = AdvancedProbeTransport.ParseTraceroute(" 8  2001:db8::10  12.456 ms !\n", target);
        Assert.True(result.Responded);
        Assert.Equal(ProbeStatus.Success, result.Status);
        Assert.Equal(12.456, result.LatencyMs);
        var denied = AdvancedProbeTransport.ParseTraceroute(" 8  2001:db8::1  12.456 ms !P\n", target);
        Assert.Equal(ProbeStatus.Unreachable, denied.Status);
        Assert.False(denied.Responded);
        Assert.Null(denied.LatencyMs);
        Assert.Equal(ProbeStatus.Timeout, AdvancedProbeTransport.ParseTraceroute(" 8 *\n", target).Status);
        Assert.Equal(ProbeStatus.Error, AdvancedProbeTransport.ParseTraceroute("", target, 71,
            "pcap_activate failed").Status);
    }

    [Fact]
    public async Task TcpEndpointMeasuresRealListenerAndHonorsDestinationPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var options = new ProbeOptions { Protocol = ProbeProtocol.Tcp,
                DestinationPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                Timeout = TimeSpan.FromSeconds(2), SourceAddress = "127.0.0.1" };
            var accept = listener.AcceptTcpClientAsync();
            var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
                options, CancellationToken.None);
            using var client = await accept.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.Responded, result.Detail);
            Assert.Equal(ProbeStatus.Success, result.Status);
            Assert.Equal("127.0.0.1", result.Address);
            Assert.Contains("TCP connect", result.Detail);
            Assert.True(result.LatencyMs >= 0);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task TcpClosedPortCountsARealResetAsHostResponse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
            new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = port,
                Timeout = TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 10 : 2) }, CancellationToken.None);
        Assert.True(result.Responded, result.Detail);
        Assert.Contains("TCP RST", result.Detail);
    }

    [Fact]
    public async Task UdpSendWithoutResponseIsNotSuccessAndCanBeCanceled()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var transport = new AdvancedProbeTransport();
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = port,
            Timeout = TimeSpan.FromMilliseconds(100) };
        var result = await transport.ProbeAsync(IPAddress.Loopback, 255, options, CancellationToken.None);
        Assert.Equal(ProbeStatus.Timeout, result.Status);
        Assert.Null(result.LatencyMs);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ProbeAsync(
            IPAddress.Loopback, 255, options with { Timeout = TimeSpan.FromSeconds(5) }, cancellation.Token));
    }

    [Fact]
    public async Task UdpClosedPortRequiresValidatedIcmpOrConservativeSocketFailure()
    {
        int port;
        using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
            new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = port,
                Timeout = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        // Windows may only expose a loopback socket reset, without a qualifying
        // raw ICMP quote. That rejection must remain an unanswered measurement.
        if (OperatingSystem.IsWindows() && !result.Responded)
        {
            Assert.True(result.Status is ProbeStatus.Unreachable or ProbeStatus.Timeout, result.Detail);
            Assert.Null(result.LatencyMs);
            Assert.NotEqual(ProbeTransmission.NotSent, result.Transmission);
            Assert.NotEqual(ProbeResponseKind.UdpPortUnreachable, result.ResponseKind);
            return;
        }
        Assert.True(result.Responded, result.Detail);
        Assert.Equal(ProbeResponseKind.UdpPortUnreachable, result.ResponseKind);
        Assert.Contains("Matched ICMP", result.Detail);
    }

    [Fact]
    public async Task MacUdpTracerouteUsesRealHopResponseAndFixedPort()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 1,
            new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 45999,
                Timeout = TimeSpan.FromSeconds(1) }, deadline.Token);
        Assert.True(result.Responded, result.Detail);
        Assert.Equal("127.0.0.1", result.Address);
        Assert.Equal(ProbeStatus.Success, result.Status);
    }

    [Fact]
    public async Task InvalidSourceDoesNotSilentlyUseDefaultRoute()
    {
        var result = await new AdvancedProbeTransport().ProbeAsync(IPAddress.Loopback, 255,
            new ProbeOptions { Protocol = ProbeProtocol.Tcp, SourceAddress = "::1" }, CancellationToken.None);
        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.False(result.Responded);
        Assert.Contains("matching", result.Detail);
    }

    [Fact]
    public async Task Ipv6TcpEndpointAndIcmpLoopbackWorkWhenIpv6IsAvailable()
    {
        if (!Socket.OSSupportsIPv6) return;
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            var transport = new AdvancedProbeTransport();
            var accept = listener.AcceptTcpClientAsync();
            var tcp = await transport.ProbeAsync(IPAddress.IPv6Loopback, 255,
                new ProbeOptions { Protocol = ProbeProtocol.Tcp,
                    DestinationPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                    Timeout = TimeSpan.FromSeconds(2), SourceAddress = "::1" }, CancellationToken.None);
            using var client = await accept.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(tcp.Responded, tcp.Detail);
            Assert.Equal("::1", tcp.Address);
            var icmp = await transport.ProbeAsync(IPAddress.IPv6Loopback, 1,
                new ProbeOptions { Timeout = TimeSpan.FromSeconds(1) }, CancellationToken.None);
            Assert.True(icmp.Responded, icmp.Detail);
            Assert.Equal("::1", icmp.Address);
        }
        finally { listener.Stop(); }
    }

    [LiveNetworkFact]
    public async Task PublicTcp443AndUdpGatewayPreserveActualOutcomes()
    {
        var transport = new AdvancedProbeTransport();
        var target = IPAddress.Parse("1.1.1.1");
        var options = new ProbeOptions { Protocol = ProbeProtocol.Tcp, DestinationPort = 443,
            Timeout = TimeSpan.FromSeconds(3) };
        var tcp = await transport.ProbeAsync(target, 255, options, CancellationToken.None);
        _output.WriteLine($"Public TCP443: {tcp.Status}; {tcp.Address}; {tcp.LatencyMs:F3} ms; {tcp.Detail}");
        Assert.True(tcp.Responded, tcp.Detail);
        if (!OperatingSystem.IsMacOS()) return;
        var udp = await transport.ProbeAsync(target, 1,
            options with { Protocol = ProbeProtocol.Udp, DestinationPort = 33434 }, CancellationToken.None);
        _output.WriteLine($"Public UDP TTL1: {udp.Status}; {udp.Address}; {udp.LatencyMs:F3} ms; {udp.Detail}");
        // Intermediate routers may legitimately rate-limit diagnostics. A silent
        // gateway must remain a timeout, rather than be manufactured into success.
        Assert.Contains(udp.Status, new[] { ProbeStatus.TimeExceeded, ProbeStatus.Timeout, ProbeStatus.Unreachable });
        if (udp.Responded) Assert.Equal(ProbeResponseKind.IcmpTimeExceeded, udp.ResponseKind);
        else Assert.Null(udp.LatencyMs);
        await Task.Delay(1200); // Do not consume the gateway's ICMP error rate limit back-to-back.
        var tcpHop = await transport.ProbeAsync(target, 1, options, CancellationToken.None);
        _output.WriteLine($"Public TCP TTL1: {tcpHop.Status}; {tcpHop.Address}; {tcpHop.LatencyMs:F3} ms; {tcpHop.Detail}");
        // Routers may suppress ICMP responses to TCP SYN packets.
        Assert.Contains(tcpHop.Status, new[] { ProbeStatus.TimeExceeded, ProbeStatus.Timeout });
    }

    private sealed class LiveNetworkFactAttribute : FactAttribute
    {
        public LiveNetworkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("TRACESCOPE_LIVE_NETWORK_TESTS") != "1")
                Skip = "Set TRACESCOPE_LIVE_NETWORK_TESTS=1 to run probes against a public target.";
        }
    }
}
