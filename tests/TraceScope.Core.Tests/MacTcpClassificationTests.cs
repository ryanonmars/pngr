using System.Net;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class MacTcpClassificationTests
{
    [Theory]
    [InlineData("TCP_SOCKET_REJECTION 61 1", ProbeStatus.Unreachable, ProbeTransmission.Sent)]
    [InlineData("TCP_SOCKET_REJECTION 54 1", ProbeStatus.Unreachable, ProbeTransmission.Sent)]
    [InlineData("TCP_SOCKET_REJECTION 51 0", ProbeStatus.Unreachable, ProbeTransmission.NotSent)]
    [InlineData("TCP_SOCKET_REJECTION 60 1", ProbeStatus.Timeout, ProbeTransmission.Sent)]
    public void UnverifiedRemoteSocketErrorsNeverInventDestinationSuccess(string error,
        ProbeStatus status, ProbeTransmission transmission)
    {
        var result = MacTcpHopProbe.ParseHelperResult("8 *\n", IPAddress.Parse("1.1.1.1"), 0, error);
        Assert.Equal(status, result.Status);
        Assert.Equal(transmission, result.Transmission);
        Assert.False(result.Responded);
        Assert.Null(result.LatencyMs);
        Assert.Contains("no independently validated", result.Detail);
    }

    [Fact]
    public void MatchedRouterResponseAndValidatedConnectRetainLatency()
    {
        var target = IPAddress.Parse("1.1.1.1");
        var router = MacTcpHopProbe.ParseHelperResult("5 192.0.2.1 12.345 ms\n", target, 0, "TCP_ICMP_TIME_EXCEEDED\n");
        Assert.Equal(ProbeStatus.TimeExceeded, router.Status);
        Assert.Equal(ProbeResponseKind.IcmpTimeExceeded, router.ResponseKind);
        Assert.Equal(12.345, router.LatencyMs);
        var destination = MacTcpHopProbe.ParseHelperResult("8 1.1.1.1 17.234 ms\n", target, 0, "TCP_CONNECTED\n");
        Assert.Equal(ProbeStatus.Success, destination.Status);
        Assert.Equal(ProbeResponseKind.TcpConnected, destination.ResponseKind);
        Assert.Equal(17.234, destination.LatencyMs);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void LoopbackClosedPortEvidenceIsExplicitlyScoped(string address)
    {
        var result = MacTcpHopProbe.ParseHelperResult($"1 {address} 0.1 ms\n", IPAddress.Parse(address), 0, "TCP_RESET_LOOPBACK\n");
        Assert.Equal(ProbeStatus.Success, result.Status);
        Assert.Equal(ProbeResponseKind.TcpReset, result.ResponseKind);
        Assert.True(result.Responded);
        var remote = MacTcpHopProbe.ParseHelperResult("8 1.1.1.1 17 ms\n", IPAddress.Parse("1.1.1.1"), 0, "TCP_RESET_LOOPBACK\n");
        Assert.False(remote.Responded);
    }

    [Fact]
    public void UnmarkedLegacyDestinationResultsAreNotTrusted()
    {
        var result = MacTcpHopProbe.ParseHelperResult("8 1.1.1.1 17 ms\n", IPAddress.Parse("1.1.1.1"), 0, "");
        Assert.Equal(ProbeStatus.Unreachable, result.Status);
        Assert.False(result.Responded);
    }
}
