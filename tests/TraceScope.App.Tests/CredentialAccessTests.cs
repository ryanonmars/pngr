using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class CredentialAccessTests
{
    [AvaloniaTheory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task SettingsNavigationApplyCancelAndReopenNeverAccessSavedSecrets(WebhookFormat format)
    {
        await using var fixture = await Fixture.CreateAsync(format);
        var original = fixture.Configuration.Actions.Webhook;
        var settings = fixture.OpenSettings();
        settings.SelectPage("Notification actions");
        foreach (var category in new[] { "General", "History", "Alerts & notifications", "Notification actions" })
            settings.SelectPage(category);
        var selector = Find<ComboBox>(settings, "SettingsWebhookFormat");
        selector.SelectedIndex = 0;
        selector.SelectedIndex = 1;
        selector.SelectedIndex = 2;
        selector.SelectedIndex = (int)format;
        Assert.Empty(fixture.Secrets.Calls);

        Click(Find<Button>(settings, "SettingsApply"));
        await Until(() => Find<Button>(settings, "SettingsApply").IsEnabled &&
            Find<TextBlock>(settings, "SettingsStatus").Text?.StartsWith("Settings saved.") == true);
        Assert.Empty(fixture.Secrets.Calls);
        Assert.Equal(original, fixture.Configuration.Actions.Webhook);

        Find<ComboBox>(settings, "SettingsWebhookFormat").SelectedIndex = ((int)format + 1) % 3;
        Click(Find<Button>(settings, "SettingsCancel"));
        Assert.Equal(original, fixture.Configuration.Actions.Webhook);
        settings = fixture.OpenSettings();
        settings.SelectPage("Notification actions");
        Assert.Equal((int)format, Find<ComboBox>(settings, "SettingsWebhookFormat").SelectedIndex);
        Assert.Empty(fixture.Secrets.Calls);
        Assert.Equal(original, (await fixture.Store.LoadAsync())!.Actions.Webhook);
        Assert.Empty(fixture.Secrets.Calls);
    }

    [AvaloniaTheory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task OrdinaryAlertRuleCreationAndEditingPersistWithoutAccessingSavedSecrets(WebhookFormat format)
    {
        await using var fixture = await Fixture.CreateAsync(format);
        var original = fixture.Configuration.Actions.Webhook;
        Invoke(fixture.Window, "OpenAlerts", null, new RoutedEventArgs());
        await Until(() => fixture.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var alerts = Assert.IsType<SettingsWindow>(fixture.Window.OwnedWindows.Single());
        Assert.Empty(fixture.Secrets.Calls);
        Click(Find<Button>(alerts, "AlertRuleAdd"));
        Assert.Empty(fixture.Configuration.Alerts);
        Click(Find<Button>(alerts, "SettingsApply"));
        await Until(async () => File.Exists(fixture.Store.Path) && (await fixture.Store.LoadAsync())!.Alerts.Count == 1);
        Assert.Empty(fixture.Secrets.Calls);
        Assert.Equal(original, (await fixture.Store.LoadAsync())!.Actions.Webhook);

        Assert.IsType<AlertRuleRow>(Find<DataGrid>(alerts, "AlertRulesTable").SelectedItem).Enabled = false;
        Find<TextBox>(alerts, "AlertRuleName").Text = "Edited without credentials";
        Click(Find<Button>(alerts, "SettingsApply"));
        await Until(async () => !(await fixture.Store.LoadAsync())!.Alerts.Single().Enabled);
        Assert.Empty(fixture.Secrets.Calls);
        Assert.Equal(original, fixture.Configuration.Actions.Webhook);
        Click(Find<Button>(alerts, "AlertRuleRemove"));
        Click(Find<Button>(alerts, "SettingsApply"));
        await Until(async () => (await fixture.Store.LoadAsync())!.Alerts.Count == 0);
        Assert.Empty(fixture.Secrets.Calls);
    }

    private static Button ContentButton(Window window, string content) => window.GetVisualDescendants()
        .OfType<Button>().Single(b => Equals(b.Content, content));
    private static T Find<T>(Window window, string name) where T : Control
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void SetField(MainWindow window, string name, object value) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Until(Func<bool> predicate) => Until(() => Task.FromResult(predicate()));
    private static async Task Until(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!await predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Settings operation did not complete.");
            await Task.Delay(20);
        }
    }

    private sealed class CountingSecrets : IWebhookSecretStore
    {
        public List<string> Calls { get; } = [];
        public string Store(string value, string? existingId = null)
        { Calls.Add("store"); throw new InvalidOperationException("Unexpected credential write."); }
        public string Retrieve(string id)
        { Calls.Add("read"); throw new InvalidOperationException("Unexpected credential read."); }
        public void Delete(string id)
        { Calls.Add("delete"); throw new InvalidOperationException("Unexpected credential deletion."); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pngr-credential-access", Guid.NewGuid().ToString("N"));
        public CountingSecrets Secrets { get; } = new();
        public WorkspaceStore Store { get; }
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public WorkspaceConfiguration Configuration => Field<WorkspaceConfiguration>(Window, "_configuration");
        private Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new WorkspaceStore(Path.Combine(Directory, "workspace.json"), Secrets);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"));
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Show();
        }
        public static async Task<Fixture> CreateAsync(WebhookFormat format)
        {
            var fixture = new Fixture();
            await (Task)Invoke(fixture.Window, "InitializeProfessionalOnOpenAsync")!;
            SetField(fixture.Window, "_workspaceStore", fixture.Store);
            SetField(fixture.Window, "_configuration", fixture.Configuration with { Actions = new()
            {
                Webhook = new() { Enabled = true, Format = format,
                    TelegramBotTokenSecretId = "11111111111111111111111111111111", TelegramChatId = "12345678901234",
                    UrlSecretId = "22222222222222222222222222222222" }
            } });
            return fixture;
        }
        public SettingsWindow OpenSettings()
        {
            Invoke(Window, "OpenSettings", null, new RoutedEventArgs());
            return Assert.IsType<SettingsWindow>(Window.OwnedWindows.Single());
        }
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
}
