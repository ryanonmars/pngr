using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.App.Updates;

namespace TraceScope.App.Views;

/// <summary>Downloading and restarting require separate, explicit user actions.</summary>
public sealed class UpdateWindow : Window
{
    private readonly TextBlock _status = new() { Name = "UpdateStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { Name = "UpdateNotes", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _download = new() { Name = "UpdateDownload", Content = "Download Update", IsVisible = false };
    private readonly Button _install = new() { Name = "UpdateInstall", Content = "Install and Restart", IsVisible = false, Classes = { "primary" } };
    private readonly Button _later = new() { Name = "UpdateLater", Content = "Later" };
    private readonly Button _releasePage = new() { Name = "UpdateReleasePage", Content = "View Release", IsVisible = false };
    private readonly ProgressBar _progress = new() { Name = "UpdateProgress", Minimum = 0, Maximum = 100, IsVisible = false, Height = 6 };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<UpdateRelease, IProgress<int>, CancellationToken, Task> _fetch;
    private readonly Action _restart;
    private UpdateRelease? _release;
    private bool _busy;
    public bool IsBusy => _busy;

    public UpdateWindow(Func<UpdateRelease, IProgress<int>, CancellationToken, Task> fetch, Action restart)
    {
        _fetch = fetch; _restart = restart;
        Title = "pngr — Updates"; Width = 580; Height = 440; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { _releasePage, _later, _download, _install } };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(18), RowSpacing = 12 };
        body.Children.Add(_status);
        var notes = new ScrollViewer { Content = _notes }; Grid.SetRow(notes, 1); body.Children.Add(notes);
        Grid.SetRow(_progress, 2); body.Children.Add(_progress);
        var warning = new TextBlock { Text = "Unsigned distribution: macOS Gatekeeper and Windows SmartScreen may require your approval. Updates preserve your monitoring data.",
            Foreground = Brush.Parse("#999FA9"), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        Grid.SetRow(warning, 3); body.Children.Add(warning); Grid.SetRow(actions, 4); body.Children.Add(actions); Content = body;
        _later.Click += (_, _) => Close();
        _releasePage.Click += (_, _) =>
        {
            if (_release is null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_release.ReleasePage.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = "Could not open the release page: " + ex.Message; }
        };
        _download.Click += async (_, _) => await DownloadAsync();
        _install.Click += (_, _) =>
        {
            try { _install.IsEnabled = false; _restart(); Close(); }
            catch (Exception ex) { _status.Text = "Could not start the update: " + ex.Message; _install.IsEnabled = true; }
        };
        Closed += (_, _) => _cancellation.Cancel();
    }

    public void ShowResult(UpdateCheckResult result, string? unsupportedReason)
    {
        if (_busy) return;
        _install.IsVisible = false;
        _progress.IsVisible = false;
        _release = result.Release;
        _status.Text = result.Message ?? (result.Status switch
        {
            UpdateCheckStatus.Available => $"pngr {result.Release?.Version} is available",
            UpdateCheckStatus.Current => "pngr is up to date.",
            UpdateCheckStatus.Failed => "Could not check for updates. Please try again later.",
            _ => "Automatic updates were checked recently. Use Settings to check again."
        });
        _notes.Text = result.Release?.Notes ?? "";
        _releasePage.IsVisible = result.Release is not null;
        _download.IsVisible = result.Release is not null && unsupportedReason is null;
        if (result.Release is not null && unsupportedReason is not null)
            _status.Text += "\n" + unsupportedReason;
    }

    public void ShowChecking()
    {
        _install.IsVisible = false; _download.IsVisible = false;
        _status.Text = "Checking for updates…";
    }

    private async Task DownloadAsync()
    {
        if (_busy || _release is null) return;
        _busy = true; _download.IsEnabled = false; _progress.IsVisible = true;
        try
        {
            _status.Text = "Downloading and verifying update…";
            var progress = new Progress<int>(value => Dispatcher.UIThread.Post(() => _progress.Value = value));
            await _fetch(_release, progress, _cancellation.Token);
            if (_cancellation.IsCancellationRequested) return;
            _status.Text = "Update verified. Install and Restart will stop monitoring, save your workspace, and restart pngr.";
            _download.IsVisible = false; _install.IsVisible = true;
        }
        catch (OperationCanceledException) { _status.Text = "Download cancelled. Your installed application is unchanged."; }
        catch (Exception ex) { _status.Text = "Update download failed. Your installed application is unchanged.\n" + ex.Message; }
        finally { _busy = false; _download.IsEnabled = true; }
    }
}
