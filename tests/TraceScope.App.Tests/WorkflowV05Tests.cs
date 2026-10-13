using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WorkflowV05Tests
{
    [AvaloniaFact]
    public async Task UnifiedControlTracksSelectedStateAndStoppedPlayPreservesOptions()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var original = f.Selected.Session!;
        var play = f.Find<Button>("StartButton");
        Assert.Equal("Ⅱ", play.Content);
        Click(play);
        Assert.Equal(SessionState.Paused, original.State);
        Assert.Equal("▶", play.Content);
        Click(play);
        Assert.Equal(SessionState.Running, original.State);
        Assert.Equal("Ⅱ", play.Content);
        f.Action("StopTrace");
        await Until(() => original.State == SessionState.Stopped && play.IsEnabled);
        Assert.Equal("▶", play.Content);
        Click(play);
        await Until(() => f.Selected.Session!.Id != original.Id && f.Selected.Session.State == SessionState.Running);
        Assert.Equal(original.Options, f.Selected.Session!.Options);
        Assert.NotEmpty(await f.Engine.Storage.LoadSessionsAsync());
    }

    [AvaloniaFact]
    public async Task RestartPreservesSelectedConfigurationAndLeavesOtherTargetRunning()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var first = f.Selected;
        await f.Add("127.0.0.2", UdpOptions() with { Protocol = ProbeProtocol.Tcp, DestinationPort = 8443 });
        var second = f.Selected.Session!;
        f.Find<ListBox>("TargetList").SelectedItem = first;
        var original = first.Session!;
        f.Action("RestartTrace");
        await Until(() => f.Selected.Session!.Id != original.Id && f.Selected.Session.LatestSnapshot is not null);
        Assert.Equal(SessionState.Stopped, original.State);
        Assert.Equal(original.Options, f.Selected.Session!.Options);
        Assert.Equal(SessionState.Running, second.State);
        Assert.Contains(await f.Engine.Storage.LoadSessionsAsync(), s => s.Id == original.Id && s.SampleCount > 0);
    }

    [AvaloniaFact]
    public async Task ResetKeepsSessionRunningAndClosingItPreservesHistoryAndOtherTargets()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var first = f.Selected;
        await f.Add("127.0.0.2", UdpOptions());
        var second = f.Selected.Session!;
        f.Find<ListBox>("TargetList").SelectedItem = first;
        var session = first.Session!;
        await Until(() => session.LatestSnapshot!.Destination.Sent >= 3);
        var before = (await f.Engine.Storage.LoadSessionsAsync()).Single(s => s.Id == session.Id).SampleCount;
        f.Action("ResetStatistics");
        Assert.Equal(SessionState.Running, session.State);
        await Until(() => session.LatestSnapshot!.Destination.Sent <= 2);
        Assert.True((await f.Engine.Storage.LoadSessionsAsync()).Single(s => s.Id == session.Id).SampleCount >= before);
        f.Action("CloseSelectedTarget");
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 1);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Equal(SessionState.Running, second.State);
        Assert.Contains(await f.Engine.Storage.LoadSessionsAsync(), s => s.Id == session.Id && s.SampleCount >= before);
    }

    [AvaloniaFact]
    public async Task EnterAddsAndClearsValidTargetWithoutEditingSelectedAddress()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var original = f.Selected.Session!;
        var input = f.Find<TextBox>("TargetInput");
        input.Text = "127.0.0.2";
        f.Invoke("TargetKeyDown", input, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 2 && string.IsNullOrEmpty(input.Text));
        Assert.Equal("127.0.0.1", original.Target);
        Assert.Equal(SessionState.Running, original.State);
        var invalid = new string('x', 256);
        input.Text = invalid;
        f.Invoke("TargetKeyDown", input, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => !(bool)typeof(MainWindow).GetField("_addingTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Window)!);
        Assert.Equal(invalid, input.Text);
        Assert.Equal(2, f.Find<ListBox>("TargetList").ItemCount);
    }

    [AvaloniaFact]
    public async Task ProtocolControlsHideIcmpPortAndRememberSeparateTcpAndUdpPortsPerTarget()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var first = f.Selected;
        var protocol = f.Find<ComboBox>("ProtocolInput");
        var port = f.Find<NumericUpDown>("PortInput");
        Assert.True(port.IsVisible);
        Assert.Equal(33434m, port.Value);
        port.Value = 33625;
        protocol.SelectedItem = ProbeProtocol.Tcp;
        port.Value = 8443;
        protocol.SelectedItem = ProbeProtocol.Icmp;
        Assert.False(port.IsVisible && port.IsEnabled);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.True(port.IsVisible);
        Assert.Equal(33625m, port.Value);
        protocol.SelectedItem = ProbeProtocol.Tcp;
        Assert.Equal(8443m, port.Value);
        await f.Add("127.0.0.2", UdpOptions() with { DestinationPort = 40000 });
        Assert.Equal(40000m, port.Value);
        f.Find<ListBox>("TargetList").SelectedItem = first;
        Assert.Equal(ProbeProtocol.Tcp, protocol.SelectedItem);
        Assert.Equal(8443m, port.Value);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.Equal(33625m, port.Value);
        Assert.True(port.Width >= 108);
        f.Window.UpdateLayout();
        var editor = port.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("33625", editor.Text);
        Assert.True(editor.Bounds.Width >= 60, $"Five-digit port editor only has {editor.Bounds.Width} pixels.");
    }

    [AvaloniaFact]
    public async Task LastUsedOptionsPersistAndNewTargetsInheritThemAfterClose()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        f.Find<NumericUpDown>("PortInput").Value = 33625;
        f.Find<ComboBox>("IntervalInput").SelectedIndex = 1;
        f.Find<NumericUpDown>("MaxHopsInput").Value = 12;
        var expected = f.Selected.Session!.Options;
        await Assert.IsAssignableFrom<Task>(f.Invoke("SaveWorkspaceNowAsync"));
        var saved = (await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal(expected, saved.LastUsedProbeOptions);
        Assert.Equal(33625, saved.LastUdpPort);
        f.Action("CloseSelectedTarget");
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 0);
        f.Find<TextBox>("TargetInput").Text = "127.0.0.2";
        Click(f.Find<Button>("StartButton"));
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 1);
        Assert.Equal(expected, f.Selected.Session!.Options);
        await using var reopened = new Fixture();
        await Assert.IsAssignableFrom<Task>(reopened.Invoke("ApplyWorkspaceAsync", saved with { Targets = [] }));
        reopened.Find<TextBox>("TargetInput").Text = "127.0.0.3";
        Click(reopened.Find<Button>("StartButton"));
        await Until(() => reopened.Find<ListBox>("TargetList").ItemCount == 1);
        Assert.Equal(expected, reopened.Selected.Session!.Options);
    }

    [AvaloniaFact]
    public async Task NewTargetMenuUsesLastUsedSettingsWithoutChangingSelectedTarget()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions() with { Protocol = ProbeProtocol.Tcp, DestinationPort = 8443 });
        var first = f.Selected;
        await f.Add("127.0.0.2", UdpOptions() with { DestinationPort = 33625 });
        var lastUsed = f.Selected.Session!.Options;
        f.Find<ListBox>("TargetList").SelectedItem = first;
        f.Action("NewTargetFromOverview");
        Assert.Equal(ProbeProtocol.Udp, f.Find<ComboBox>("ProtocolInput").SelectedItem);
        Assert.Equal(33625m, f.Find<NumericUpDown>("PortInput").Value);
        Assert.Equal(ProbeProtocol.Tcp, first.Session!.Options.Protocol);
        Assert.Equal(8443, first.Session.Options.DestinationPort);
        f.Find<TextBox>("TargetInput").Text = "127.0.0.3";
        Click(f.Find<Button>("StartButton"));
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 3);
        Assert.Equal(lastUsed, f.Selected.Session!.Options);
    }

    [AvaloniaFact]
    public async Task AppliedProfileSurvivesRestartAndIsSavedForNewTargets()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        f.Find<ComboBox>("ProfileInput").SelectedItem = "UDP 33434";
        var original = f.Selected.Session!;
        var options = original.Options;
        Assert.Equal("UDP 33434", f.Selected.ProfileName);
        f.Action("RestartTrace");
        await Until(() => f.Selected.Session!.Id != original.Id);
        Assert.Equal(options, f.Selected.Session!.Options);
        Assert.Equal("UDP 33434", f.Selected.ProfileName);
        await Assert.IsAssignableFrom<Task>(f.Invoke("SaveWorkspaceNowAsync"));
        var saved = (await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal("UDP 33434", saved.LastUsedProfile);
        f.Action("NewTargetFromOverview");
        Assert.Equal("UDP 33434", f.Find<ComboBox>("ProfileInput").SelectedItem);
        f.Find<TextBox>("TargetInput").Text = "127.0.0.2";
        Click(f.Find<Button>("StartButton"));
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 2);
        Assert.Equal("UDP 33434", f.Selected.ProfileName);
        Assert.Equal(options, f.Selected.Session!.Options);
    }

    [AvaloniaFact]
    public async Task ExplicitTargetOptionsSurviveDefaultOptInWhileNewTargetUsesDefaults()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var existing = f.Selected;
        var explicitOptions = existing.Session!.Options;
        var field = typeof(MainWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var configuration = Assert.IsType<WorkspaceConfiguration>(field.GetValue(f.Window));
        var defaults = UdpOptions() with { Protocol = ProbeProtocol.Tcp, DestinationPort = 9443, MaxHops = 8 };
        field.SetValue(f.Window, configuration with { UseDefaultProbeOptionsForNewTargets = true, DefaultProbeOptions = defaults });
        f.Action("NewTargetFromOverview");
        Assert.Equal(explicitOptions, existing.Session.Options);
        f.Find<TextBox>("TargetInput").Text = "127.0.0.2";
        Click(f.Find<Button>("StartButton"));
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 2);
        Assert.Equal(defaults, f.Selected.Session!.Options);
        Assert.Equal(explicitOptions, existing.Session.Options);
        f.Find<ListBox>("TargetList").SelectedItem = existing;
        Assert.Equal(ProbeProtocol.Udp, f.Find<ComboBox>("ProtocolInput").SelectedItem);
        Assert.Equal(33434m, f.Find<NumericUpDown>("PortInput").Value);
    }

    [AvaloniaFact]
    public async Task ExplicitNewTargetProfileOverridesSavedDefaultsWithoutChangingSelected()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1", UdpOptions());
        var existing = f.Selected.Session!;
        var before = existing.Options;
        var field = typeof(MainWindow).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var configuration = Assert.IsType<WorkspaceConfiguration>(field.GetValue(f.Window));
        field.SetValue(f.Window, configuration with { UseDefaultProbeOptionsForNewTargets = true });
        f.Action("NewTargetFromOverview");
        f.Find<ComboBox>("ProfileInput").SelectedItem = "UDP 33434";
        f.Find<TextBox>("TargetInput").Text = "127.0.0.2";
        Click(f.Find<Button>("StartButton"));
        await Until(() => f.Find<ListBox>("TargetList").ItemCount == 2);
        Assert.Equal(ProbeProtocol.Udp, f.Selected.Session!.Options.Protocol);
        Assert.Equal("UDP 33434", f.Selected.ProfileName);
        Assert.Equal(before, existing.Options);
    }

    private static ProbeOptions UdpOptions() => new()
    { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(1), ResolveHostnames = false };
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The monitoring workflow did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public TargetItem Selected => Assert.IsType<TargetItem>(Find<ListBox>("TargetList").SelectedItem);
        public Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            Directory = Path.Combine(root!.FullName, "artifacts", "tests", "workflow-v05", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new ImmediateTransport());
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Show();
        }
        public T Find<T>(string name) where T : Control => Window.FindControl<T>(name)!;
        public object? Invoke(string method, params object?[] args)
            => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public void Action(string method) => Invoke(method, null, new RoutedEventArgs());
        public async Task Add(string target, ProbeOptions options)
        {
            await Assert.IsAssignableFrom<Task>(Invoke("AddTarget", target, options, null, false));
            await Until(() => Selected.Session?.LatestSnapshot is not null);
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class ImmediateTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
