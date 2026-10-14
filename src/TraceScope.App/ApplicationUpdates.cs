using Avalonia.Interactivity;
using TraceScope.App.Updates;
using TraceScope.App.Views;

namespace TraceScope.App;

public partial class MainWindow
{
    private IAppUpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private UpdateRelease? _availableUpdate;
    private bool _checkingUpdate;

    private void InitializeUpdates()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 7, 1);
        _updates = new UpdateService(_dataDirectory, new Version(version.Major, version.Minor, version.Build));
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_closing || _checkingUpdate || _updates is null || _updateWindow?.IsBusy == true) return;
        _checkingUpdate = true;
        if (manual)
        {
            OpenUpdateWindow();
            _updateWindow!.ShowChecking();
        }
        try
        {
            var result = await _updates.CheckAsync(manual, _applicationCancellation.Token);
            if (_closing) return;
            if (manual) _updateWindow?.ShowResult(result, _updates.InstallationSupport);
            if (result.Status == UpdateCheckStatus.Available && result.Release is { } release)
            {
                _availableUpdate = release;
                UpdateNoticeText.Text = $"Pngr {release.Version} is available";
                UpdateNotice.IsVisible = !manual;
            }
        }
        catch (OperationCanceledException) { }
        finally { _checkingUpdate = false; }
    }

    private void OpenUpdateWindow()
    {
        if (_updateWindow is { IsVisible: true }) { _updateWindow.Activate(); return; }
        _updateWindow = new UpdateWindow(async (release, progress, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _applicationCancellation.Token);
            await _updates!.DownloadAsync(release, progress, linked.Token);
        }, () =>
        {
            // The updater waits for normal process exit: existing shutdown saves history and stops monitoring.
            _updates!.PrepareInstall();
            Close();
        });
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show(this);
    }

    private void ViewAvailableUpdate(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updates is null) return;
        UpdateNotice.IsVisible = false;
        OpenUpdateWindow();
        _updateWindow!.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, _availableUpdate,
            $"Pngr {_availableUpdate.Version} is available"), _updates.InstallationSupport);
    }

    private void DismissUpdateNotice(object? sender, RoutedEventArgs e) => UpdateNotice.IsVisible = false;
}
