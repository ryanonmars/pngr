using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Updates;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class UpdateWindowTests
{
    private static readonly UpdateRelease Release = new(new Version(0, 7, 2), "Release notes: test update.",
        new Uri("https://github.com/ryanonmars/pngr/releases/tag/v0.7.2"),
        new Uri("https://github.com/ryanonmars/pngr/releases/download/v0.7.2/"));

    [AvaloniaFact]
    public async Task DownloadAndInstallRequireSeparateApprovalAndDisplayNotes()
    {
        var downloads = 0; var installs = 0;
        var window = new UpdateWindow((_, _, _) => { downloads++; return Task.CompletedTask; }, () => installs++);
        window.Show();
        window.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, Release, "Available"), null);
        Assert.Equal(Release.Notes, Find<TextBlock>(window, "UpdateNotes").Text);
        Assert.Equal(0, downloads); Assert.Equal(0, installs);
        Assert.False(Find<Button>(window, "UpdateInstall").IsVisible);
        Click(window, "UpdateDownload");
        await Until(() => Find<Button>(window, "UpdateInstall").IsVisible);
        Assert.Equal(1, downloads); Assert.Equal(0, installs);
        Click(window, "UpdateInstall");
        Assert.Equal(1, installs);
    }

    [AvaloniaFact]
    public async Task DownloadFailureKeepsInstallationUntouchedAndAllowsRetry()
    {
        var installs = 0;
        var window = new UpdateWindow((_, _, _) => Task.FromException(new IOException("Checksum mismatch")), () => installs++);
        window.Show(); window.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, Release), null);
        Click(window, "UpdateDownload");
        await Until(() => Find<TextBlock>(window, "UpdateStatus").Text!.Contains("failed"));
        Assert.Equal(0, installs);
        Assert.False(Find<Button>(window, "UpdateInstall").IsVisible);
        Assert.True(Find<Button>(window, "UpdateDownload").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void UnsupportedInstallationShowsExplanationAndNoInstallAction()
    {
        var window = new UpdateWindow((_, _, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        window.Show(); window.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, Release), "Move Pngr to Applications.");
        Assert.Contains("Move Pngr", Find<TextBlock>(window, "UpdateStatus").Text);
        Assert.False(Find<Button>(window, "UpdateDownload").IsVisible);
        Assert.True(Find<Button>(window, "UpdateReleasePage").IsVisible);
        Click(window, "UpdateLater");
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task RecheckingDoesNotLeaveAStaleInstallButton()
    {
        var window = new UpdateWindow((_, _, _) => Task.CompletedTask, () => throw new InvalidOperationException());
        window.Show(); window.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Available, Release), null);
        Click(window, "UpdateDownload");
        await Until(() => Find<Button>(window, "UpdateInstall").IsVisible);
        window.ShowChecking();
        Assert.False(Find<Button>(window, "UpdateInstall").IsVisible);
        window.ShowResult(new UpdateCheckResult(UpdateCheckStatus.Current), null);
        Assert.Equal("Pngr is up to date.", Find<TextBlock>(window, "UpdateStatus").Text);
        Assert.False(Find<Button>(window, "UpdateDownload").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void GeneralSettingsNoLongerContainsAnUpdateButton()
    {
        var window = new SettingsWindow(new WorkspaceConfiguration(), null, null,
            [new ProbeSourceChoice("Automatic", null, null)], "test-data", _ => Task.CompletedTask,
            () => Task.FromResult(new NotificationResult(true, "test")), _ => { });
        window.Show();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "SettingsCheckUpdates");
        window.Close();
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(predicate());
    }
}
