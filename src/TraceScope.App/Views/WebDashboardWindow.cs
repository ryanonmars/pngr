using System.Diagnostics;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TraceScope.App.Views;

/// <summary>Observes the existing listener; never starts or configures it.</summary>
public sealed class WebDashboardWindow : Window
{
    private readonly Func<(string? Url, string? Error, bool Lan)> _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _status = new() { Name = "WebStatus", FontSize = 14 };
    private readonly TextBlock _mode = new() { Name = "WebAccessMode", FontSize = 12 };
    private readonly SelectableTextBlock _address = SettingsUi.Location("");
    private readonly TextBlock _message = SettingsUi.Note("");
    private readonly Button _open = new() { Name = "WebOpenDashboard", Content = "Open Dashboard" };
    private readonly Button _copy = new() { Name = "WebCopyAddress", Content = "Copy Address" };
    private Uri? _active;
    private (string? Url, string? Error, bool Lan)? _lastState;

    public WebDashboardWindow(Func<(string? Url, string? Error, bool Lan)> state, Action openSettings,
        Action<Uri>? openBrowser = null, Func<string, Task>? copyAddress = null)
    {
        _state = state;
        Title = "Local read-only web interface";
        Width = 510; Height = 310; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SettingsUi.Apply(this);
        _address.Name = "WebAddress";
        _message.Name = "WebStatusMessage";
        var settings = new Button { Name = "WebServerSettings", Content = "Web Server Settings…" };
        settings.Click += (_, _) => { Close(); openSettings(); };
        _open.Click += (_, _) =>
        {
            RefreshStatus();
            if (_active is not { } uri) return;
            try { (openBrowser ?? (url => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })))(uri); }
            catch { _message.Text = "Could not open your browser. Copy the address and open it manually."; }
        };
        _copy.Click += async (_, _) =>
        {
            RefreshStatus();
            if (_active is not { } uri) return;
            try
            {
                if (copyAddress is not null) await copyAddress(uri.AbsoluteUri);
                else if (Clipboard is { } clipboard) await clipboard.SetTextAsync(uri.AbsoluteUri);
                else { _message.Text = "Clipboard unavailable. Select and copy the address above."; return; }
                _message.Text = "Address copied.";
            }
            catch { _message.Text = "Could not copy the address. Select and copy it above."; }
        };
        Content = new ScrollViewer { Background = Brush.Parse("#191A1D"), Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = {
            SettingsUi.Heading("Web dashboard"),
            SettingsUi.Columns(SettingsUi.Field("Server status", _status), SettingsUi.Field("Access mode", _mode)),
            SettingsUi.Field("Server URL", _address),
            SettingsUi.Actions(_open, _copy), _message, SettingsUi.Divider(), settings
        } } };
        _timer.Tick += (_, _) => RefreshStatus();
        Opened += (_, _) => { RefreshStatus(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var state = _state();
        if (_lastState == state) return;
        _lastState = state;
        var (url, error, lan) = state;
        _active = null;
        // Only a concrete listener address is actionable; never invent a URL from settings.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !ip.Equals(IPAddress.Any)
            && !ip.Equals(IPAddress.IPv6Any) && uri.UserInfo.Length == 0 && uri.Query.Length == 0
            && uri.Fragment.Length == 0 && uri.AbsolutePath == "/")
            _active = uri;
        var failed = !string.IsNullOrWhiteSpace(error) || (url is not null && _active is null);
        _status.Text = failed ? "Error" : _active is not null ? "Running" : "Stopped";
        _mode.Text = _active is not null
            ? IPAddress.IsLoopback(IPAddress.Parse(_active.Host.Trim('[', ']'))) ? "Localhost" : "LAN"
            : (lan ? "LAN" : "Localhost") + " (configured)";
        _address.Text = !failed && _active is not null ? _active.AbsoluteUri : "Not available";
        _open.IsEnabled = _copy.IsEnabled = !failed && _active is not null;
        _message.Text = failed ? "Server unavailable. Check the selected network and configuration in Web Server Settings."
            : _active is null ? "Start the server in Web Server Settings."
            : "Available while Pngr is running. Dashboard access is read-only.";
    }
}
