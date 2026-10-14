using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TraceScope.App.Controls;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WindowLayoutPersistenceTests
{
    [AvaloniaFact]
    public async Task FirstLaunchResizeAndSubsequentLaunchPreserveGeometryAndSplitter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracescope-window-layout", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await Fixture.Open(directory))
            {
                Assert.Equal(1200, first.Window.Width);
                Assert.Equal(800, first.Window.Height);
                Assert.Equal(.68, first.Capture().HistoryLayout.PanelFraction);
                first.Window.Width = 1080; first.Window.Height = 720;
                first.Window.Position = new PixelPoint(110, 80);
                first.Invoke("ApplyHistoryPanelFraction", .73);
                await Task.Delay(40); Dispatcher.UIThread.RunJobs();
                await first.Save();
                var saved = (await new WorkspaceStore(Path.Combine(directory, "workspace.json")).LoadAsync())!;
                Assert.Equal(1080, saved.WindowLayout!.Width);
                Assert.Equal(720, saved.WindowLayout.Height);
                Assert.Equal(110, saved.WindowLayout.X); Assert.Equal(80, saved.WindowLayout.Y);
            }
            await using var second = await Fixture.Open(directory);
            Assert.Equal(1080, second.Window.Width); Assert.Equal(720, second.Window.Height);
            Assert.Equal(new PixelPoint(110, 80), second.Window.Position);
            Assert.Equal(.73, second.Capture().HistoryLayout.PanelFraction);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task OffscreenWorkspaceIsRepairedWithoutChangingProbeSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracescope-window-layout", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33550 };
        var saved = new WorkspaceConfiguration
        {
            RestoreOnStartup = false, DefaultProbeOptions = options,
            WindowLayout = new MainWindowLayout { X = 100000, Y = -100000, Width = 1400, Height = 1000 },
            HistoryLayout = new HistoryLayoutPreferences { PanelFraction = .57 }
        };
        try
        {
            await new WorkspaceStore(Path.Combine(directory, "workspace.json")).SaveAsync(saved);
            await using var f = await Fixture.Open(directory);
            var displays = f.Window.Screens.All.Select(s => new WindowDisplay(s.WorkingArea, s.Scaling, s.IsPrimary)).ToArray();
            var expected = WindowPlacementCalculator.Calculate(saved.WindowLayout, displays);
            Assert.Equal(new PixelPoint(expected.X, expected.Y), f.Window.Position);
            Assert.Equal(expected.Width, f.Window.Width); Assert.Equal(expected.Height, f.Window.Height);
            Assert.Equal(options, f.Capture().DefaultProbeOptions);
            Assert.Equal(.57, f.Capture().HistoryLayout.PanelFraction);
            Assert.Empty(f.Engine.Sessions);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task MaximizedAndMinimizedCaptureKeepNormalRestoreDimensions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracescope-window-layout", Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Open(directory);
            f.Window.Width = 1090; f.Window.Height = 730;
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            var normal = f.Capture().WindowLayout!;
            f.Window.WindowState = WindowState.Maximized;
            var maximized = f.Capture().WindowLayout!;
            Assert.True(maximized.Maximized);
            Assert.Equal(normal.Width, maximized.Width); Assert.Equal(normal.Height, maximized.Height);
            f.Window.WindowState = WindowState.Minimized;
            var minimized = f.Capture().WindowLayout!;
            Assert.True(minimized.Maximized);
            Assert.Equal(normal.Width, minimized.Width); Assert.Equal(normal.Height, minimized.Height);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MainWindow Window { get; }
        public TraceEngine Engine { get; }
        private Fixture(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            Engine = new TraceEngine(Path.Combine(directory, "history.db"));
            Window = new MainWindow(["--data-dir", directory], Engine);
            typeof(MainWindow).GetField("_runtimeFeatures", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, true);
            Window.Show();
        }
        public static async Task<Fixture> Open(string directory)
        {
            var f = new Fixture(directory);
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!(bool)typeof(MainWindow).GetField("_windowLayoutReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window)!)
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
            return f;
        }
        public object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public WorkspaceConfiguration Capture() => (WorkspaceConfiguration)Invoke("CaptureWorkspace")!;
        public Task Save() => (Task)Invoke("SaveWorkspaceNowAsync")!;
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            while (Window.IsVisible) await Task.Delay(20);
            await Engine.DisposeAsync();
        }
    }
}
