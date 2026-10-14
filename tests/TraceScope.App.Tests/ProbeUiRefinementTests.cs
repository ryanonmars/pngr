using System.Net;
using System.Reflection;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProbeUiRefinementTests
{
    [AvaloniaTheory]
    [InlineData(980)]
    [InlineData(1520)]
    public async Task CompactProbeToolbarFitsFiveDigitPortsAndIdentifiesTargetInTab(double width)
    {
        await using var f = new Fixture(width);
        var target = await f.Add("127.0.0.1", 96);
        f.Window.FindControl<ComboBox>("UdpModeInput")!.SelectedIndex = 1;
        f.Window.UpdateLayout();
        foreach (var name in new[] { "ProtocolInput", "PortInput", "UdpEndPortInput", "SourceInput", "ProfileInput" })
            Assert.False(f.Window.FindControl<Control>(name)!.IsEffectivelyVisible);
        foreach (var name in new[] { "ConfigureProbeButton", "ProbeSummary", "IntervalInput", "FocusInput" })
        {
            var control = f.Window.FindControl<Control>(name)!;
            Assert.True(control.IsEffectivelyVisible);
            var origin = control.TranslatePoint(default, f.Window)!.Value;
            Assert.True(origin.X + control.Bounds.Width <= f.Window.Bounds.Width + 1, $"{name} overflows at {width} px.");
        }
        Assert.Equal(target.StateLabel, f.Window.FindControl<TextBlock>("TargetTitle")!.Text);
        Assert.False(f.Window.FindControl<TextBlock>("TargetSubtitle")!.IsVisible);
        Assert.Contains(f.Window.FindControl<ListBox>("TargetList")!.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == target.Target);
        Assert.Contains("33434", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        f.Window.FindControl<ComboBox>("ProtocolInput")!.SelectedItem = ProbeProtocol.Icmp;
        Assert.Equal("ICMP", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        f.Window.FindControl<ComboBox>("ProtocolInput")!.SelectedItem = ProbeProtocol.Tcp;
        Assert.StartsWith("TCP ·", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);

    }

    [AvaloniaFact]
    public async Task DirectPortEntryCommitsOnEnterAndRejectsReversedRange()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1", 56);
        f.Window.FindControl<Button>("ConfigureProbeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.IsType<TraceScope.App.Views.ProbeConfigurationWindow>(f.Window.OwnedWindows.Single());
        var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ProbeUdpPort");
        input.Text = "33550";
        Assert.True(await dialog.ApplyAsync());
        Assert.Equal(33550, target.Session!.Options.DestinationPort);
        dialog.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ProbeUdpEndPort").Text = "33625";
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ProbeUdpMode").SelectedItem = UdpPortMode.Range;
        var before = target.Session.Options;
        input.Text = "34000";
        Assert.False(await dialog.ApplyAsync());
        Assert.Equal(before, target.Session.Options);
        Assert.False(string.IsNullOrWhiteSpace(dialog.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "ProbeStatus").Text));
        dialog.Close();

    }

    [AvaloniaFact]
    public async Task PacketSizeEditorCommitsOnlySelectedTargetAndPersistsWithWorkspace()
    {
        await using var f = new Fixture();
        var first = await f.Add("127.0.0.1", 56);
        var second = await f.Add("127.0.0.2", 128);
        f.Window.FindControl<ListBox>("TargetList")!.SelectedItem = first;
        var closed = false;
        var editor = Assert.IsAssignableFrom<Control>(f.Invoke("BuildUdpPacketSizeEditor", (Func<bool>)(() => true), (Action)(() => closed = true)));
        var size = editor.GetLogicalDescendants().OfType<NumericUpDown>().Single();
        var apply = editor.GetLogicalDescendants().OfType<Button>().Single();
        Assert.Equal(56m, size.Value);
        Assert.False(size.ShowButtonSpinner);
        size.Value = 96;
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed);
        Assert.Equal(96, first.Session!.Options.UdpPacketSize);
        Assert.Equal(128, second.Session!.Options.UdpPacketSize);
        var workspace = Assert.IsType<WorkspaceConfiguration>(f.Invoke("CaptureWorkspace"));
        var store = new WorkspaceStore(Path.Combine(f.Directory, "workspace.json"));
        await store.SaveAsync(workspace);
        var restored = (await store.LoadAsync())!;
        Assert.Equal(96, restored.Targets.Single(t => t.Target == first.Target).Options.UdpPacketSize);
        Assert.Equal(96, restored.LastUsedProbeOptions!.UdpPacketSize);
        f.Window.FindControl<ListBox>("TargetList")!.SelectedItem = second;
        var read = Assert.IsType<ProbeOptions>(f.Invoke("ReadOptions"));
        Assert.Equal(128, read.UdpPacketSize);
        Assert.False(Assert.IsType<bool>(f.Invoke("ApplyUdpPacketSize", 55m)));
        Assert.False(Assert.IsType<bool>(f.Invoke("ApplyUdpPacketSize", 56.5m)));
        Assert.Equal(128, second.Session.Options.UdpPacketSize);
    }

    [AvaloniaFact]
    public async Task HalfSecondIntervalIsExposedAndPreservesOtherProbeConfiguration()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1", 96);
        var interval = f.Window.FindControl<ComboBox>("IntervalInput")!;
        Assert.Equal("500 ms", Assert.IsType<ComboBoxItem>(interval.Items[0]).Content);
        interval.SelectedIndex = 0;
        Assert.Equal(TimeSpan.FromMilliseconds(500), target.Session!.Options.Interval);
        Assert.Equal(96, target.Session.Options.UdpPacketSize);
        Assert.Equal(ProbeProtocol.Udp, target.Session.Options.Protocol);
        f.Invoke("SynchronizeProbeControls", target.Session.Options);
        Assert.Equal(0, interval.SelectedIndex);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tracescope-probe-ui", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public Fixture(double width = 1180)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new Transport());
            Window = new MainWindow(["--data-dir", Directory], Engine);
            Window.Width = width;
            Window.Show();
        }
        public object? Invoke(string method, params object?[] arguments) => typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, arguments);
        public async Task<TargetItem> Add(string address, int size)
        {
            await Assert.IsAssignableFrom<Task>(Invoke("AddTarget", address, new ProbeOptions
                { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, UdpPacketSize = size, MaxHops = 2, ResolveHostnames = false }, null, false));
            return Assert.IsType<TargetItem>(Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            for (var i = 0; Window.IsVisible && i < 200; i++) await Task.Delay(10);
            await Engine.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class Transport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(new ProbeResult(destination.ToString(), 10, ProbeStatus.Success));
    }
}
