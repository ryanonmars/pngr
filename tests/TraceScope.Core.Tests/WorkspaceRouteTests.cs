using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class WorkspaceRouteTests
{
    private static string WorkspacePath() => Path.Combine(
        AppContext.BaseDirectory, "test-data", "workspaces", Guid.NewGuid() + ".json");

    [Fact]
    public async Task WorkspaceSaveLoadAndStreamImportPreserveTargetProfileAndAlertSettings()
    {
        var options = new ProbeOptions
        {
            Protocol = ProbeProtocol.Tcp, DestinationPort = 8443, SourceAddress = "192.0.2.20",
            InterfaceId = "en7", Interval = TimeSpan.FromSeconds(3), Timeout = TimeSpan.FromSeconds(2),
            MaxHops = 24, HistoryLimit = 500, Concurrency = 4, ResolveHostnames = false
        };
        var configuration = new WorkspaceConfiguration
        {
            Name = "Office WAN", RestoreOnStartup = false, RetentionDays = 120,
            AggregationSeconds = 30, WebEnabled = true, WebPort = 9876, DesktopNotifications = true,
            Targets = [new("example.test", options, "Office", true),
                new("2001:db8::1", options with { Protocol = ProbeProtocol.Udp,
                    DestinationPort = 33434, SourceAddress = "2001:db8::20" }, "IPv6")],
            Profiles = [new("TLS checks", options)],
            Alerts = [new AlertRule { Id = "latency-office", Name = "WAN latency",
                Condition = AlertCondition.Latency, Threshold = 150,
                EvaluationPeriod = TimeSpan.FromSeconds(45), Cooldown = TimeSpan.FromMinutes(3),
                RecoveryPeriod = TimeSpan.FromSeconds(20), Target = "example.test", Group = "Office" }]
        };
        var path = WorkspacePath();
        var store = new WorkspaceStore(path);
        await store.SaveAsync(configuration);
        var restored = await new WorkspaceStore(path).LoadAsync();
        Assert.NotNull(restored);
        Assert.Equal("Office WAN", restored.Name);
        Assert.False(restored.RestoreOnStartup);
        Assert.Equal(120, restored.RetentionDays);
        Assert.Equal(30, restored.AggregationSeconds);
        Assert.True(restored.WebEnabled);
        Assert.True(restored.DesktopNotifications);
        Assert.Equal(9876, restored.WebPort);
        Assert.Equal(configuration.Targets, restored.Targets);
        Assert.Equal(configuration.Profiles, restored.Profiles);
        Assert.Equal(configuration.Alerts, restored.Alerts);
        Assert.False(File.Exists(path + ".tmp"));
        await using var exported = File.OpenRead(path);
        var imported = await WorkspaceStore.ImportAsync(exported);
        Assert.Equal(restored.Targets, imported.Targets);
        Assert.Equal(restored.Alerts, imported.Alerts);
    }

    [Fact]
    public async Task OldMinimalWorkspaceGetsSafeDefaultsAndMissingFileIsEmpty()
    {
        Assert.Null(await new WorkspaceStore(WorkspacePath()).LoadAsync());
        await using var minimal = Json("{\"targets\":[{\"target\":\"1.1.1.1\",\"options\":{}}]}");
        var workspace = await WorkspaceStore.ImportAsync(minimal);
        Assert.True(workspace.RestoreOnStartup);
        Assert.False(workspace.WebEnabled);
        Assert.Equal(30, workspace.RetentionDays);
        Assert.Equal(5, workspace.AggregationSeconds);
        Assert.Equal(8787, workspace.WebPort);
        Assert.Empty(workspace.Profiles);
        Assert.Empty(workspace.Alerts);
        var target = Assert.Single(workspace.Targets);
        Assert.Equal("Default", target.Group);
        Assert.False(target.Paused);
        Assert.Equal(ProbeProtocol.Icmp, target.Options.Protocol);
        Assert.Equal(443, target.Options.DestinationPort);
        Assert.Equal(TimeSpan.FromSeconds(2.5), target.Options.Interval);
        Assert.Null(target.Options.SourceAddress);
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"retentionDays\":0}")]
    [InlineData("{\"aggregationSeconds\":0}")]
    [InlineData("{\"webPort\":80}")]
    [InlineData("{\"targets\":[{\"target\":\" \",\"options\":{}}]}")]
    public async Task ImportRejectsInvalidConfigurationValues(string json)
    {
        await using var stream = Json(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.ImportAsync(stream));
    }

    [Theory]
    [InlineData("{invalid-json")]
    [InlineData("{\"targets\":[{\"target\":\"1.1.1.1\",\"options\":{\"protocol\":\"bogus\"}}]}")]
    public async Task ImportRejectsMalformedJsonAndUnknownProtocolNames(string json)
    {
        await using var stream = Json(json);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => WorkspaceStore.ImportAsync(stream));
    }

    [Theory]
    [InlineData("{\"targets\":null}")]
    [InlineData("{\"profiles\":null}")]
    [InlineData("{\"alerts\":null}")]
    [InlineData("{\"profiles\":[null]}")]
    [InlineData("{\"profiles\":[{\"name\":\"Example\",\"options\":null}]}")]
    [InlineData("{\"alerts\":[null]}")]
    [InlineData("{\"targets\":[null]}")]
    [InlineData("{\"targets\":[{\"target\":\"1.1.1.1\",\"options\":null}]}")]
    [InlineData("{\"targets\":[{\"target\":\"1.1.1.1\",\"options\":{},\"group\":null}]}")]
    public async Task ImportRejectsNullConfigurationMembersWithUsefulValidationError(string json)
    {
        await using var stream = Json(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.ImportAsync(stream));
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedWithoutReplacingExistingWorkspace()
    {
        var path = WorkspacePath();
        var store = new WorkspaceStore(path);
        await store.SaveAsync(new WorkspaceConfiguration { Name = "Valid" });
        var invalid = new WorkspaceConfiguration
        {
            Targets = Enumerable.Range(0, 501).Select(i => new WorkspaceTarget($"host{i}.test", new())).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        Assert.Equal("Valid", (await store.LoadAsync())!.Name);
        var oversizedGroup = new WorkspaceConfiguration
        { Targets = [new("1.1.1.1", new(), new string('x', 81))] };
        Assert.Throws<InvalidDataException>(oversizedGroup.Validate);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesStayAtomicForConcurrentReaders()
    {
        var store = new WorkspaceStore(WorkspacePath());
        var otherStore = new WorkspaceStore(store.Path);
        await store.SaveAsync(new WorkspaceConfiguration { Name = "Save 0" });
        var writes = Enumerable.Range(1, 25).Select(i => (i % 2 == 0 ? store : otherStore).SaveAsync(new WorkspaceConfiguration
        {
            Name = $"Save {i}", Targets = [new("1.1.1.1", new(), $"Group {i}")]
        })).ToArray();
        for (var read = 0; read < 30; read++)
        {
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.StartsWith("Save ", loaded.Name);
            loaded.Validate();
        }
        await Task.WhenAll(writes);
        var last = await store.LoadAsync();
        Assert.NotNull(last);
        Assert.Single(last.Targets);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task ImportRejectsOversizedStreamsBeforeJsonParsing()
    {
        await using var stream = Json("{" + new string(' ', 4 * 1024 * 1024) + "}");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.ImportAsync(stream));
        Assert.Contains("4 MiB", error.Message);
    }

    [Fact]
    public void RouterSilenceAndCapabilityErrorsDoNotProduceRouteChanges()
    {
        var tracker = new RouteTracker();
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.1"), Hop(2, "192.0.2.2")]));
        Assert.Null(tracker.Observe([Hop(1, null, ProbeStatus.Timeout), Hop(2, "192.0.2.2")]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.99", ProbeStatus.Error)]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.99", ProbeStatus.Unreachable)]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.1"), Hop(2, "192.0.2.2")]));
    }

    [Fact]
    public void ChangedRouterRequiresTwoRespondingRoundsAndResetRelearnsBaseline()
    {
        var tracker = new RouteTracker();
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.1")]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.3")]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.1")])); // Cancels a transient candidate.
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.3")]));
        Assert.Null(tracker.Observe([Hop(1, null, ProbeStatus.Timeout)]));
        var change = tracker.Observe([Hop(1, "192.0.2.3")]);
        Assert.Equal("Hop 1: 192.0.2.1 → 192.0.2.3", change);
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.3")]));
        tracker.Reset();
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.50")]));
        Assert.Null(tracker.Observe([Hop(1, "192.0.2.50")]));
    }

    private static MemoryStream Json(string text) => new(Encoding.UTF8.GetBytes(text));
    private static HopSnapshot Hop(int hop, string? address, ProbeStatus status = ProbeStatus.TimeExceeded)
        => new(hop, address, null, status == ProbeStatus.TimeExceeded ? 10 : null,
            10, 10, 10, 0, 1, status == ProbeStatus.TimeExceeded ? 1 : 0,
            status == ProbeStatus.TimeExceeded ? 0 : 100, false, [], status);
}
