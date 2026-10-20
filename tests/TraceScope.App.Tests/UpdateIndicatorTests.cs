using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Updates;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class UpdateIndicatorTests
{
    private static readonly UpdateRelease Release = new(new Version(0, 8, 2), "Update preview notes.",
        new("https://github.com/ryanonmars/pngr/releases/tag/v0.8.2"), new("https://github.com/ryanonmars/pngr/releases/download/v0.8.2/"));
    private static UpdateCheckResult Available => new(UpdateCheckStatus.Available, Release);

    [AvaloniaFact]
    public async Task StartupDoesNotBlockMonitoringAndPeriodicChecksStopOnClose()
    {
        await using var fixture = new Fixture(runtime: true);
        var pending = new TaskCompletionSource<UpdateCheckResult>();
        fixture.Updates.Check = (_, _) => pending.Task;
        fixture.Window.Show();
        await Until(() => fixture.Updates.Checks == 1);
        Assert.True(fixture.Updates.LastManual); // Startup bypasses the persisted throttle.
        Assert.True(fixture.Window.IsVisible);
        Assert.True(Find<TextBox>(fixture.Window, "TargetInput").IsEffectivelyEnabled);
        Assert.Empty(fixture.Window.OwnedWindows);
        var timer = Field<DispatcherTimer>(fixture.Window, "_automaticUpdateTimer");
        Assert.Equal(UpdateService.AutomaticCheckInterval, timer.Interval);
        pending.SetResult(Available);
        await Until(() => Indicator(fixture).IsVisible);
        fixture.Updates.Check = (_, _) => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Current));
        timer.Interval = TimeSpan.FromMilliseconds(20); timer.Stop(); timer.Start();
        await Until(() => fixture.Updates.Checks >= 2);
        Assert.False(fixture.Updates.LastManual); // Periodic checks keep the normal throttle.
        Assert.False(Indicator(fixture).IsVisible);
        fixture.Window.Close(); await Until(() => !fixture.Window.IsVisible);
        Assert.False(timer.IsEnabled);
        var count = fixture.Updates.Checks; await Task.Delay(60); Assert.Equal(count, fixture.Updates.Checks);
    }

    [AvaloniaFact]
    public async Task StartupDiscoversNewReleaseEvenAfterRecentSuccessfulOrFailedCheck()
    {
        foreach (var previousCheckFailed in new[] { false, true })
        {
            await using var fixture = new Fixture(runtime: true);
            var handler = new StartupHttp { Fail = previousCheckFailed };
            using var client = new HttpClient(handler);
            var now = DateTimeOffset.UtcNow;
            var previous = new UpdateService(fixture.DirectoryPath, new Version(0, 8, 1), client, () => now);
            Assert.Equal(previousCheckFailed ? UpdateCheckStatus.Failed : UpdateCheckStatus.Current,
                (await previous.CheckAsync(false)).Status);
            handler.Fail = false;
            handler.Version = "0.8.2";
            var restarted = new UpdateService(fixture.DirectoryPath, new Version(0, 8, 1), client, () => now);
            Assert.Equal(UpdateCheckStatus.Skipped, (await restarted.CheckAsync(false)).Status);
            typeof(MainWindow).GetField("_updates", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, restarted);

            fixture.Window.Show();
            await Until(() => Indicator(fixture).IsVisible);
            Assert.Equal(2, handler.Calls);
            Assert.Equal("Update available: v0.8.2", Indicator(fixture).Content);
            Assert.Empty(fixture.Window.OwnedWindows);
        }
    }

    [AvaloniaFact]
    public async Task AvailabilityPersistsAcrossFailuresSkippedChecksAndDialogDismissal()
    {
        await using var fixture = new Fixture(); fixture.Window.Show();
        await Check(fixture, Available);
        Assert.Equal("Update available: v0.8.2", Indicator(fixture).Content);
        Assert.Contains("v0.8.2", AutomationProperties.GetName(Indicator(fixture)));
        Assert.NotNull(ToolTip.GetTip(Indicator(fixture)));
        Assert.Empty(fixture.Window.OwnedWindows);
        Click(Indicator(fixture));
        var dialog = fixture.Window.OwnedWindows.OfType<UpdateWindow>().Single();
        Assert.Equal(Release.Notes, Find<TextBlock>(dialog, "UpdateNotes").Text);
        Assert.True(Indicator(fixture).IsVisible);
        Assert.Equal(0, fixture.Updates.Downloads); Assert.Equal(0, fixture.Updates.Installs);
        Click(Find<Button>(dialog, "UpdateLater"));
        await Check(fixture, new(UpdateCheckStatus.Failed));
        await Check(fixture, new(UpdateCheckStatus.Skipped));
        fixture.Updates.Check = (_, _) => throw new IOException("offline");
        await InvokeCheck(fixture.Window, false);
        Assert.True(Indicator(fixture).IsVisible);
        await Check(fixture, new(UpdateCheckStatus.Current));
        Assert.False(Indicator(fixture).IsVisible);
    }

    [AvaloniaFact]
    public async Task ManualCheckSharesPendingAutomaticRequestAndStillShowsDialog()
    {
        await using var fixture = new Fixture(); fixture.Window.Show();
        var pending = new TaskCompletionSource<UpdateCheckResult>();
        fixture.Updates.Check = (_, _) => pending.Task;
        var automatic = InvokeCheck(fixture.Window, false);
        var manual = InvokeCheck(fixture.Window, true);
        Assert.Equal(1, fixture.Updates.Checks);
        var dialog = fixture.Window.OwnedWindows.OfType<UpdateWindow>().Single();
        Assert.Contains("Checking", Find<TextBlock>(dialog, "UpdateStatus").Text);
        pending.SetResult(Available); await Task.WhenAll(automatic, manual);
        Assert.Equal(Release.Notes, Find<TextBlock>(dialog, "UpdateNotes").Text);
        Assert.True(Indicator(fixture).IsVisible);
        dialog.Close();
        await InvokeCheck(fixture.Window, true);
        Assert.True(fixture.Updates.LastManual);
        Assert.Equal(2, fixture.Updates.Checks);
    }

    [AvaloniaFact]
    public async Task KeyboardActionPreservesInstallApprovalAndShutdownFlow()
    {
        await using var fixture = new Fixture(); fixture.Window.Show();
        await Check(fixture, Available);
        Indicator(fixture).Focus();
        Assert.True(Indicator(fixture).IsFocused);
        Indicator(fixture).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        var dialog = fixture.Window.OwnedWindows.OfType<UpdateWindow>().Single();
        Click(Find<Button>(dialog, "UpdateDownload"));
        await Until(() => Find<Button>(dialog, "UpdateInstall").IsVisible);
        Assert.Equal(1, fixture.Updates.Downloads); Assert.Equal(0, fixture.Updates.Installs);
        Click(Indicator(fixture));
        Assert.True(Find<Button>(dialog, "UpdateInstall").IsVisible);
        Click(Find<Button>(dialog, "UpdateInstall"));
        await Until(() => !fixture.Window.IsVisible);
        Assert.Equal(1, fixture.Updates.Installs);
        Assert.False(Indicator(fixture).IsVisible);
    }

    [AvaloniaFact]
    public async Task CachedAvailabilityAndManualFailureUseExistingDialog()
    {
        await using var fixture = new Fixture(); fixture.Window.Show();
        await Check(fixture, new(UpdateCheckStatus.Failed, Release));
        Assert.True(Indicator(fixture).IsVisible);
        fixture.Updates.Check = (_, _) => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Failed));
        await InvokeCheck(fixture.Window, true);
        Assert.Contains("Could not check", Find<TextBlock>(fixture.Window.OwnedWindows.Single(), "UpdateStatus").Text);
        Assert.True(Indicator(fixture).IsVisible);
    }

    [AvaloniaFact]
    public async Task ClosingCancelsPendingAutomaticCheckWithoutOpeningADialog()
    {
        await using var fixture = new Fixture(runtime: true);
        var canceled = new TaskCompletionSource();
        fixture.Updates.Check = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.SetResult(); throw; }
            return new(UpdateCheckStatus.Current);
        };
        fixture.Window.Show(); await Until(() => fixture.Updates.Checks == 1);
        Assert.Empty(fixture.Window.OwnedWindows);
        fixture.Window.Close(); await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => !fixture.Window.IsVisible);
        Assert.False(Field<DispatcherTimer>(fixture.Window, "_automaticUpdateTimer").IsEnabled);
    }

    [AvaloniaFact]
    public async Task FailedInstallKeepsAvailabilityAndDoesNotCloseApplication()
    {
        await using var fixture = new Fixture(); fixture.Window.Show();
        await Check(fixture, Available); Click(Indicator(fixture));
        var dialog = fixture.Window.OwnedWindows.OfType<UpdateWindow>().Single();
        Click(Find<Button>(dialog, "UpdateDownload"));
        await Until(() => Find<Button>(dialog, "UpdateInstall").IsVisible);
        fixture.Updates.FailInstall = true; Click(Find<Button>(dialog, "UpdateInstall"));
        Assert.True(fixture.Window.IsVisible); Assert.True(Indicator(fixture).IsVisible);
        Assert.Contains("Could not start", Find<TextBlock>(dialog, "UpdateStatus").Text);
    }

    [AvaloniaTheory]
    [InlineData(980, 640)]
    [InlineData(1200, 800)]
    public async Task StatusIndicatorFitsBesideVersionAndLongStatus(double width, double height)
    {
        await using var fixture = new Fixture();
        fixture.Window.Width = width; fixture.Window.Height = height; fixture.Window.Show();
        await Check(fixture, Available);
        Find<TextBlock>(fixture.Window, "StatusText").Text = new string('x', 500);
        fixture.Window.UpdateLayout();
        var indicator = Indicator(fixture); var version = Find<TextBlock>(fixture.Window, "VersionLabel");
        var point = indicator.TranslatePoint(new Point(), fixture.Window)!.Value;
        var versionPoint = version.TranslatePoint(new Point(), fixture.Window)!.Value;
        Assert.True(point.X > width / 2, $"Indicator {point}, window {fixture.Window.Bounds}, version {version.Bounds}"); Assert.True(point.Y > height - 40, $"Indicator {point}, window {fixture.Window.Bounds}");
        Assert.True(point.X + indicator.Bounds.Width <= versionPoint.X);
        Assert.True(versionPoint.X + version.Bounds.Width <= width);
        Assert.True(indicator.Bounds.Height >= 18);
    }

    private static async Task Check(Fixture f, UpdateCheckResult result)
    { f.Updates.Check = (_, _) => Task.FromResult(result); await InvokeCheck(f.Window, false); }
    private static Task InvokeCheck(MainWindow w, bool manual) => (Task)typeof(MainWindow).GetMethod("CheckForUpdatesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, [manual, false])!;
    private static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static Button Indicator(Fixture f) => Find<Button>(f.Window, "UpdateIndicator");
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; !condition() && i < 300; i++) await Task.Delay(10); Assert.True(condition()); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pngr-update-indicator", Guid.NewGuid().ToString("N"));
        public string DirectoryPath => _directory;
        private readonly TraceEngine _engine;
        public readonly MainWindow Window;
        public readonly FakeUpdates Updates = new();
        public Fixture(bool runtime = false)
        {
            _engine = new TraceEngine(Path.Combine(_directory, "history.db"));
            Window = new MainWindow(["--data-dir", _directory], _engine);
            typeof(MainWindow).GetField("_updates", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, Updates);
            typeof(MainWindow).GetField("_runtimeFeatures", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, runtime);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var w in Window.OwnedWindows.ToArray()) w.Close();
            Window.Close(); await Until(() => !Window.IsVisible);
            await _engine.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
    private sealed class StartupHttp : HttpMessageHandler
    {
        public bool Fail;
        public string Version = "0.8.1";
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"draft":false,"prerelease":false,"tag_name":"v{{Version}}","html_url":"https://github.com/ryanonmars/pngr/releases/tag/v{{Version}}","body":"Update notes."}
                    """)
            });
        }
    }
    private sealed class FakeUpdates : IAppUpdateService
    {
        public int Checks, Downloads, Installs; public bool LastManual, FailInstall;
        public string? InstallationSupport => null;
        public Func<bool, CancellationToken, Task<UpdateCheckResult>> Check = (_, _) => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Current));
        public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
        { Checks++; LastManual = manual; return Check(manual, cancellationToken); }
        public Task DownloadAsync(UpdateRelease release, IProgress<int>? progress, CancellationToken cancellationToken)
        { Downloads++; return Task.CompletedTask; }
        public void PrepareInstall() { if (FailInstall) throw new IOException("Mock preparation failure"); Installs++; }
    }
}
