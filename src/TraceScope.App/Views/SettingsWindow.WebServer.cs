using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed partial class SettingsWindow
{
    private readonly RadioButton _webLocal = new() { Name = "SettingsWebLocalhost", Content = "Localhost Only", GroupName = "WebAccessMode" };
    private readonly RadioButton _webLan = new() { Name = "SettingsWebLan", Content = "Local Network (LAN)", GroupName = "WebAccessMode" };
    private readonly ComboBox _webInterface = new() { Name = "SettingsWebInterface", MinWidth = 270, MaxWidth = 360 };
    private readonly CheckBox _webAcknowledged = Toggle("SettingsWebHttpAcknowledged", "I understand LAN HTTP exposes the access key and monitoring data to anyone who can observe this network.");
    private readonly TextBox _webKey = new() { Name = "SettingsWebAccessKey", IsReadOnly = true, PasswordChar = '●', MaxWidth = 350, Watermark = "Key stored as a verifier" };
    private readonly CheckBox _webReveal = Toggle("SettingsWebRevealKey", "Show key");
    private readonly Button _webCopyKey = new() { Name = "SettingsWebCopyKey", Content = "Copy Key" };
    private readonly Button _webRegenerate = new() { Name = "SettingsWebRegenerateKey", Content = "Regenerate Key" };
    private readonly TextBlock _webKeyHelp = Note("");
    private readonly TextBlock _webAddress = new() { Name = "SettingsWebAddress", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _webRunning = Note("");
    private readonly Button _webCopyAddress = new() { Name = "SettingsWebCopyAddress", Content = "Copy Address" };
    private readonly StackPanel _webLanOptions = new() { Name = "SettingsWebLanOptions", Spacing = 13 };
    private Func<string?>? _serverAddress;
    private Func<string?>? _serverError;
    private string? _webVerifier;
    private bool _webSaved;
    private bool _initializingWeb;
    private readonly DispatcherTimer _webStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeWebSettings(WorkspaceConfiguration configuration, Func<string?>? serverAddress, Func<string?>? serverError)
    {
        _initializingWeb = true;
        _serverAddress = serverAddress;
        _serverError = serverError;
        _webRunning.Name = "SettingsWebRunning";
        _webVerifier = configuration.WebAccessKeyVerifier;
        _webLocal.IsChecked = !configuration.WebLanEnabled;
        _webLan.IsChecked = configuration.WebLanEnabled;
        _webAcknowledged.IsChecked = configuration.WebHttpAcknowledged;
        ReloadWebInterfaces(configuration.WebLanAddress);
        ToolTip.SetTip(_webInterface, "Bind only to this assigned private address. VPN interfaces may appear; select the network you intend to share with. Refresh after changing networks.");
        ToolTip.SetTip(_webPort, "TCP port used for both the read-only dashboard and REST API. A firewall rule may be needed on this computer.");
        ToolTip.SetTip(_webKey, "Only a verifier is saved. A previously saved key cannot be recovered; regenerate it if lost.");
        _webAcknowledged.MaxWidth = 510;
        _webAcknowledged.Content = new TextBlock { Text = (string)_webAcknowledged.Content!, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 };
        _webLan.IsCheckedChanged += (_, _) =>
        {
            if (!_initializingWeb && _webLan.IsChecked == true && _webVerifier is null) GenerateWebKey();
            UpdateWebControls();
        };
        _webLocal.IsCheckedChanged += (_, _) => UpdateWebControls();
        _web.IsCheckedChanged += (_, _) => UpdateWebControls();
        _webInterface.SelectionChanged += (_, _) => UpdateWebAddress();
        _webPort.ValueChanged += (_, _) => UpdateWebAddress();
        _webReveal.IsCheckedChanged += (_, _) => _webKey.PasswordChar = _webReveal.IsChecked == true ? '\0' : '●';
        _webCopyKey.Click += async (_, _) => await CopyWebTextAsync(_webKey.Text, "Access key copied.");
        _webCopyAddress.Click += async (_, _) => await CopyWebTextAsync(_serverAddress?.Invoke(), "Running server address copied.");
        _webRegenerate.Click += async (_, _) => await ConfirmWebKeyRotationAsync();
        _webStatusTimer.Tick += (_, _) => UpdateWebAddress();
        Opened += (_, _) => _webStatusTimer.Start();
        Closed += (_, _) => { _webStatusTimer.Stop(); _webKey.Text = null; _webReveal.IsChecked = false; };
        _initializingWeb = false;
        UpdateWebControls();
    }

    private Control BuildWebPage()
    {
        var refresh = new Button { Name = "SettingsWebRefreshInterfaces", Content = "Refresh interfaces" };
        refresh.Click += (_, _) => ReloadWebInterfaces((_webInterface.SelectedItem as WebServerAddress)?.Address);
        foreach (var control in new Control[] {
            Row("LAN interface", _webInterface), refresh,
            Heading("Authentication"), Note("Generated Access Key · required for browser and API access in LAN mode, including localhost clients."),
            Row("Access Key", _webKey), _webReveal,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _webCopyKey, _webRegenerate } }, _webKeyHelp,
            _webAcknowledged, Note("HTTP is not encrypted. Use a trusted network or an encrypted VPN. For HTTPS, use your own reverse proxy; no external service is required.") })
            _webLanOptions.Children.Add(control);
        return Page("Web Server", "Read-only dashboard and REST API. The desktop application never requires web authentication.",
            _web, Row("Access Mode", new StackPanel { Spacing = 7, Children = { _webLocal, _webLan } }),
            Row("Port", _webPort), _webLanOptions,
            Heading("Server Address"), _webAddress, _webRunning, _webCopyAddress,
            Note("Apply starts, stops or rebinds the server. It runs only while Pngr is running. LAN access does not automatically enable internet access, open router ports or change firewall rules."),
            ToolButton("Apply & open dashboard controls…", "web"));
    }

    private void ReloadWebInterfaces(string? address)
    {
        try
        {
            var choices = WebServerNetwork.GetLanAddresses().ToList();
            if (address is not null && choices.All(a => a.Address != address))
                choices.Add(new WebServerAddress("Saved interface (unavailable)", address));
            _webInterface.ItemsSource = choices;
            // A choice is never made silently on multi-interface machines.
            _webInterface.SelectedItem = choices.FirstOrDefault(a => a.Address == address) ?? (choices.Count == 1 ? choices[0] : null);
            UpdateWebAddress();
        }
        catch
        {
            _webInterface.ItemsSource = Array.Empty<WebServerAddress>();
            _webInterface.SelectedItem = null;
            _status.Text = "Network interfaces could not be read. Refresh interfaces after reconnecting.";
        }
    }

    private void GenerateWebKey()
    {
        var key = WebAccessKey.Generate();
        _webVerifier = WebAccessKey.CreateVerifier(key);
        _webKey.Text = key;
        _webReveal.IsChecked = false;
        _webSaved = false;
    }

    private void UpdateWebControls()
    {
        var lan = _webLan.IsChecked == true;
        _webLanOptions.IsVisible = lan;
        _webInterface.IsEnabled = lan;
        _webAcknowledged.IsEnabled = lan;
        _webCopyKey.IsEnabled = !string.IsNullOrEmpty(_webKey.Text);
        _webReveal.IsEnabled = _webCopyKey.IsEnabled;
        _webRegenerate.IsEnabled = _webVerifier is not null && !_busy;
        _webKeyHelp.Text = string.IsNullOrEmpty(_webKey.Text)
            ? _webVerifier is null ? "A key is generated when you select LAN access. Only its verifier is saved."
                : "The saved key cannot be displayed again. Regenerate it if lost; existing clients will need the new key."
            : "Copy this key before closing Settings; it cannot be recovered later. " + (_webSaved ? "The key is active." : "Press Apply to activate it.");
        UpdateWebAddress();
    }

    private void UpdateWebAddress()
    {
        var running = _serverAddress?.Invoke();
        var address = _webLan.IsChecked == true ? (_webInterface.SelectedItem as WebServerAddress)?.Address : "127.0.0.1";
        var host = address?.Contains(':') == true ? "[" + address + "]" : address;
        _webAddress.Text = running ?? (host is null ? "Select an available LAN interface." : $"http://{host}:{_webPort.Value ?? 8787}/");
        _webRunning.Text = running is null ? _serverError?.Invoke() ?? "Stopped. The selected address becomes active after Apply." : "Running at the address above. Apply to activate any changes.";
        _webCopyAddress.IsEnabled = running is not null;
    }

    private void ValidateWebDraft()
    {
        if (_webLan.IsChecked != true) return;
        if (_webInterface.SelectedItem is not WebServerAddress)
            throw new InvalidDataException("Select a LAN interface. Refresh interfaces if your network has changed.");
        if (_webAcknowledged.IsChecked != true)
            throw new InvalidDataException("Acknowledge the unencrypted HTTP risk before enabling LAN access.");
        if (_webVerifier is null) GenerateWebKey();
    }

    private async Task ConfirmWebKeyRotationAsync()
    {
        if (_busy) return;
        var dialog = new Window { Name = "WebKeyRotationConfirmation", Title = "Regenerate access key?", Width = 460, Height = 270,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#191A1D") };
        var confirm = new Button { Name = "ConfirmWebKeyRotation", Content = "Regenerate Key", Classes = { "primary" } };
        var cancel = new Button { Name = "CancelWebKeyRotation", Content = "Cancel" };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 15, Children = {
            Heading("Replace the current access key?"),
            Note("This saves your current settings, immediately signs out all browsers and stops existing API integrations until they use the new key."),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, confirm } } } };
        if (!await dialog.ShowDialog<bool>(this)) return;
        var previousVerifier = _webVerifier;
        var previousKey = _webKey.Text;
        var previousSaved = _webSaved;
        GenerateWebKey();
        if (!await SaveAsync(false))
        { _webVerifier = previousVerifier; _webKey.Text = previousKey; _webSaved = previousSaved; }
        else _status.Text = "Settings saved. New key active; previous keys and browser sessions are invalid. Copy the new key before closing Settings.";
        UpdateWebControls();
    }

    private async Task CopyWebTextAsync(string? text, string message)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (Clipboard is not { } clipboard) { _status.Text = "Clipboard is unavailable."; return; }
            await clipboard.SetTextAsync(text);
            _status.Text = message;
        }
        catch { _status.Text = "Clipboard is unavailable."; }
    }
}
