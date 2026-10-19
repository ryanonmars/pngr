using TraceScope.App.Controls;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WebServerSettingsTests
{
    [AvaloniaFact]
    public async Task DefaultSettingsKeepLocalhostAndDesktopAccessUnauthenticated()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Configuration.WebLanEnabled);
        Assert.True(fixture.Configuration.WebUsesAutomaticInterface);
        Assert.Equal(0, fixture.Get<ComboBox>("SettingsWebNetworkMode").SelectedIndex);
        Assert.False(fixture.Get<Expander>("SettingsWebAdvanced").IsExpanded);
        Assert.True(fixture.Get<RadioButton>("SettingsWebLocalhost").IsChecked);
        Assert.False(fixture.Get<RadioButton>("SettingsWebLan").IsChecked);
        Assert.False(fixture.Get<StackPanel>("SettingsWebLanOptions").IsVisible);
        Assert.Equal(8787m, fixture.Get<SettingsNumberField>("SettingsWebPort").Value);
        Assert.Empty(fixture.Get<TextBox>("SettingsWebAccessKey").Text ?? "");
        // The settings window remains usable without a key or any desktop login.
        fixture.Get<CheckBox>("SettingsWebEnabled").IsChecked = true;
        await fixture.ApplyAsync();
        Assert.True(fixture.Configuration.WebEnabled);
        Assert.False(fixture.Configuration.WebLanEnabled);
        Assert.Null(fixture.Configuration.WebAccessKeyVerifier);
    }

    [AvaloniaFact]
    public async Task LanSelectionGeneratesMaskedKeyAndRequiresExplicitHttpAcknowledgement()
    {
        using var fixture = new Fixture();
        fixture.SelectLan();
        var key = fixture.Get<TextBox>("SettingsWebAccessKey");
        Assert.StartsWith("pngr_", key.Text);
        Assert.Equal(69, key.Text!.Length);
        Assert.Equal('●', key.PasswordChar);
        Assert.True(fixture.Get<StackPanel>("SettingsWebLanOptions").IsVisible);
        Assert.False(fixture.Get<CheckBox>("SettingsWebHttpAcknowledged").IsChecked);
        await fixture.ApplyAsync(success: false);
        Assert.Contains("Acknowledge", fixture.Get<TextBlock>("SettingsStatus").Text);
        Assert.False(fixture.Configuration.WebLanEnabled);
        Assert.Null(fixture.Configuration.WebAccessKeyVerifier);
        fixture.Get<CheckBox>("SettingsWebHttpAcknowledged").IsChecked = true;
        await fixture.ApplyAsync();
        Assert.True(fixture.Configuration.WebLanEnabled);
        Assert.True(WebAccessKey.Verify(key.Text, fixture.Configuration.WebAccessKeyVerifier));
        Assert.True(fixture.Get<Button>("SettingsWebRegenerateKey").IsEnabled);
        Assert.DoesNotContain(key.Text!, System.Text.Json.JsonSerializer.Serialize(fixture.Configuration));
        Assert.DoesNotContain(key.Text!, fixture.Get<TextBlock>("SettingsStatus").Text!);
        Assert.Contains("The key is active", fixture.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Copy this key") == true).Text);
    }

    [AvaloniaFact]
    public void GeneratedKeyIsDiscardedWhenSettingsAreCanceled()
    {
        using var fixture = new Fixture();
        fixture.SelectLan();
        var key = fixture.Get<TextBox>("SettingsWebAccessKey");
        fixture.Get<CheckBox>("SettingsWebRevealKey").IsChecked = true;
        Assert.Equal('\0', key.PasswordChar);
        Click(fixture.Get<Button>("SettingsCancel"));
        Assert.Null(fixture.Configuration.WebAccessKeyVerifier);
        Assert.Null(key.Text);
        Assert.Equal('●', key.PasswordChar);
    }

    [AvaloniaFact]
    public async Task PersistedVerifierCannotRevealOriginalKeyAndLocalhostModeCanBeRestored()
    {
        var originalKey = WebAccessKey.Generate();
        using var fixture = new Fixture(LanConfiguration(originalKey));
        Assert.Equal(1, fixture.Get<ComboBox>("SettingsWebNetworkMode").SelectedIndex);
        Assert.True(fixture.Get<Expander>("SettingsWebAdvanced").IsExpanded);
        Assert.Empty(fixture.Get<TextBox>("SettingsWebAccessKey").Text ?? "");
        Assert.False(fixture.Get<Button>("SettingsWebCopyKey").IsEnabled);
        Assert.False(fixture.Get<CheckBox>("SettingsWebRevealKey").IsEnabled);
        Assert.True(fixture.Get<Button>("SettingsWebRegenerateKey").IsEnabled);
        fixture.Get<RadioButton>("SettingsWebLocalhost").IsChecked = true;
        await fixture.ApplyAsync();
        Assert.False(fixture.Configuration.WebLanEnabled);
        Assert.True(WebAccessKey.Verify(originalKey, fixture.Configuration.WebAccessKeyVerifier));
        Assert.False(fixture.Get<StackPanel>("SettingsWebLanOptions").IsVisible);
    }

    [AvaloniaFact]
    public async Task RegenerationRequiresConfirmationAndPersistsNewVerifierImmediately()
    {
        var oldKey = WebAccessKey.Generate();
        using var fixture = new Fixture(LanConfiguration(oldKey));
        Click(fixture.Get<Button>("SettingsWebRegenerateKey"));
        var confirmation = Assert.Single(fixture.Window.OwnedWindows);
        Assert.Equal("Regenerate access key?", confirmation.Title);
        Assert.Contains(confirmation.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("stops existing API integrations") == true);
        Click(Find<Button>(confirmation, "CancelWebKeyRotation"));
        Assert.True(WebAccessKey.Verify(oldKey, fixture.Configuration.WebAccessKeyVerifier));
        Assert.Equal(0, fixture.Applies);
        Click(fixture.Get<Button>("SettingsWebRegenerateKey"));
        confirmation = Assert.Single(fixture.Window.OwnedWindows);
        Click(Find<Button>(confirmation, "ConfirmWebKeyRotation"));
        await Until(() => fixture.Applies == 1 && fixture.Get<Button>("SettingsApply").IsEnabled);
        var newKey = fixture.Get<TextBox>("SettingsWebAccessKey").Text;
        Assert.True(WebAccessKey.Verify(newKey, fixture.Configuration.WebAccessKeyVerifier));
        Assert.False(WebAccessKey.Verify(oldKey, fixture.Configuration.WebAccessKeyVerifier));
        Assert.Contains("previous keys and browser sessions are invalid", fixture.Get<TextBlock>("SettingsStatus").Text);
    }

    [AvaloniaFact]
    public async Task FailedRotationRestoresPreviousVerifierAndDoesNotDisplayUnappliedKey()
    {
        var oldKey = WebAccessKey.Generate();
        using var fixture = new Fixture(LanConfiguration(oldKey)) { RejectChanges = true };
        Click(fixture.Get<Button>("SettingsWebRegenerateKey"));
        Click(Find<Button>(Assert.Single(fixture.Window.OwnedWindows), "ConfirmWebKeyRotation"));
        await Until(() => fixture.Get<TextBlock>("SettingsStatus").Text?.StartsWith("Settings were not applied:") == true);
        Assert.True(WebAccessKey.Verify(oldKey, fixture.Configuration.WebAccessKeyVerifier));
        Assert.Empty(fixture.Get<TextBox>("SettingsWebAccessKey").Text ?? "");
        Assert.Contains("Port is unavailable", fixture.Get<TextBlock>("SettingsStatus").Text);
    }

    [AvaloniaFact]
    public async Task MultipleInterfacesRequireASelectionAndShowTheChosenAddress()
    {
        using var fixture = new Fixture();
        fixture.Get<RadioButton>("SettingsWebLan").IsChecked = true;
        fixture.Get<ComboBox>("SettingsWebNetworkMode").SelectedIndex = 1;
        var interfaces = fixture.Get<ComboBox>("SettingsWebInterface");
        interfaces.ItemsSource = new[] { new WebServerAddress("Ethernet", "192.168.30.50"), new WebServerAddress("Wi-Fi", "10.1.2.3") };
        interfaces.SelectedItem = null;
        fixture.Get<CheckBox>("SettingsWebHttpAcknowledged").IsChecked = true;
        await fixture.ApplyAsync(success: false);
        Assert.Contains("Select a LAN interface", fixture.Get<TextBlock>("SettingsStatus").Text);
        interfaces.SelectedIndex = 1;
        fixture.Get<SettingsNumberField>("SettingsWebPort").Value = 8799;
        Assert.Equal("http://10.1.2.3:8799/", fixture.Get<TextBlock>("SettingsWebAddress").Text);
        await fixture.ApplyAsync();
        Assert.Equal("10.1.2.3", fixture.Configuration.WebLanAddress);
        Assert.Equal(8799, fixture.Configuration.WebPort);
        Assert.False(fixture.Configuration.WebLanAutomatic);
    }

    [AvaloniaFact]
    public async Task AutomaticSelectionNeedsNoTechnicalInterfaceChoiceAndPersistsMode()
    {
        using var fixture = new Fixture();
        fixture.Get<CheckBox>("SettingsWebEnabled").IsChecked = true;
        fixture.Get<RadioButton>("SettingsWebLan").IsChecked = true;
        fixture.Get<CheckBox>("SettingsWebHttpAcknowledged").IsChecked = true;
        await Until(() => fixture.Get<TextBlock>("SettingsWebSelectedAddress").Text == "192.168.30.20");
        Assert.False(fixture.Get<Expander>("SettingsWebAdvanced").IsExpanded);
        await fixture.ApplyAsync();
        Assert.True(fixture.Configuration.WebLanAutomatic);
        Assert.Equal("192.168.30.20", fixture.Configuration.WebLanAddress);
        Assert.True(fixture.LastChange!.ReevaluateWebInterface);
    }

    [AvaloniaFact]
    public async Task UnavailableAutomaticNetworkOffersManualSelection()
    {
        using var fixture = new Fixture(automaticAddress: _ => Task.FromResult<WebServerAddress?>(null));
        fixture.Get<RadioButton>("SettingsWebLan").IsChecked = true;
        Assert.Contains("Choose manually", fixture.Get<TextBlock>("SettingsWebSelectionHelp").Text);
        Assert.Equal("No suitable network found", fixture.Get<TextBlock>("SettingsWebSelectedAddress").Text);
        fixture.SelectLan();
        fixture.Get<CheckBox>("SettingsWebHttpAcknowledged").IsChecked = true;
        await fixture.ApplyAsync();
        Assert.False(fixture.Configuration.WebLanAutomatic);
        Assert.Equal("192.168.30.50", fixture.Configuration.WebLanAddress);
    }

    [AvaloniaFact]
    public async Task UnrelatedSettingsApplyDoesNotRequestRunningAutomaticNetworkChange()
    {
        var key = WebAccessKey.Generate();
        using var fixture = new Fixture(LanConfiguration(key) with { WebLanAutomatic = true },
            serverAddress: () => "http://192.168.30.50:8787/");
        fixture.Window.SelectPage("General");
        await fixture.ApplyAsync();
        Assert.False(fixture.LastChange!.ReevaluateWebInterface);
        fixture.Window.SelectPage("Web Server");
        Assert.Equal("192.168.30.50", fixture.Get<TextBlock>("SettingsWebSelectedAddress").Text);
    }

    [AvaloniaFact]
    public void KeyFieldAndActionsRemainSeparateAtSupportedWindowWidths()
    {
        using var fixture = new Fixture();
        fixture.SelectLan();
        foreach (var width in new[] { 760d, 830d })
        {
            fixture.Window.Width = width;
            Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
            Rect Bounds(Control control) => new(control.TranslatePoint(default, fixture.Window)!.Value, control.Bounds.Size);
            var key = Bounds(fixture.Get<TextBox>("SettingsWebAccessKey"));
            var copy = Bounds(fixture.Get<Button>("SettingsWebCopyKey"));
            var regenerate = Bounds(fixture.Get<Button>("SettingsWebRegenerateKey"));
            var reveal = Bounds(fixture.Get<CheckBox>("SettingsWebRevealKey"));
            Assert.InRange(key.Width, 200, 300);
            Assert.InRange(Math.Abs(key.Left - copy.Left), 0, 1);
            Assert.True(key.Bottom <= copy.Top, "Key text and actions must occupy separate rows.");
            Assert.True(copy.Right <= regenerate.Left && regenerate.Right <= reveal.Left,
                "Copy, regenerate and reveal controls must not overlap.");
            Assert.True(reveal.Right < width - 16, "Key actions must remain inside the settings content.");
        }
    }

    [AvaloniaFact]
    public void StatusShowsRunningAddressRatherThanUnappliedDraft()
    {
        using var fixture = new Fixture(serverAddress: () => "http://127.0.0.1:8787/");
        fixture.SelectLan();
        Assert.Equal("http://127.0.0.1:8787/", fixture.Get<TextBlock>("SettingsWebAddress").Text);
        Assert.True(fixture.Get<Button>("SettingsWebCopyAddress").IsEnabled);
    }

    [AvaloniaFact]
    public void UnavailableInterfaceErrorIsVisibleAndAddressCopyIsDisabled()
    {
        using var fixture = new Fixture(serverError: () => "The selected LAN interface is unavailable. Refresh interfaces and Apply.");
        Assert.Contains("selected LAN interface is unavailable", fixture.Get<TextBlock>("SettingsWebRunning").Text);
        Assert.False(fixture.Get<Button>("SettingsWebCopyAddress").IsEnabled);
    }

    private static WorkspaceConfiguration LanConfiguration(string key) => new()
    {
        WebEnabled = true, WebLanEnabled = true, WebLanAddress = "192.168.30.50", WebHttpAcknowledged = true,
        WebAccessKeyVerifier = WebAccessKey.CreateVerifier(key)
    };
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Concat(window.GetLogicalDescendants().OfType<T>())
            .Distinct().Single(control => control.Name == name);
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        { if (DateTime.UtcNow > end) throw new TimeoutException("Web settings operation did not complete."); await Task.Delay(10); }
    }

    private sealed class Fixture : IDisposable
    {
        public WorkspaceConfiguration Configuration { get; private set; }
        public SettingsWindow Window { get; }
        public int Applies { get; private set; }
        public SettingsChange? LastChange { get; private set; }
        public bool RejectChanges { get; init; }
        public Fixture(WorkspaceConfiguration? configuration = null, Func<string?>? serverAddress = null, Func<string?>? serverError = null,
            Func<CancellationToken, Task<WebServerAddress?>>? automaticAddress = null)
        {
            Configuration = configuration ?? new WorkspaceConfiguration();
            Window = new SettingsWindow(Configuration, null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-web-settings",
                change =>
                {
                    if (RejectChanges) throw new IOException("Port is unavailable.");
                    Configuration = change.Configuration; LastChange = change; Applies++;
                    return Task.CompletedTask;
                }, () => Task.FromResult(new NotificationResult(true, "Unused")), _ => { }, serverAddress: serverAddress, serverError: serverError,
                automaticAddress: automaticAddress ?? (_ => Task.FromResult<WebServerAddress?>(new("Wi-Fi", "192.168.30.20"))));
            Window.Show(); Window.SelectPage("Web Server");
        }
        public T Get<T>(string name) where T : Control => Find<T>(Window, name);
        public void SelectLan()
        {
            Get<CheckBox>("SettingsWebEnabled").IsChecked = true;
            Get<RadioButton>("SettingsWebLan").IsChecked = true;
            Get<ComboBox>("SettingsWebNetworkMode").SelectedIndex = 1;
            var interfaces = Get<ComboBox>("SettingsWebInterface");
            interfaces.ItemsSource = new[] { new WebServerAddress("Test LAN", "192.168.30.50") };
            interfaces.SelectedIndex = 0;
        }
        public async Task ApplyAsync(bool success = true)
        {
            var status = Get<TextBlock>("SettingsStatus"); status.Text = "";
            Click(Get<Button>("SettingsApply"));
            await Until(() => Get<Button>("SettingsApply").IsEnabled && !string.IsNullOrEmpty(status.Text));
            Assert.StartsWith(success ? "Settings saved." : "Settings were not applied:", status.Text);
        }
        public void Dispose()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close();
        }
    }
}
