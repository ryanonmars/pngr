using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class WindowsTcpHopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RouterQuoteMustMatchOriginalSourceDestinationAndTcpPorts(bool ipv6)
    {
        var source = IPAddress.Parse(ipv6 ? "2001:db8::10" : "192.0.2.10");
        var destination = IPAddress.Parse(ipv6 ? "2001:db8::20" : "198.51.100.20");
        var router = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1");
        var packet = Quote(source, destination, router);
        var reply = WindowsTcpHopProbe.ParseIcmp(packet, router, source, destination, 51001, 443, 12.5);
        Assert.NotNull(reply);
        Assert.Equal(ProbeStatus.TimeExceeded, reply.Status);
        Assert.Equal(router.ToString(), reply.Address);
        Assert.Equal(ProbeTransmission.Sent, reply.Transmission);
        Assert.Equal(ProbeResponseKind.IcmpTimeExceeded, reply.ResponseKind);
        Assert.Equal(12.5, reply.LatencyMs);
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, router, source, destination, 51002, 443, 12.5));
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, router, source, destination, 51001, 444, 12.5));
        var other = IPAddress.Parse(ipv6 ? "2001:db8::99" : "203.0.113.99");
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, router, source, other, 51001, 443, 12.5));
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, router, other, destination, 51001, 443, 12.5));
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, other, source, destination, 51001, 443, 12.5));
        var originalSourceOffset = (ipv6 ? 40 : 20) + 8 + (ipv6 ? 8 : 12);
        packet[originalSourceOffset] ^= 1;
        Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet, router, source, destination, 51001, 443, 12.5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitNetworkRejectionNeverBecomesDestinationSuccess(bool ipv6)
    {
        var source = IPAddress.Parse(ipv6 ? "2001:db8::10" : "192.0.2.10");
        var target = IPAddress.Parse(ipv6 ? "2001:db8::20" : "198.51.100.20");
        var packet = Quote(source, target, target);
        var offset = ipv6 ? 40 : 20;
        packet[offset] = ipv6 ? (byte)1 : (byte)3;
        packet[offset + 1] = ipv6 ? (byte)4 : (byte)3;
        var reply = WindowsTcpHopProbe.ParseIcmp(packet, target, source, target, 51001, 443, 12.5);
        Assert.Equal(ProbeStatus.Unreachable, reply!.Status);
        Assert.False(reply.Responded);
        Assert.Equal(ProbeResponseKind.NetworkRejection, reply.ResponseKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedOrIncompleteQuotesNeverProduceMeasurements(bool ipv6)
    {
        var source = IPAddress.Parse(ipv6 ? "2001:db8::10" : "192.0.2.10");
        var destination = IPAddress.Parse(ipv6 ? "2001:db8::20" : "198.51.100.20");
        var router = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1");
        var packet = Quote(source, destination, router);
        var minimum = (ipv6 ? 40 : 20) * 2 + 8 + 4;
        for (var length = 0; length < minimum; length++)
            Assert.Null(WindowsTcpHopProbe.ParseIcmp(packet.AsSpan(0, length), router, source, destination, 51001, 443, 12.5));
    }

    [Fact]
    public void PermissionFailureExplainsTheLimitationAndDoesNotPromiseHopDiscovery()
    {
        var detail = WindowsTcpHopProbe.CapabilityFailure(SocketError.AccessDenied);
        Assert.Contains("administrator", detail);
        Assert.Contains("endpoint monitoring remains available", detail);
        Assert.Contains("No driver or service", detail);
        Assert.Contains("unavailable", WindowsTcpHopProbe.CapabilityFailure(SocketError.ProtocolNotSupported));
    }

    [Fact]
    public async Task WrongPlatformDoesNotTransmitOrStartAnyHelper()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await WindowsTcpHopProbe.ProbeAsync(IPAddress.Loopback, 1,
            new ProbeOptions { Protocol = ProbeProtocol.Tcp }, CancellationToken.None);
        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(ProbeTransmission.NotSent, result.Transmission);
        Assert.False(result.Responded);
    }

    [Theory]
    [InlineData(ProbeProtocol.Tcp)]
    [InlineData(ProbeProtocol.Udp)]
    public void WindowsCapabilityPolicyRetainsEndpointFallbackAndDistinguishesPrivileges(ProbeProtocol protocol)
    {
        var denied = ProtocolCapabilities.WindowsPolicy(protocol, SocketError.AccessDenied);
        Assert.False(denied.HopDiscoveryAvailable);
        Assert.True(denied.EndpointMonitoringAvailable);
        Assert.True(denied.RequiresElevation);
        var unsupported = ProtocolCapabilities.WindowsPolicy(protocol, SocketError.ProtocolNotSupported);
        Assert.False(unsupported.HopDiscoveryAvailable);
        Assert.True(unsupported.EndpointMonitoringAvailable);
        Assert.False(unsupported.RequiresElevation);
        var available = ProtocolCapabilities.WindowsPolicy(protocol, null);
        Assert.True(available.HopDiscoveryAvailable);
        Assert.True(available.EndpointMonitoringAvailable);
        Assert.Contains("filtering", available.Detail);
    }

    [Fact]
    public void UnsupportedAddressFamilyNeverClaimsNetworkCapability()
    {
        var result = ProtocolCapabilities.Get(ProbeProtocol.Tcp, AddressFamily.Unix);
        Assert.False(result.HopDiscoveryAvailable);
        Assert.False(result.EndpointMonitoringAvailable);
    }

    [Theory]
    [InlineData(SocketError.NetworkUnreachable, false, ProbeStatus.Unreachable)]
    [InlineData(SocketError.HostUnreachable, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.NetworkDown, false, ProbeStatus.Unreachable)]
    [InlineData(SocketError.ConnectionRefused, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.ConnectionReset, true, ProbeStatus.Unreachable)]
    [InlineData(SocketError.TimedOut, false, ProbeStatus.Timeout)]
    [InlineData(SocketError.TimedOut, true, ProbeStatus.Timeout)]
    [InlineData(SocketError.AccessDenied, false, ProbeStatus.Error)]
    [InlineData(SocketError.AddressNotAvailable, false, ProbeStatus.Error)]
    [InlineData(SocketError.OperationAborted, true, ProbeStatus.Timeout)]
    public void TcpSocketFailuresPreserveNetworkFailuresAndSeparateUnsentLocalErrors(
        SocketError error, bool transmitted, ProbeStatus expected)
    {
        var result = WindowsTcpHopProbe.SocketFailure(error, transmitted, "diagnostic");
        Assert.Equal(expected, result.Status);
        Assert.False(result.Responded);
        Assert.Null(result.LatencyMs);
        Assert.Equal(transmitted ? ProbeTransmission.Sent : ProbeTransmission.NotSent, result.Transmission);
        Assert.Contains(error.ToString(), result.Detail);
        Assert.Contains("diagnostic", result.Detail);
        Assert.Equal(expected == ProbeStatus.Unreachable ? ProbeResponseKind.NetworkRejection : ProbeResponseKind.None,
            result.ResponseKind);
    }

    private static byte[] Quote(IPAddress source, IPAddress destination, IPAddress responder)
    {
        var ipv6 = source.AddressFamily == AddressFamily.InterNetworkV6;
        var header = ipv6 ? 40 : 20;
        var packet = new byte[header * 2 + 8 + 8];
        packet[0] = ipv6 ? (byte)0x60 : (byte)0x45;
        packet[ipv6 ? 6 : 9] = ipv6 ? (byte)58 : (byte)1;
        responder.GetAddressBytes().CopyTo(packet, ipv6 ? 8 : 12);
        source.GetAddressBytes().CopyTo(packet, ipv6 ? 24 : 16);
        packet[header] = ipv6 ? (byte)3 : (byte)11;
        var quoted = header + 8;
        packet[quoted] = ipv6 ? (byte)0x60 : (byte)0x45;
        packet[quoted + (ipv6 ? 6 : 9)] = 6;
        source.GetAddressBytes().CopyTo(packet, quoted + (ipv6 ? 8 : 12));
        destination.GetAddressBytes().CopyTo(packet, quoted + (ipv6 ? 24 : 16));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(quoted + header, 2), 51001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(quoted + header + 2, 2), 443);
        return packet;
    }
}
