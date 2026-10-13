using System.Net;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class WebDiscoveryTests
{
    private static string Database() => Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid() + ".db");

    [Fact]
    public async Task ReadOnlyLoopbackServerExposesActualTargetsHistoryAndInterfaces()
    {
        await using var engine = new TraceEngine(Database(), new ProbeOptions
        { Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100),
            MaxHops = 2, ResolveHostnames = false }, new MeasuredTransport());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotUpdated += (_, snapshot) =>
        { if (snapshot.Round >= 3) { engine.Pause(snapshot.SessionId); ready.TrySetResult(); } };
        var session = await engine.StartTargetAsync("127.0.0.1");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var monitoringEvent = new MonitoringEvent(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow,
            "RouteChanged", "Route changed to measured gateway");
        await engine.Storage.AppendEventAsync(monitoringEvent);
        await using var web = new LocalWebServer(engine);
        await web.StartAsync(0);
        Assert.NotNull(web.Url);
        Assert.Equal("127.0.0.1", new Uri(web.Url!).Host);
        using var client = new HttpClient { BaseAddress = new Uri(web.Url!) };
        using var targets = JsonDocument.Parse(await client.GetStringAsync("/api/targets"));
        var target = Assert.Single(targets.RootElement.EnumerateArray());
        Assert.Equal(session.Id.ToString(), target.GetProperty("id").GetString());
        Assert.Equal(9, target.GetProperty("snapshot").GetProperty("destination").GetProperty("averageMs").GetDouble());
        Assert.Equal(0, target.GetProperty("snapshot").GetProperty("destination").GetProperty("lossPercent").GetDouble());
        Assert.False(target.GetProperty("snapshot").GetProperty("destination").TryGetProperty("history", out _));
        using var history = JsonDocument.Parse(await client.GetStringAsync($"/api/history/{session.Id}?hop=0&maxPoints=2"));
        Assert.True(history.RootElement.GetProperty("sampleCount").GetInt32() >= 3);
        Assert.InRange(history.RootElement.GetProperty("points").GetArrayLength(), 1, 2);
        Assert.All(history.RootElement.GetProperty("points").EnumerateArray(), point =>
            Assert.Equal(9, point.GetProperty("averageMs").GetDouble()));
        Assert.Equal(monitoringEvent.Id.ToString(), Assert.Single(history.RootElement.GetProperty("events")
            .EnumerateArray()).GetProperty("id").GetString());
        using var events = JsonDocument.Parse(await client.GetStringAsync($"/api/events/{session.Id}"));
        Assert.Equal("RouteChanged", Assert.Single(events.RootElement.EnumerateArray()).GetProperty("kind").GetString());
        Assert.Contains("LOCAL MONITOR", await client.GetStringAsync("/"));
        using var interfaces = JsonDocument.Parse(await client.GetStringAsync("/api/interfaces"));
        Assert.Equal(JsonValueKind.Array, interfaces.RootElement.ValueKind);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/targets", null)).StatusCode);
    }

    [Fact]
    public async Task ServerRejectsInvalidHistoryParametersUnknownIdsAndOtherHostHeaders()
    {
        await using var engine = new TraceEngine(Database());
        await using var web = new LocalWebServer(engine);
        await web.StartAsync(0);
        using var client = new HttpClient { BaseAddress = new Uri(web.Url!) };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/history/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/events/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/events/{Guid.NewGuid()}?from=invalid")).StatusCode);
        foreach (var query in new[] { "hop=-1", "hop=65", "hop=abc", "maxPoints=0", "maxPoints=10001", "from=invalid", "from=2026-02-01&to=2026-01-01" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/history/{Guid.NewGuid()}?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/history/invalid")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/targets");
        request.Headers.Host = "example.com";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        await web.DisposeAsync();
        Assert.Null(web.Url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/targets"));
    }

    [Fact]
    public void NeighborParsingSupportsMacWindowsAndLinuxCachesWithoutScanning()
    {
        var mac = NetworkDiscovery.ParseNeighbors("? (192.168.1.1) at aa:bb:cc:dd:ee:ff on en0 ifscope [ethernet]\n? (192.168.1.2) at (incomplete) on en0 ifscope [ethernet]");
        Assert.Equal(2, mac.Count);
        Assert.Equal("en0", mac[0].Interface);
        Assert.Null(mac[1].MacAddress);
        Assert.Equal("Incomplete", mac[1].State);
        var windows = Assert.Single(NetworkDiscovery.ParseNeighbors("Interface: 192.168.1.8 --- 0x9\n  Internet Address      Physical Address      Type\n  192.168.1.1           aa-bb-cc-dd-ee-ff      dynamic"));
        Assert.Equal("192.168.1.8", windows.Interface);
        Assert.Equal("aa-bb-cc-dd-ee-ff", windows.MacAddress);
        var linux = Assert.Single(NetworkDiscovery.ParseNeighbors("fe80::1 dev eth0 lladdr aa:bb:cc:dd:ee:ff REACHABLE"));
        Assert.Equal("fe80::1", linux.Address);
        Assert.Equal("eth0", linux.Interface);
    }

    [Fact]
    public async Task LiveLocalDiscoveryReadsInterfaceGatewayAndNeighborCache()
    {
        var interfaces = NetworkDiscovery.GetInterfaces();
        Assert.NotEmpty(interfaces);
        Assert.All(interfaces, i => Assert.All(i.Addresses, address => Assert.True(IPAddress.TryParse(address, out _))));
        var gateway = await NetworkDiscovery.GetLocalGatewayAsync();
        if (gateway is not null) Assert.True(IPAddress.TryParse(gateway, out _));
        var neighbors = await NetworkDiscovery.LocalNeighborsAsync();
        Assert.All(neighbors, neighbor => Assert.True(IPAddress.TryParse(neighbor.Address, out _)));
    }

    private sealed class MeasuredTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 9, ProbeStatus.Success));
    }
}
