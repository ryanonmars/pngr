using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TraceScope.App.Updates;
using TraceScope.App.Views;

namespace TraceScope.App;

public partial class MainWindow
{
    private IAppUpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private UpdateRelease? _availableUpdate;
    private Task<UpdateCheckResult>? _updateCheck;
    private readonly DispatcherTimer _automaticUpdateTimer = new() { Interval = UpdateService.AutomaticCheckInterval };

    private void InitializeUpdates()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 7, 3);
        _updates = new UpdateService(_dataDirectory, new Version(version.Major, version.Minor, version.Build));
        _automaticUpdateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
    }

    private void StartAutomaticUpdateChecks()
    {
        if (_closing) return;
        _automaticUpdateTimer.Start();
        _ = CheckForUpdatesAsync(false, bypassThrottle: true);
    }

    private async Task CheckForUpdatesAsync(bool manual, bool bypassThrottle = false)
    {
        if (_closing || _updates is null || _updateWindow?.IsBusy == true) return;
        if (manual)
        {
            OpenUpdateWindow();
            _updateWindow!.ShowChecking();
        }
        // A manual check joins an automatic request already in flight instead of being ignored.
        // Startup always refreshes availability, without opening the manual-check dialog.
        var check = _updateCheck ??= RunUpdateCheckAsync(manual || bypassThrottle);
        try
        {
            var result = await check;
            if (_closing) return;
            if (result.Status is UpdateCheckStatus.Available or UpdateCheckStatus.Skipped or UpdateCheckStatus.Failed && result.Release is { } release)
            {
                _availableUpdate = release;
                UpdateIndicator.Content = $"Update available: v{release.Version}";
                AutomationProperties.SetName(UpdateIndicator, $"Review available update v{release.Version}");
                ToolTip.SetTip(UpdateIndicator, $"Review v{release.Version} release notes and choose whether to download and install. Updates are never installed automatically.");
                UpdateIndicator.IsVisible = true;
            }
            else if (result.Status == UpdateCheckStatus.Current)
            {
                _availableUpdate = null;
                UpdateIndicator.IsVisible = false;
            }
            // A skipped or failed check provides no evidence that an existing update is obsolete.
            if (manual) _updateWindow?.ShowResult(result, _updates.InstallationSupport);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_updateCheck, check)) _updateCheck = null; }
    }

    private async Task<UpdateCheckResult> RunUpdateCheckAsync(bool bypassThrottle)
    {
        try { return await _updates!.CheckAsync(bypassThrottle, _applicationCancellation.Token); }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(UpdateCheckStatus.Failed, Message: "Could not check for updates. Please try again later."); }
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
            _availableUpdate = null;
            UpdateIndicator.IsVisible = false;
            Close();
        });
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show(this);
    }

    private void ViewAvailableUpdate(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updates is null || _closing) return;
        // Preserve an open dialog's download/install state when the indicator is clicked again.
        if (_updateWindow is { IsVisible: true }) { _updateWindow.Activate(); return; }
        OpenUpdateWindow();
        _updateWindow!.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, _availableUpdate,
            $"Pngr {_availableUpdate.Version} is available"), _updates.InstallationSupport);
    }
}
