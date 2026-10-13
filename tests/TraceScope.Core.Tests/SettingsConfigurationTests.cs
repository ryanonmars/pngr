using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class SettingsConfigurationTests
{
    [Fact]
    public async Task ExistingWorkspaceWithoutSettingsFieldsGetsCompatibleDefaults()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Version\":1,\"Targets\":[],\"Profiles\":[],\"Alerts\":[]}"));
        var configuration = await WorkspaceStore.ImportAsync(stream);
        Assert.Equal(new ProbeOptions(), configuration.DefaultProbeOptions);
        Assert.Equal(new DisplayConfiguration(), configuration.Display);
        Assert.Equal(.6, configuration.HistoryLayout.PanelFraction);
        Assert.Empty(configuration.HistoryLayout.Targets);
        Assert.False(configuration.WebEnabled);
        Assert.False(configuration.Actions.NativeNotificationsEnabled);
    }

    [Fact]
    public async Task DisplayAndDefaultProbeSettingsRoundTripThroughWorkspaceJson()
    {
        var configuration = new WorkspaceConfiguration
        {
            DefaultProbeOptions = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33435, Concurrency = 3,
                HistoryLimit = 1234, ResolveHostnames = false, Interval = TimeSpan.FromSeconds(3.2) },
            Display = new DisplayConfiguration { WarningLatencyMs = 40, CriticalLatencyMs = 80, FixedMaximumMs = 150,
                DefaultTimeRangeMinutes = 360, ShowJitter = false, ShowEventMarkers = false }
        };
        using var stream = new MemoryStream();
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, configuration, WorkspaceStore.JsonOptions);
        stream.Position = 0;
        var restored = await WorkspaceStore.ImportAsync(stream);
        Assert.Equal(configuration.DefaultProbeOptions, restored.DefaultProbeOptions);
        Assert.Equal(configuration.Display, restored.Display);
        Assert.True(restored.Display.ShowPacketLoss);
    }

    [Fact]
    public async Task HistoricalLayoutRoundTripsWithoutChangingMonitoringOptions()
    {
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434 };
        var configuration = new WorkspaceConfiguration
        {
            Targets = [new("1.1.1.1", options)],
            HistoryLayout = new()
            {
                PanelFraction = .7,
                Targets = [new("1.1.1.1", 0, true, [1, 5], new Dictionary<int, double> { [0] = .3, [1] = .2, [5] = .5 })]
            }
        };
        using var stream = new MemoryStream();
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, configuration, WorkspaceStore.JsonOptions);
        stream.Position = 0;
        var restored = await WorkspaceStore.ImportAsync(stream);
        Assert.Equal(.7, restored.HistoryLayout.PanelFraction);
        var layout = Assert.Single(restored.HistoryLayout.Targets);
        Assert.Equal(new[] { 1, 5 }, layout.PinnedHops);
        Assert.Equal(.5, layout.GraphWeights![5]);
        Assert.Equal(options, Assert.Single(restored.Targets).Options);
        Assert.False(restored.WebEnabled);
    }

    [Theory]
    [InlineData("{\"HistoryLayout\":null}")]
    [InlineData("{\"HistoryLayout\":{\"PanelFraction\":0}}")]
    [InlineData("{\"HistoryLayout\":{\"Targets\":[{\"Target\":\"1.1.1.1\",\"GraphWeights\":{\"1\":0}}]}}")]
    [InlineData("{\"HistoryLayout\":{\"Targets\":[{\"Target\":\"1.1.1.1\",\"PinnedHops\":[-1]}]}}")]
    public async Task InvalidLayoutIsRejectedBeforeItCanBreakRendering(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.ImportAsync(stream));
    }

    [Theory]
    [InlineData(100, 100, 0)]
    [InlineData(200, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(double.NaN, 200, 0)]
    [InlineData(100, double.PositiveInfinity, 0)]
    [InlineData(100, 200, -1)]
    [InlineData(100, 200, .5)]
    [InlineData(100, 200, 60001)]
    public void InvalidDisplaySettingsCannotBeSaved(double warning, double critical, double maximum)
        => Assert.Throws<InvalidDataException>(() => new WorkspaceConfiguration
        { Display = new DisplayConfiguration { WarningLatencyMs = warning, CriticalLatencyMs = critical, FixedMaximumMs = maximum } }.Validate());

    [Fact]
    public void InvalidOrMissingDefaultsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceConfiguration
        { DefaultProbeOptions = new ProbeOptions { Concurrency = 99 } }.Validate());
        Assert.Throws<InvalidDataException>(() => new WorkspaceConfiguration { DefaultProbeOptions = null! }.Validate());
        Assert.Throws<InvalidDataException>(() => new WorkspaceConfiguration { Display = null! }.Validate());
        Assert.Throws<InvalidDataException>(() => new WorkspaceConfiguration
        { Display = new DisplayConfiguration { DefaultTimeRangeMinutes = 17 } }.Validate());
    }
}
