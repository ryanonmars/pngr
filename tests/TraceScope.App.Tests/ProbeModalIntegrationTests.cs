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

public sealed class ProbeModalIntegrationTests
{
    [AvaloniaFact]
    public async Task ProtocolDraftUpdatesVisibilityWithoutChangingTargetAndCancelDiscardsEdits()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        var original = target.Session!.Options;
        var dialog = f.Open();
        var protocol = Find<ComboBox>(dialog, "ProbeProtocol");
        Assert.False(Find<TextBox>(dialog, "ProbeTcpPort").IsVisible);
        Assert.False(Find<TextBox>(dialog, "ProbeUdpPort").IsVisible);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Find<ComboBox>(dialog, "ProbeUdpMode").SelectedItem = UdpPortMode.Range;
        Assert.True(Find<TextBox>(dialog, "ProbeUdpPort").IsVisible);
        Assert.True(Find<TextBox>(dialog, "ProbeUdpEndPort").IsVisible);
        Assert.True(Find<TextBox>(dialog, "ProbeUdpSize").IsVisible);
        Assert.False(Find<TextBox>(dialog, "ProbeTcpPort").IsVisible);
        Find<TextBox>(dialog, "ProbeUdpPort").Text = "33550";
        protocol.SelectedItem = ProbeProtocol.Tcp;
        Assert.True(Find<TextBox>(dialog, "ProbeTcpPort").IsVisible);
        Assert.False(Find<TextBox>(dialog, "ProbeUdpSize").IsVisible);
        Assert.False(Find<TextBox>(dialog, "ProbeUdpEndPort").IsVisible);
        Assert.Equal(original, target.Session.Options);
        Assert.Equal(SessionState.Running, target.Session.State);
        Click(Find<Button>(dialog, "ProbeCancel"));
        Assert.False(dialog.IsVisible);
        Assert.Equal(original, target.Session.Options);
        var reopened = f.Open();
        Assert.Equal(original.Protocol, Find<ComboBox>(reopened, "ProbeProtocol").SelectedItem);
    }

    [AvaloniaFact]
    public async Task SaveAppliesOnlyCapturedTargetPersistsConfigurationAndUpdatesSummary()
    {
        await using var f = new Fixture();
        var first = await f.Add("127.0.0.1");
        var second = await f.Add("127.0.0.2");
        var other = second.Session!.Options;
        f.Select(first);
        var defaults = f.Configuration.DefaultProbeOptions;
        var dialog = f.Open();
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<ComboBox>(dialog, "ProbeUdpMode").SelectedItem = UdpPortMode.Range;
        Find<TextBox>(dialog, "ProbeUdpPort").Text = "33434";
        Find<TextBox>(dialog, "ProbeUdpEndPort").Text = "33625";
        Find<TextBox>(dialog, "ProbeUdpSize").Text = "96";
        Find<TextBox>(dialog, "ProbeTimeout").Text = "1.5";
        Click(Find<Button>(dialog, "ProbeSave"));
        await Until(() => !dialog.IsVisible);
        Assert.Equal(ProbeProtocol.Udp, first.Session!.Options.Protocol);
        Assert.Equal(UdpPortMode.Range, first.Session.Options.UdpPortMode);
        Assert.Equal(33434, first.Session.Options.DestinationPort);
        Assert.Equal(33625, first.Session.Options.UdpEndPort);
        Assert.Equal(96, first.Session.Options.UdpPacketSize);
        Assert.Equal(TimeSpan.FromSeconds(1.5), first.Session.Options.Timeout);
        Assert.Equal(other, second.Session.Options);
        Assert.Equal(defaults, f.Configuration.DefaultProbeOptions);
        Assert.Contains("33434", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        Assert.Contains("33625", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        var stored = (await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal(first.Session.Options, stored.Targets.Single(t => t.Target == first.Target).Options);
        Assert.Equal(other, stored.Targets.Single(t => t.Target == second.Target).Options);
    }

    [AvaloniaFact]
    public async Task InvalidRangeAndFractionalPortDoNotApplyOtherDraftFields()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        var original = target.Session!.Options;
        var dialog = f.Open();
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<ComboBox>(dialog, "ProbeUdpMode").SelectedItem = UdpPortMode.Range;
        Find<TextBox>(dialog, "ProbeUdpPort").Text = "34000";
        Find<TextBox>(dialog, "ProbeUdpEndPort").Text = "33625";
        Assert.False(await dialog.ApplyAsync());
        Assert.True(dialog.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(dialog, "ProbeStatus").Text));
        Assert.Equal(original, target.Session.Options);
        Find<ComboBox>(dialog, "ProbeUdpMode").SelectedItem = UdpPortMode.Fixed;
        Find<TextBox>(dialog, "ProbeUdpPort").Text = "33434.5";
        Assert.False(await dialog.ApplyAsync());
        Assert.Equal(original, target.Session.Options);
    }

    [AvaloniaFact]
    public async Task ApplyKeepsModalOpenAndCancelOnlyDiscardsSubsequentEdits()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        var dialog = f.Open();
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Tcp;
        Find<TextBox>(dialog, "ProbeTcpPort").Text = "8443";
        Assert.True(await dialog.ApplyAsync());
        Assert.True(dialog.IsVisible);
        Assert.Equal(8443, target.Session!.Options.DestinationPort);
        Find<TextBox>(dialog, "ProbeTcpPort").Text = "443";
        Click(Find<Button>(dialog, "ProbeCancel"));
        Assert.Equal(8443, target.Session.Options.DestinationPort);
        Assert.Equal("TCP · 8443", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
    }

    [AvaloniaFact]
    public async Task ProbeModalHasNoProfilesAndGeneralSettingsUsesSavedConfiguration()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        var dialog = f.Open();
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Control>(), c => c.Name is "ProbeProfiles" or "ProbeSaveProfile" or "ProbeDeleteProfile");
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Tcp;
        Find<TextBox>(dialog, "ProbeTcpPort").Text = "8443";
        Assert.True(await dialog.ApplyAsync(true));
        Assert.Equal(ProbeProtocol.Tcp, target.Session!.Options.Protocol);
        Click(f.Window.FindControl<Button>("SettingsButton")!);
        var settings = Assert.IsType<SettingsWindow>(f.Window.OwnedWindows.Single());
        settings.SelectPage("Monitoring");
        Find<ComboBox>(settings, "ProbeScope").SelectedIndex = 1;
        Assert.Equal(ProbeProtocol.Tcp, Find<ComboBox>(settings, "SettingsProtocol").SelectedItem);
        settings.Close();
    }

    [AvaloniaFact]
    public async Task ModalNeverAppliesDraftToAnotherTargetIfSelectionChanges()
    {
        await using var f = new Fixture();
        var first = await f.Add("127.0.0.1");
        var second = await f.Add("127.0.0.2");
        var untouched = second.Session!.Options;
        f.Select(first);
        var dialog = f.Open();
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Tcp;
        Find<TextBox>(dialog, "ProbeTcpPort").Text = "8443";
        f.Select(second); // The modal blocks pointer interaction; guard programmatic owner changes too.
        await dialog.ApplyAsync();
        Assert.Equal(untouched, second.Session.Options);
        Assert.Equal(SessionState.Running, first.Session!.State);
        Assert.Equal(SessionState.Running, second.Session.State);
    }

    [AvaloniaFact]
    public async Task NewTargetDraftSaveDoesNotChangeDefaultsAndIsUsedWhenTargetStarts()
    {
        await using var f = new Fixture();
        var defaults = f.Configuration.DefaultProbeOptions;
        var dialog = f.Open();
        Find<ComboBox>(dialog, "ProbeProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<TextBox>(dialog, "ProbeUdpPort").Text = "33550";
        Assert.True(await dialog.ApplyAsync(true));
        Assert.Empty(f.Engine.Sessions);
        Assert.Equal(defaults, f.Configuration.DefaultProbeOptions);
        Assert.Equal("UDP · 33550", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        f.Window.FindControl<TextBox>("TargetInput")!.Text = "127.0.0.1";
        typeof(MainWindow).GetMethod("StartTrace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Window, [null, new RoutedEventArgs()]);
        await Until(() => f.Engine.Sessions.Any());
        var session = Assert.Single(f.Engine.Sessions);
        Assert.Equal(ProbeProtocol.Udp, session.Options.Protocol);
        Assert.Equal(33550, session.Options.DestinationPort);
        await f.Engine.StopAsync(session.Id);
        Assert.Equal(SessionState.Stopped, session.State);
    }

    [AvaloniaFact]
    public async Task ProbeModalApplyDoesNotOverwriteSeparatelyChangedProfiles()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1");
        var dialog = f.Open();
        var profiles = f.Configuration.Profiles.Append(new MonitoringProfile("New saved profile", new ProbeOptions())).ToArray();
        await f.InvokeTask("CommitProfileListAsync", (object)profiles);
        Assert.True(await dialog.ApplyAsync());
        Assert.Equal(profiles, f.Configuration.Profiles);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task SavedSessionCannotOpenEditableProbeModal()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        await Until(() => target.Session!.LatestSnapshot is not null);
        await f.Engine.StopAsync(target.Id);
        var stored = (await f.Engine.Storage.LoadSessionsAsync()).Single(s => s.Id == target.Id);
        await f.InvokeTask("CloseTargetAsync", target);
        await f.InvokeTask("LoadHistory", stored);
        Assert.True(Assert.IsType<TargetItem>(f.Window.FindControl<ListBox>("TargetList")!.SelectedItem).IsHistorical);
        Assert.False(f.Window.FindControl<Button>("ConfigureProbeButton")!.IsEnabled);
        typeof(MainWindow).GetMethod("OpenProbeConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Window, [null, new RoutedEventArgs()]);
        Assert.Empty(f.Window.OwnedWindows);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("Probe modal operation timed out."); await Task.Delay(20); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tracescope-probe-modal", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public WorkspaceConfiguration Configuration => (WorkspaceConfiguration)typeof(MainWindow).GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window)!;
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new Transport());
            Window = new MainWindow(["--data-dir", Directory], Engine); Window.Show();
        }
        public ProbeConfigurationWindow Open()
        {
            Click(Window.FindControl<Button>("ConfigureProbeButton")!);
            return Assert.IsType<ProbeConfigurationWindow>(Window.OwnedWindows.Single());
        }
        public void Select(TargetItem item) => Window.FindControl<ListBox>("TargetList")!.SelectedItem = item;
        public Task InvokeTask(string method, params object?[] args) => (Task)typeof(MainWindow)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Window, args)!;
        public async Task<TargetItem> Add(string target)
        {
            var task = (Task)typeof(MainWindow).GetMethod("AddTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Window, [target, new ProbeOptions { MaxHops = 2, Interval = TimeSpan.FromSeconds(2.5), Timeout = TimeSpan.FromSeconds(1), ResolveHostnames = false }, null, false])!;
            await task;
            return Assert.IsType<TargetItem>(Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close(); await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class Transport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 10, ProbeStatus.Success));
    }
}
