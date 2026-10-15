using System.Net;
using System.Net.NetworkInformation;
using TraceScope.Core;
using Xunit;
using Interface = TraceScope.Core.WebServerNetwork.InterfaceSnapshot;
using Route = TraceScope.Core.WebServerNetwork.DefaultRoute;

namespace TraceScope.Core.Tests;

public sealed class WebAutomaticNetworkTests
{
    private static Interface Network(string name, string address, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool up = true, string? description = null) => new(name, name, description ?? name, type, up, [IPAddress.Parse(address)]);

    [Theory]
    [InlineData("en5", "192.168.30.3")]
    [InlineData("en0", "192.168.30.2")]
    public void DefaultRouteSelectsEthernetOrWifiWithoutHardCodedPriority(string device, string expected)
    {
        Interface[] networks = [Network("en0", "192.168.30.2", NetworkInterfaceType.Wireless80211), Network("en5", "192.168.30.3")];
        var selected = WebServerNetwork.SelectAutomaticLanAddress(networks, new Route(InterfaceId: device));
        Assert.Equal(expected, selected?.Address);
        Assert.Equal(device, selected?.InterfaceId);
        Assert.True(selected?.IsAutomaticCandidate);
        Assert.DoesNotContain(device, selected!.InterfaceName);
        Assert.Equal(device == "en0" ? "Wi-Fi" : "Ethernet", selected.InterfaceName);
    }

