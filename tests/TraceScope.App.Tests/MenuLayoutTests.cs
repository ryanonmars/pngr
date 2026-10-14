using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.App.Updates;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class MenuLayoutTests
{
    [AvaloniaFact]
    public async Task WindowsMenusContainEveryFormerHeaderActionWithoutDuplicateButtons()
    {
        await using var fixture = new Fixture();
        var menu = MenuTestSupport.WindowsMenu(fixture.Window);
        Assert.Equal(new[] { "File", "Window", "Tools" }, menu.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        Assert.Equal(new[] { "Open History…", "Export CSV…", "Workspace…", "Check for Updates…" }, Headers(menu, "File"));
        Assert.Equal(new[] { "All Targets", "Summary" }, Headers(menu, "Window"));
        Assert.Equal(new[] { "Settings…", "Alerts…", "Network…", "Local Web / API…", "Profiles" }, Headers(menu, "Tools"));
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Control), MenuTestSupport.Item(fixture.Window, "Settings…").Gesture);
        foreach (var name in new[] { "SettingsButton", "SummaryButton", "AlertsButton", "WorkspaceButton", "NetworkButton", "WebButton", "HistoryButton", "ExportButton" })
            Assert.Null(fixture.Window.FindControl<Button>(name));
        Assert.NotNull(fixture.Window.FindControl<Button>("ConfigureProbeButton"));
        Assert.NotNull(fixture.Window.FindControl<Button>("StartButton"));
        Assert.False(MenuTestSupport.Item(fixture.Window, "Export CSV…").IsEnabled);
        fixture.Window.FindControl<NativeMenuBar>("ApplicationMenuBar")!.IsVisible = true;
        fixture.Window.Width = 980; fixture.Window.Height = 640; fixture.Window.UpdateLayout();
        Assert.True(fixture.Window.FindControl<NativeMenuBar>("ApplicationMenuBar")!.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void MacSettingsRemainInApplicationMenuRatherThanTools()
    {
        var menu = (NativeMenu)typeof(App).GetMethod("CreateWindowMenuForPlatform", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Application.Current, [false, null])!;
        Assert.DoesNotContain("Settings…", Headers(menu, "Tools"));
        Assert.DoesNotContain("Check for Updates…", Headers(menu, "File"));
        Assert.Contains(NativeMenu.GetMenu(Application.Current!)!.Items.OfType<NativeMenuItem>(), item => item.Header == "Check for Updates…");
        Assert.Equal("Pngr", Application.Current!.Name);
        Assert.Contains(NativeMenu.GetMenu(Application.Current!)!.Items.OfType<NativeMenuItem>(), item => item.Header == "Settings…");
    }

    [AvaloniaFact]
    public async Task MenuClicksOpenExistingWindowsAndOverviewWithoutEnablingTheApi()
    {
        await using var fixture = new Fixture();
        MenuTestSupport.WindowsMenu(fixture.Window);
        foreach (var (header, title) in new[]
        {
            ("Settings…", "Pngr Settings"), ("Open History…", "Saved trace sessions"),
            ("Summary", "Pngr · Professional Summaries"),
            ("Alerts…", "Alert rules and events"), ("Workspace…", "Workspaces and data"),
            ("Network…", "Pngr · Local Network"), ("Local Web / API…", "Local read-only web interface")
        })
        {
            MenuTestSupport.Click(fixture.Window, header);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!fixture.Window.OwnedWindows.Any(w => w.Title == title))
            { if (DateTime.UtcNow > deadline) throw new TimeoutException(header); await Task.Delay(20); }
            var dialog = fixture.Window.OwnedWindows.Single(w => w.Title == title);
            Assert.True(dialog.IsVisible);
            dialog.Close();
        }
        MenuTestSupport.Click(fixture.Window, "All Targets");
        Assert.True(fixture.Window.FindControl<AllTargetsView>("AllTargetsPane")!.IsVisible);
        Assert.False(fixture.Window.FindControl<Grid>("SingleTargetPane")!.IsVisible);
        var configuration = (WorkspaceConfiguration)typeof(MainWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
        Assert.False(configuration.WebEnabled);
        Assert.Empty(fixture.Engine.Sessions);
    }

    [AvaloniaFact]
    public async Task UpdateMenuChecksWithoutApplyingSettingsOrStartingMonitoring()
    {
        await using var fixture = new Fixture();
        var updater = new TestUpdater();
        typeof(MainWindow).GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window, updater);
        MenuTestSupport.WindowsMenu(fixture.Window);
        MenuTestSupport.Click(fixture.Window, "Settings…");
        var settings = fixture.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        settings.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "SettingsRestore").IsChecked = false;
        MenuTestSupport.Click(fixture.Window, "Check for Updates…");
        Assert.Equal(1, updater.ManualChecks);
        Assert.IsType<UpdateWindow>(fixture.Window.OwnedWindows.Single(w => w is UpdateWindow));
        var configuration = (WorkspaceConfiguration)typeof(MainWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
        Assert.True(configuration.RestoreOnStartup);
        Assert.Empty(fixture.Engine.Sessions);
        foreach (var dialog in fixture.Window.OwnedWindows.ToArray()) dialog.Close();
    }

    private sealed class TestUpdater : IAppUpdateService
    {
        public int ManualChecks { get; private set; }
        public string? InstallationSupport => null;
        public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
        {
            if (manual) ManualChecks++;
            return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Current));
        }
        public Task DownloadAsync(UpdateRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Checking must not download");
        public void PrepareInstall() => throw new InvalidOperationException("Checking must not install");
    }

    private static IEnumerable<string?> Headers(NativeMenu menu, string parent)
        => menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == parent).Menu!.Items.OfType<NativeMenuItem>().Select(item => item.Header);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pngr-menu-layout", Guid.NewGuid().ToString("N"));
        internal TraceEngine Engine { get; }
        internal MainWindow Window { get; }
        internal Fixture()
        {
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"));
            Window = new MainWindow(["--data-dir", _directory], Engine);
            Window.Show();
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            while (Window.IsVisible) await Task.Delay(20);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }
}
