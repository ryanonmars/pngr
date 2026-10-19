using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WebDashboardWindowTests
{
    [AvaloniaTheory]
    [InlineData(null, null, false, "Stopped", "Localhost (configured)", false)]
    [InlineData(null, null, true, "Stopped", "LAN (configured)", false)]
    [InlineData(null, "Interface unavailable", true, "Error", "LAN (configured)", false)]
    [InlineData("invalid", null, true, "Error", "LAN (configured)", false)]
    [InlineData("http://0.0.0.0:8787/", null, true, "Error", "LAN (configured)", false)]
    [InlineData("file:///tmp/example", null, false, "Error", "Localhost (configured)", false)]
    [InlineData("http://secret@127.0.0.1:8787/", null, false, "Error", "Localhost (configured)", false)]
    [InlineData("http://127.0.0.1:8787/", null, true, "Running", "Localhost", true)]
    [InlineData("http://192.168.1.20:8787/", null, false, "Running", "LAN", true)]
    [InlineData("http://[::1]:8787/", null, false, "Running", "Localhost", true)]
    public void StatusUsesActualListenerAndGuardsUnavailableAddresses(string? url, string? error, bool lan,
        string status, string mode, bool actionable)
    {
        var window = new WebDashboardWindow(() => (url, error, lan), () => { });
        window.Show();
        Assert.Equal(status, Find<TextBlock>(window, "WebStatus").Text);
        Assert.Equal(mode, Find<TextBlock>(window, "WebAccessMode").Text);
        Assert.Equal(actionable, Find<Button>(window, "WebOpenDashboard").IsEnabled);
        Assert.Equal(actionable, Find<Button>(window, "WebCopyAddress").IsEnabled);
        Assert.Empty(window.GetVisualDescendants().OfType<NumericUpDown>());
        Assert.Empty(window.GetVisualDescendants().OfType<CheckBox>());
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "WebApply");
        window.Close();
    }

    [AvaloniaFact]
    public void QuickActionsRecheckActiveUrlAndSettingsClosesDialog()
    {
        string? url = "http://127.0.0.1:8787/";
        Uri? opened = null; string? copied = null; var settings = false;
        var window = new WebDashboardWindow(() => (url, null, false), () => settings = true,
            uri => opened = uri, text => { copied = text; return Task.CompletedTask; });
        window.Show();
        url = "http://192.168.1.25:9010/";
        Click(window, "WebOpenDashboard"); Click(window, "WebCopyAddress");
        Assert.Equal(url, opened!.AbsoluteUri); Assert.Equal(url, copied);
        url = null; window.RefreshStatus();
        Assert.False(Find<Button>(window, "WebOpenDashboard").IsEnabled);
        Assert.Equal("Not available", Find<SelectableTextBlock>(window, "WebAddress").Text);
        Click(window, "WebServerSettings"); Assert.True(settings); Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void BrowserFailureIsActionableAndSurvivesStatusRefresh()
    {
        var window = new WebDashboardWindow(() => ("http://127.0.0.1:8787/", null, false), () => { },
            _ => throw new InvalidOperationException("sensitive platform detail"));
        window.Show(); Click(window, "WebOpenDashboard"); window.RefreshStatus();
        Assert.Equal("Could not open your browser. Copy the address and open it manually.", Find<TextBlock>(window, "WebStatusMessage").Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(510, 310)]
    [InlineData(400, 300)]
    public void ActionsRemainVisibleAtNormalAndMinimumSizes(double width, double height)
    {
        var window = new WebDashboardWindow(() => ("http://[2001:db8::1234]:8787/", null, true), () => { })
            { Width = width, Height = height };
        window.Show(); window.UpdateLayout();
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            var point = button.TranslatePoint(new Avalonia.Point(), window)!.Value;
            Assert.InRange(point.X, 0, width - button.Bounds.Width);
            Assert.InRange(point.Y, 0, height - button.Bounds.Height);
        }
        window.Close();
    }
    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window w, string name) => Find<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
