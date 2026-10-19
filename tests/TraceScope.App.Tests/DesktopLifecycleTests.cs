using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class DesktopLifecycleTests
{
    [AvaloniaFact]
    public async Task FreshDesktopKeepsLocalApiDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        Assert.False(Field<WorkspaceConfiguration>(fixture.Window, "_configuration").WebEnabled);
        Invoke(fixture.Window, "OpenWeb", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.Any());
        var dialog = fixture.Window.OwnedWindows.Single();
        Assert.Equal("Stopped", Find<TextBlock>(dialog, "WebStatus").Text);
        Assert.False(Find<Button>(dialog, "WebOpenDashboard").IsEnabled);
        Assert.False(Find<Button>(dialog, "WebCopyAddress").IsEnabled);
        Assert.Empty(dialog.GetVisualDescendants().OfType<NumericUpDown>());
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task DesktopSettingsEnableDisableAndPersistApiWithoutSeparateEngine()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget();
        var session = fixture.Engine.Sessions.Single();
        var port = AvailablePort();
        Invoke(fixture.Window, "OpenWeb", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.Any());
        var dialog = fixture.Window.OwnedWindows.Single();
        Find<Button>(dialog, "WebServerSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = fixture.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        Assert.Equal("Web Server", Find<ListBox>(settings, "SettingsNavigation").SelectedItem);
        Find<TraceScope.App.Controls.SettingsNumberField>(settings, "SettingsWebPort").Value = port;
        Find<CheckBox>(settings, "SettingsWebEnabled").IsChecked = true;
        var apply = Find<Button>(settings, "SettingsApply");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Field<LocalWebServer?>(fixture.Window, "_webServer") is not null && apply.IsEnabled);
        var server = Field<LocalWebServer>(fixture.Window, "_webServer");
        var url = server.Url!;
        Assert.Equal("127.0.0.1", new Uri(url).Host);
        Assert.Equal(port, new Uri(url).Port);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Assert.Contains(session.Id.ToString(), await client.GetStringAsync(url + "/api/targets"));
        var saved = await fixture.LoadWorkspace();
        Assert.True(saved.WebEnabled);
        Assert.Equal(port, saved.WebPort);
        Find<CheckBox>(settings, "SettingsWebEnabled").IsChecked = false;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Field<LocalWebServer?>(fixture.Window, "_webServer") is null && apply.IsEnabled);
        Assert.False((await fixture.LoadWorkspace()).WebEnabled);
        Assert.Equal(SessionState.Running, session.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api/targets"));
        AssertPortReusable(port);
        settings.Close();
    }

    [AvaloniaFact]
    public async Task ClosingDesktopStopsApiMonitoringAndOwnedSummary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget();
        await InvokeTask(fixture.Window, "ConfigureWebAsync", true, AvailablePort());
        var server = Field<LocalWebServer>(fixture.Window, "_webServer");
        var url = server.Url!;
        Invoke(fixture.Window, "OpenSummary", null, new RoutedEventArgs(Button.ClickEvent));
        var summary = Assert.IsType<SummaryWindow>(fixture.Window.OwnedWindows.Single());
        Assert.True(summary.IsVisible);
        fixture.Window.Close();
        await Until(() => !fixture.Window.IsVisible);
        Assert.False(summary.IsVisible);
        Assert.Null(server.Url);
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        Assert.True(Field<CancellationTokenSource>(fixture.Window, "_applicationCancellation").IsCancellationRequested);
        Assert.All(fixture.Engine.Sessions, session => Assert.Equal(SessionState.Stopped, session.State));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api/targets"));
        AssertPortReusable(new Uri(url).Port);
    }

    [AvaloniaFact]
    public async Task WorkspaceSaveFailureDoesNotSkipApiOrMonitoringCleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget();
        await InvokeTask(fixture.Window, "ConfigureWebAsync", true, AvailablePort());
        var server = Field<LocalWebServer>(fixture.Window, "_webServer");
        var port = new Uri(server.Url!).Port;
        var directoryAsWorkspaceFile = Path.Combine(fixture.Directory, "cannot-replace-directory");
        System.IO.Directory.CreateDirectory(directoryAsWorkspaceFile);
        SetField(fixture.Window, "_workspaceStore", new WorkspaceStore(directoryAsWorkspaceFile));
        SetField(fixture.Window, "_runtimeFeatures", true);
        fixture.Window.Close();
        await Until(() => !fixture.Window.IsVisible);
        Assert.True(Field<bool>(fixture.Window, "_shutdownComplete"));
        Assert.Null(server.Url);
        Assert.All(fixture.Engine.Sessions, session => Assert.Equal(SessionState.Stopped, session.State));
        AssertPortReusable(port);
    }

    [AvaloniaFact]
    public async Task RepeatedCloseWaitsForInFlightProbeCancellationBeforeWindowCloses()
    {
        var transport = new DelayedCancellationTransport();
        await using var fixture = await Fixture.CreateAsync(transport);
        var session = await fixture.Engine.StartTargetAsync("127.0.0.9", new ProbeOptions
        { MaxHops = 1, ResolveHostnames = false });
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            fixture.Window.Close();
            await transport.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Window.Close();
            Assert.True(fixture.Window.IsVisible);
            Assert.False(Field<bool>(fixture.Window, "_shutdownComplete"));
            Assert.False(Field<Task>(fixture.Window, "_shutdownTask").IsCompleted);
        }
        finally { transport.Release.TrySetResult(); }
        await Until(() => !fixture.Window.IsVisible);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.True(Field<bool>(fixture.Window, "_shutdownComplete"));
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static void AssertPortReusable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
    }
    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void SetField(MainWindow window, string name, object value)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string method, params object?[] arguments)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static Task InvokeTask(MainWindow window, string method, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(Invoke(window, method, arguments));
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(6);
        while (!predicate()) { if (DateTime.UtcNow > end) throw new TimeoutException("Desktop lifecycle did not complete."); await Task.Delay(20); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        private Fixture(IProbeTransport? transport)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate project test artifacts.");
            Directory = Path.Combine(root.FullName, "artifacts", "tests", "desktop-lifecycle", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: transport ?? new ImmediateTransport());
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Show();
        }
        public static async Task<Fixture> CreateAsync(IProbeTransport? transport = null)
        {
            var fixture = new Fixture(transport);
            await InvokeTask(fixture.Window, "InitializeProfessionalOnOpenAsync");
            await Task.Delay(20);
            return fixture;
        }
        public async Task AddTarget()
        {
            await InvokeTask(Window, "AddTarget", "127.0.0.1", new ProbeOptions
            { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            await Until(() => Engine.Sessions.Single().LatestSnapshot is not null);
        }
        public async Task<WorkspaceConfiguration> LoadWorkspace()
            => (await new WorkspaceStore(Path.Combine(Directory, "workspace.json")).LoadAsync())!;
        public async ValueTask DisposeAsync()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ImmediateTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
    private sealed class DelayedCancellationTransport : IProbeTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
                throw;
            }
            return new(destination.ToString(), 12, ProbeStatus.Success);
        }
    }
}
