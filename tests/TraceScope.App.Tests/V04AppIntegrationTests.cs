using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class V04AppIntegrationTests
{
    [AvaloniaFact]
    public async Task SummaryWindowChangesPersistThroughDesktopWorkspaceCallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        await fixture.AddTarget("127.0.0.2");
        // Enable only the desktop's debounced save callback; its production startup is not run.
        SetField(fixture.Window, "_runtimeFeatures", true);
        Invoke(fixture.Window, "OpenSummary", null, new RoutedEventArgs(Button.ClickEvent));
        var summary = Assert.IsType<SummaryWindow>(fixture.Window.OwnedWindows.Single());
        var definition = new SummaryDefinition
        { Name = "Saved office view", Targets = ["127.0.0.2"], Layout = SummaryLayout.Graphs, FocusMinutes = 15 };
        summary.SetDefinition(definition);
        Find<ComboBox>(summary, "SummaryFocus").SelectedIndex = 0;
        await Until(() => File.Exists(fixture.WorkspacePath));
        var saved = await fixture.LoadWorkspace();
        var view = Assert.Single(saved.SummaryViews);
        Assert.Equal(definition.Id, view.Id);
        Assert.Equal("Saved office view", view.Name);
        Assert.Equal(["127.0.0.2"], view.Targets);
        Assert.Equal(SummaryLayout.Graphs, view.Layout);
        Assert.Equal(1, view.FocusMinutes);
        Assert.Equal(2, saved.Targets.Count);
        summary.Close();

        Invoke(fixture.Window, "OpenSummary", null, new RoutedEventArgs(Button.ClickEvent));
        var reopened = Assert.IsType<SummaryWindow>(fixture.Window.OwnedWindows.Single());
        reopened.SelectSummary(view.Id);
        Assert.Equal("127.0.0.2", Assert.Single(Find<DataGrid>(reopened, "SummaryTable").ItemsSource!.Cast<SummaryRow>()).Target);
        Assert.Equal(SummaryLayout.Graphs, reopened.SelectedDefinition!.Layout);
        reopened.Close();
    }

    [AvaloniaFact]
    public async Task AlertsOpenNativeSettingsDisabledAndSavingPreservesOptInDefaults()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var notifier = new ObservedNotifier();
        SetField(fixture.Window, "_actions", new AlertActionDispatcher(notifier: notifier));
        Invoke(fixture.Window, "OpenAlerts", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var alerts = Assert.IsType<SettingsWindow>(fixture.Window.OwnedWindows.Single());
        Click(Find<Button>(alerts, "SettingsConfigureNotifications"));
        Click(Find<Button>(alerts, "SettingsConfigureAdvancedActions"));
        await Until(() => fixture.Window.OwnedWindows.Any(w => w.Title == "Notifications and alert actions"));
        var actions = fixture.Window.OwnedWindows.Single(w => w.Title == "Notifications and alert actions");
        Assert.False(Find<CheckBox>(actions, "NativeNotifications").IsChecked);
        Assert.NotNull(Find<Button>(actions, "NotificationPermission"));
        Click(Find<Button>(actions, "SaveAlertActions"));
        await Until(() => Find<TextBlock>(actions, "ActionStatus").Text?.StartsWith("Actions saved.") == true);
        var saved = await fixture.LoadWorkspace();
        Assert.False(saved.Actions.NativeNotificationsEnabled);
        Assert.False(saved.Actions.Smtp.Enabled);
        Assert.False(saved.Actions.Webhook.Enabled);
        Assert.False(saved.Actions.Script.Enabled);
        Assert.True(saved.Actions.IncludeRecovery);
        Assert.Equal(30, saved.Actions.MinimumNotificationSpacingSeconds);
        Assert.Equal(30, saved.Actions.MinimumActionSpacingSeconds);
        Assert.Equal(15, saved.Actions.ActionTimeoutSeconds);
        Assert.Empty(saved.Actions.Script.Arguments);
        Assert.False(saved.WebEnabled);
        Assert.Equal(0, notifier.PermissionRequests);
        Assert.Equal(0, notifier.Sends);
        actions.Close(); alerts.Close();
    }

    [AvaloniaFact]
    public async Task WebhookFormatSelectorMasksAndPersistsCredentialsWithoutChangingLegacyDefault()
    {
        await using var fixture = await Fixture.CreateAsync();
        var secrets = new InMemoryWebhookSecrets();
        SetField(fixture.Window, "_workspaceStore", new WorkspaceStore(fixture.WorkspacePath, secrets));
        Invoke(fixture.Window, "OpenAlertActions", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.Any(w => w.Title == "Notifications and alert actions"));
        var dialog = fixture.Window.OwnedWindows.Single();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await Until(() => dialog.GetVisualDescendants().OfType<ComboBox>().Any(c => c.Name == "WebhookFormat"));
        var format = Find<ComboBox>(dialog, "WebhookFormat");
        var url = Find<TextBox>(dialog, "WebhookUrl");
        Assert.Equal(0, format.SelectedIndex);
        Assert.Equal('●', url.PasswordChar);
        Assert.True(Find<TextBox>(dialog, "WebhookBearerReference").IsEnabled);
        format.SelectedIndex = 1;
        Assert.False(Find<TextBox>(dialog, "WebhookBearerReference").IsEnabled);
        Find<CheckBox>(dialog, "WebhookEnabled").IsChecked = true;
        url.Text = "https://discord.com/api/webhooks/123456789012345678/test-credential";
        Click(Find<Button>(dialog, "SaveAlertActions"));
        await Until(() => Find<TextBlock>(dialog, "ActionStatus").Text?.StartsWith("Actions saved.") == true);
        var saved = (await new WorkspaceStore(fixture.WorkspacePath, secrets).LoadAsync())!;
        Assert.Equal(WebhookFormat.Discord, saved.Actions.Webhook.Format);
        Assert.True(saved.Actions.Webhook.Enabled);
        Assert.Equal("", saved.Actions.Webhook.Url);
        Assert.Single(secrets.Values);
        Assert.Equal("", url.Text);
        Assert.DoesNotContain("test-credential", await File.ReadAllTextAsync(fixture.WorkspacePath));
        dialog.Close();
        Invoke(fixture.Window, "OpenAlertActions", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.Any());
        var reopened = fixture.Window.OwnedWindows.Single();
        reopened.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await Until(() => reopened.GetVisualDescendants().OfType<ComboBox>().Any(c => c.Name == "WebhookFormat"));
        Assert.Equal(1, Find<ComboBox>(reopened, "WebhookFormat").SelectedIndex);
        Assert.Equal("", Find<TextBox>(reopened, "WebhookUrl").Text);
        reopened.Close();
    }

    private sealed class InMemoryWebhookSecrets : IWebhookSecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string Store(string url, string? existingId = null)
        { var id = existingId ?? Guid.NewGuid().ToString("N"); Values[id] = url; return id; }
        public string Retrieve(string id) => Values[id];
        public void Delete(string id) => Values.Remove(id);
    }

    [AvaloniaFact]
    public async Task AcknowledgeButtonUpdatesVisibleEventAndPersistsItsNote()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var session = fixture.Engine.Sessions.Single();
        var entry = new MonitoringEvent(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow,
            "Alert", "Integration test event for acknowledgment", "test-rule");
        await fixture.Engine.Storage.AppendEventAsync(entry);
        Assert.Null(entry.AcknowledgedAt);
        Assert.Null(Assert.Single(await fixture.Engine.Storage.LoadEventsAsync(session.Id), e => e.Id == entry.Id).AcknowledgedAt);
        Invoke(fixture.Window, "OpenAlerts", null, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var alerts = fixture.Window.OwnedWindows.Single();
        await Until(() => alerts.GetVisualDescendants().OfType<DataGrid>().Any(grid =>
            grid.ItemsSource?.Cast<object>().Any(item => item is MonitoringEvent e && e.Id == entry.Id) == true));
        var events = Find<DataGrid>(alerts, "AlertEventsTable");
        var before = Assert.Single(events.ItemsSource!.Cast<MonitoringEvent>(), e => e.Id == entry.Id);
        Assert.Null(before.AcknowledgedAt);
        Assert.Equal(entry.Timestamp, before.Timestamp);
        events.SelectedItem = before;
        var clickedAt = DateTimeOffset.UtcNow;
        Click(Find<Button>(alerts, "AcknowledgeEvent"));
        await Until(() => events.ItemsSource!.Cast<MonitoringEvent>().Single(e => e.Id == entry.Id).AcknowledgedAt is not null);
        var updated = events.ItemsSource!.Cast<MonitoringEvent>().Single(e => e.Id == entry.Id);
        Assert.InRange(updated.AcknowledgedAt!.Value, clickedAt.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("Acknowledged in Pngr", updated.AcknowledgmentNote);
        Assert.Equal(entry.Timestamp, updated.Timestamp);
        var stored = Assert.Single(await fixture.Engine.Storage.LoadEventsAsync(session.Id), e => e.Id == entry.Id);
        Assert.NotNull(stored.AcknowledgedAt);
        Assert.Equal("Acknowledged in Pngr", stored.AcknowledgmentNote);
        Assert.Equal(entry.Timestamp, stored.Timestamp);
        alerts.Close();
    }

    private static T Find<T>(Window window, string name) where T : Control
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void SetField(MainWindow window, string name, object value)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string method, params object?[] arguments)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static Task InvokeTask(MainWindow window, string method, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(Invoke(window, method, arguments));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The desktop integration operation did not complete.");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public string WorkspacePath => Path.Combine(Directory, "workspace.json");
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        private Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate project test artifacts.");
            Directory = Path.Combine(root.FullName, "artifacts", "tests", "v04-app", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new ImmediateTransport());
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Show();
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await InvokeTask(fixture.Window, "InitializeProfessionalOnOpenAsync");
            await Task.Delay(20);
            return fixture;
        }
        public async Task AddTarget(string target)
        {
            await InvokeTask(Window, "AddTarget", target, new ProbeOptions
            { MaxHops = 2, Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            await Until(() => Engine.Sessions.First(s => s.Target == target).LatestSnapshot is not null);
        }
        public async Task<WorkspaceConfiguration> LoadWorkspace()
            => (await new WorkspaceStore(WorkspacePath).LoadAsync())!;
        public async ValueTask DisposeAsync()
        {
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ImmediateTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
    private sealed class ObservedNotifier : IDesktopNotifier
    {
        public int PermissionRequests { get; private set; }
        public int Sends { get; private set; }
        public Task<NotificationResult> RequestPermissionAsync(CancellationToken token = default)
        { PermissionRequests++; return Task.FromResult(new NotificationResult(false, "Test notifier")); }
        public Task<NotificationResult> SendAsync(string title, string message, CancellationToken token = default)
        { Sends++; return Task.FromResult(new NotificationResult(false, "Test notifier")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
