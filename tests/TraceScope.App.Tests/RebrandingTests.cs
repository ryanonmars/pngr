using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class RebrandingTests
{
    [AvaloniaFact]
    public async Task PngrUsesExistingWorkspaceProfilesAndSqliteHistoryWithoutMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pngr-brand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "tracescope.db");
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434,
            UdpPortMode = UdpPortMode.Range, UdpEndPort = 33625, UdpPacketSize = 56 };
        var workspace = new WorkspaceConfiguration { RestoreOnStartup = false,
            Profiles = [new("Existing profile", options)],
            Targets = [new("127.0.0.1", options, "Office", true)],
            LastUsedProbeOptions = options, WindowLayout = new() { Width = 1200, Height = 800 } };
        Guid sessionId;
        await using (var original = new TraceEngine(database, transport: new Replies()))
        {
            var session = await original.StartTargetAsync("127.0.0.1", new ProbeOptions { MaxHops = 1, ResolveHostnames = false });
            sessionId = session.Id;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await original.Storage.LoadSamplesAsync(sessionId)).Count == 0)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
        }
        await new WorkspaceStore(Path.Combine(directory, "workspace.json")).SaveAsync(workspace);
        var before = await new HistoryStore(database).LoadSamplesAsync(sessionId);
        var window = new MainWindow(["--data-dir", directory]);
        window.Show();
        try
        {
            await (Task)typeof(MainWindow).GetMethod("InitializeProfessionalOnOpenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Assert.Equal("pngr — Network diagnostics", window.Title);
            Assert.Equal("pngr", typeof(MainWindow).Assembly.GetName().Name);
            Assert.Equal("pngr", typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
            Assert.Equal("pngr", typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title);
            var engine = (TraceEngine)typeof(MainWindow).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.Equal(database, engine.Storage.DatabasePath);
            Assert.Equal(before, await engine.Storage.LoadSamplesAsync(sessionId));
            Assert.Contains(await engine.Storage.LoadSessionsAsync(), s => s.Id == sessionId);
            var restored = (WorkspaceConfiguration)typeof(MainWindow).GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.Equal(workspace.Profiles, restored.Profiles);
            Assert.Equal(workspace.Targets, restored.Targets);
            Assert.Equal(options, restored.LastUsedProbeOptions);
            Assert.False(File.Exists(Path.Combine(directory, "pngr.db")));
            Assert.True(AssetLoader.Exists(new Uri("avares://pngr/Assets/Icons/pngr.png")));
            Assert.True(AssetLoader.Exists(new Uri("avares://pngr/Assets/Icons/pngr-windows.png")));
        }
        finally
        {
            window.Close();
            while (window.IsVisible) await Task.Delay(20);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class Replies : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