    [Theory]
    [InlineData("utun10", "utun10", NetworkInterfaceType.Ethernet)]
    [InlineData("tun0", "tun0", NetworkInterfaceType.Tunnel)]
    [InlineData("docker0", "docker0", NetworkInterfaceType.Ethernet)]
    [InlineData("br-123", "bridge", NetworkInterfaceType.Ethernet)]
    [InlineData("custom", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("custom", "WireGuard Tunnel", NetworkInterfaceType.Ethernet)]
    [InlineData("custom", "Tailscale", NetworkInterfaceType.Ethernet)]
    [InlineData("vnic0", "Parallels Shared Networking", NetworkInterfaceType.Ethernet)]
    [InlineData("custom", "vmxnet3 Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("Loopback", "Loopback", NetworkInterfaceType.Loopback)]
    public void UnsafeDefaultInterfaceNeverFallsBackToAnotherAdapter(string name, string description, NetworkInterfaceType type)
    {
        Interface[] networks = [Network("en0", "192.168.30.2"), Network(name, "10.1.2.3", type, description: description)];
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress(networks, new Route(InterfaceId: name)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.1.2")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("fd00::2")]
    [InlineData("fe80::2")]
    public void AutomaticSelectionRequiresPrivateIpv4(string address) => Assert.Null(
        WebServerNetwork.SelectAutomaticLanAddress([Network("en0", address)], new Route(InterfaceId: "en0")));

    [Fact]
    public void MissingDownOrAmbiguousInterfaceDoesNotChooseArbitraryAddress()
    {
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress([Network("en0", "10.0.0.2")], null));
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress([Network("en0", "10.0.0.2")], new Route()));
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress([Network("en0", "10.0.0.2")], new Route(InterfaceId: "en5")));
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress([Network("en0", "10.0.0.2", up: false)], new Route(InterfaceId: "en0")));
        var multiAddress = Network("en0", "10.0.0.2") with { Addresses = [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.1.0.2")] };
        Assert.Null(WebServerNetwork.SelectAutomaticLanAddress([multiAddress], new Route(InterfaceId: "en0")));
        Assert.Equal("10.1.0.2", WebServerNetwork.SelectAutomaticLanAddress([multiAddress], new Route(Address: "10.1.0.2"))?.Address);
    }

    [Fact]
    public void DualStackInterfacePrefersItsPrivateIpv4Address()
    {
        var network = Network("en0", "192.168.30.2") with { Addresses = [IPAddress.Parse("fd00::2"),
            IPAddress.Parse("fe80::2"), IPAddress.Parse("2001:db8::2"), IPAddress.Parse("192.168.30.2")] };
        Assert.Equal("192.168.30.2", WebServerNetwork.SelectAutomaticLanAddress([network], new Route(InterfaceId: "en0"))?.Address);
    }

    [Fact]
    public void ManualPrivateAddressesRemainAvailableWithoutAutomaticEligibility()
    {
        Interface[] networks = [Network("custom", "10.2.3.4", description: "Virtual adapter"), Network("en0", "fd00::2"), Network("utun0", "10.9.0.2", NetworkInterfaceType.Tunnel)];
        var manual = WebServerNetwork.GetLanAddresses(networks);
        Assert.Equal(2, manual.Count);
        Assert.All(manual, address => Assert.False(address.IsAutomaticCandidate));
        var options = new WebServerOptions { LanEnabled = true, LanAddress = "10.2.3.4", HttpAcknowledged = true,
            AccessKeyVerifier = WebAccessKey.CreateVerifier(WebAccessKey.Generate()) };
        Assert.Equal(IPAddress.Parse("10.2.3.4"), WebServerNetwork.ValidateLanAddress(options, manual));
        Assert.Throws<InvalidOperationException>(() => WebServerNetwork.ValidateLanAddress(options, []));
    }

    [Fact]
    public void MacDefaultRouteUsesReportedInterfaceAndRejectsMissingOrAmbiguousOutput()
    {
        Assert.Equal("en5", WebServerNetwork.ParseMacDefaultRoute("route to: default\n gateway: 192.168.30.1\n interface: en5\n flags: <UP,GATEWAY>")?.InterfaceId);
        Assert.Null(WebServerNetwork.ParseMacDefaultRoute("route: writing to routing socket: not in table"));
        Assert.Null(WebServerNetwork.ParseMacDefaultRoute("interface: en0\ninterface: en5"));
    }

    [Fact]
    public void WindowsDefaultRouteUsesLowestMetricActiveRowsRegardlessOfLocalizedHeading()
    {
        const string table = "Aktive Routen:\n 0.0.0.0 0.0.0.0 192.168.30.1 192.168.30.2 80\n 0.0.0.0 0.0.0.0 192.168.30.1 192.168.30.3 25\nPersistente Routen:\n0.0.0.0 0.0.0.0 10.0.0.1 1";
        var route = WebServerNetwork.ParseWindowsDefaultRoute(table);
        Assert.Equal("192.168.30.3", route?.Address);
        Assert.Equal("Ethernet", WebServerNetwork.SelectAutomaticLanAddress([
            Network("Wi-Fi", "192.168.30.2", NetworkInterfaceType.Wireless80211), Network("Ethernet", "192.168.30.3")], route)?.InterfaceName);
        Assert.Null(WebServerNetwork.ParseWindowsDefaultRoute("0.0.0.0 0.0.0.0 10.0.0.1 10.0.0.2 25\n0.0.0.0 0.0.0.0 10.1.0.1 10.1.0.2 25"));
        Assert.Null(WebServerNetwork.ParseWindowsDefaultRoute("0.0.0.0 0.0.0.0 10.0.0.1 1"));
    }

    [Fact]
    public void LinuxDefaultRouteRequiresUpFlagDefaultMaskAndLowestMetric()
    {
        Assert.Equal("eth0", WebServerNetwork.ParseLinuxDefaultRoute("Iface Destination Gateway Flags RefCnt Use Metric Mask\nwlan0 00000000 0100000A 0003 0 0 600 00000000\neth0 00000000 0100000A 0003 0 0 100 00000000\ntun0 00000000 0100000A 0000 0 0 1 00000000")?.InterfaceId);
        Assert.Null(WebServerNetwork.ParseLinuxDefaultRoute("eth0 00000000 0100000A 0000 0 0 1 00000000"));
    }

    [Fact]
    public async Task RealPlatformDiscoveryNeverReturnsWildcardOrPublicAddress()
    {
        var selected = await WebServerNetwork.GetAutomaticLanAddressAsync();
        if (selected is not null)
        {
            Assert.True(selected.IsAutomaticCandidate);
            Assert.True(WebServerNetwork.IsPrivateAddress(IPAddress.Parse(selected.Address)));
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, IPAddress.Parse(selected.Address).AddressFamily);
            Assert.Contains(WebServerNetwork.GetLanAddresses(), candidate => candidate.Address == selected.Address);
        }
    }
}
