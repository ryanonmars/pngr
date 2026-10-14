using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class SettingsWindowTests
{
    [AvaloniaFact]
    public async Task PlatformSettingsShortcutOpensCentralWindow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma,
            KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control
        };
        fixture.Window.RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.IsType<SettingsWindow>(fixture.Window.OwnedWindows.Single());
    }

    [AvaloniaFact]
    public async Task DesktopPermissionIsRequestedOnlyByExplicitPermissionButton()
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new WorkspaceConfiguration();
        var requests = 0;
        SettingsWindow CreateEditor() => new(configuration, null, null,
            [new ProbeSourceChoice("Automatic", null, null)], fixture.Directory,
            change => { configuration = change.Configuration; return Task.CompletedTask; },
            () => { requests++; return Task.FromResult(new NotificationResult(true, "Permission explicitly requested")); },
            _ => { });
        var canceled = CreateEditor();
        canceled.Show(fixture.Window);
        canceled.SelectPage("Alerts & notifications");
        Assert.False(Find<CheckBox>(canceled, "SettingsNativeNotifications").IsChecked);
        Find<CheckBox>(canceled, "SettingsNativeNotifications").IsChecked = true;
        Click(Find<Button>(canceled, "SettingsCancel"));
        Assert.False(configuration.Actions.NativeNotificationsEnabled);
        Assert.Equal(0, requests);
        var dialog = CreateEditor();
        dialog.Show(fixture.Window);
        dialog.SelectPage("Alerts & notifications");
        Find<CheckBox>(dialog, "SettingsNativeNotifications").IsChecked = true;
        Find<CheckBox>(dialog, "SettingsInAppNotifications").IsChecked = true;
        Find<CheckBox>(dialog, "SettingsIncludeRecovery").IsChecked = false;
        Find<NumericUpDown>(dialog, "SettingsNotificationSpacing").Value = 120;
        await Apply(dialog);
        Assert.True(configuration.Actions.NativeNotificationsEnabled);
        Assert.True(configuration.DesktopNotifications);
        Assert.False(configuration.Actions.IncludeRecovery);
        Assert.Equal(120, configuration.Actions.MinimumNotificationSpacingSeconds);
        Assert.Equal(0, requests);
        Click(Find<Button>(dialog, "SettingsNotificationPermission"));
        await Until(() => requests == 1);
        Assert.Equal("Permission explicitly requested", Find<TextBlock>(dialog, "SettingsStatus").Text);
    }

    [AvaloniaFact]
    public async Task UnrelatedSettingsApplyPreservesTargetOptionsChangedOutsideEditor()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var session = fixture.Engine.Sessions.Single();
        var dialog = fixture.OpenSettings();
        fixture.Window.FindControl<NumericUpDown>("MaxHopsInput")!.Value = 9;
        fixture.Window.FindControl<ComboBox>("ProtocolInput")!.SelectedItem = ProbeProtocol.Tcp;
        fixture.Window.FindControl<NumericUpDown>("PortInput")!.Value = 9443;
        var changed = session.Options;
        Assert.Equal(9, changed.MaxHops);
        Assert.Equal(ProbeProtocol.Tcp, changed.Protocol);
        dialog.SelectPage("History");
        Find<NumericUpDown>(dialog, "SettingsRetention").Value = 55;
        await Apply(dialog);
        Assert.Equal(changed, session.Options);
        Assert.Equal(changed, (await fixture.LoadWorkspace()).Targets.Single().Options);
        Assert.Equal(55, fixture.Configuration.RetentionDays);
    }

    [AvaloniaFact]
    public async Task SettingsButtonOpensOneOwnedWindowAndCancelDoesNotApplyDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        await InvokeTask(fixture.Window, "SaveWorkspaceNowAsync");
        var before = await File.ReadAllTextAsync(fixture.WorkspacePath);
        var options = fixture.Engine.Sessions.Single().Options;
        var dialog = fixture.OpenSettings();
        Click(fixture.Window.FindControl<Button>("SettingsButton")!);
        Assert.Same(dialog, fixture.Window.OwnedWindows.Single());
        dialog.SelectPage("General");
        Find<CheckBox>(dialog, "SettingsRestore").IsChecked = false;
        dialog.SelectPage("Monitoring");
        Find<NumericUpDown>(dialog, "SettingsInterval").Value = 9;
        Find<ComboBox>(dialog, "ProbeScope").SelectedIndex = 1;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 8443;
        dialog.SelectPage("Local web / API");
        Find<CheckBox>(dialog, "SettingsWebEnabled").IsChecked = true;
        Click(Find<Button>(dialog, "SettingsCancel"));
        Assert.False(dialog.IsVisible);
        Assert.True(fixture.Configuration.RestoreOnStartup);
        Assert.False(fixture.Configuration.WebEnabled);
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        Assert.Equal(options, fixture.Engine.Sessions.Single().Options);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.WorkspacePath));
    }

    [AvaloniaFact]
    public async Task ApplyPersistsGraphHistoryAndNewTargetDefaultsWithoutRestartingExistingTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var first = fixture.Engine.Sessions.Single();
        var originalOptions = first.Options;
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("General");
        Find<CheckBox>(dialog, "SettingsRestore").IsChecked = false;
        dialog.SelectPage("Monitoring");
        Find<CheckBox>(dialog, "SettingsUseDefaults").IsChecked = true;
        Find<ComboBox>(dialog, "SettingsProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 33450;
        Find<NumericUpDown>(dialog, "SettingsInterval").Value = 3.7m;
        Find<NumericUpDown>(dialog, "SettingsTimeout").Value = 1.4m;
        Find<NumericUpDown>(dialog, "SettingsMaxHops").Value = 7;
        Find<NumericUpDown>(dialog, "SettingsConcurrency").Value = 3;
        Find<NumericUpDown>(dialog, "SettingsHistoryLimit").Value = 240;
        Find<CheckBox>(dialog, "SettingsResolveHostnames").IsChecked = false;
        dialog.SelectPage("Display");
        Find<NumericUpDown>(dialog, "SettingsWarningLatency").Value = 45;
        Find<NumericUpDown>(dialog, "SettingsCriticalLatency").Value = 90;
        Find<NumericUpDown>(dialog, "SettingsGraphMaximum").Value = 150;
        Find<ComboBox>(dialog, "SettingsTimeRange").SelectedIndex = 4;
        Find<CheckBox>(dialog, "SettingsShowJitter").IsChecked = false;
        Find<CheckBox>(dialog, "SettingsShowPacketLoss").IsChecked = false;
        Find<CheckBox>(dialog, "SettingsShowEvents").IsChecked = false;
        dialog.SelectPage("History");
        Find<NumericUpDown>(dialog, "SettingsRetention").Value = 60;
        Find<NumericUpDown>(dialog, "SettingsAggregation").Value = 15;
        await Apply(dialog);
        var saved = await fixture.LoadWorkspace();
        Assert.False(saved.RestoreOnStartup);
        Assert.Equal(60, saved.RetentionDays);
        Assert.Equal(15, saved.AggregationSeconds);
        Assert.True(saved.UseDefaultProbeOptionsForNewTargets);
        Assert.Equal(ProbeProtocol.Udp, saved.DefaultProbeOptions.Protocol);
        Assert.Equal(33450, saved.DefaultProbeOptions.DestinationPort);
        Assert.Equal(TimeSpan.FromSeconds(3.7), saved.DefaultProbeOptions.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1.4), saved.DefaultProbeOptions.Timeout);
        Assert.Equal(7, saved.DefaultProbeOptions.MaxHops);
        Assert.Equal(3, saved.DefaultProbeOptions.Concurrency);
        Assert.Equal(240, saved.DefaultProbeOptions.HistoryLimit);
        Assert.False(saved.DefaultProbeOptions.ResolveHostnames);
        var timeline = fixture.Window.FindControl<LatencyTimeline>("Timeline")!;
        Assert.Equal(45, timeline.WarningLatencyMs);
        Assert.Equal(90, timeline.CriticalLatencyMs);
        Assert.Equal(150, timeline.FixedMaximumMs);
        Assert.False(timeline.ShowJitter);
        Assert.False(timeline.ShowPacketLoss);
        Assert.False(timeline.ShowEventMarkers);
        Assert.Equal(TimeSpan.FromHours(1), timeline.Horizon);
        Assert.Equal(originalOptions, first.Options);
        Assert.Equal(SessionState.Running, first.State);
        await fixture.AddTarget("127.0.0.2", useDefaults: true);
        Assert.Equal(saved.DefaultProbeOptions, fixture.Engine.Sessions.Single(s => s.Id != first.Id).Options);
        Assert.Equal(first.Id, fixture.Engine.Sessions.Single(s => s.Target == "127.0.0.1").Id);
    }

    [AvaloniaFact]
    public async Task SelectedTargetScopeKeepsCapturedTargetEvenWhenMainSelectionChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var first = fixture.Engine.Sessions.Single();
        var defaults = fixture.Configuration.DefaultProbeOptions;
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Monitoring");
        Find<ComboBox>(dialog, "ProbeScope").SelectedIndex = 1;
        Find<ComboBox>(dialog, "SettingsProtocol").SelectedItem = ProbeProtocol.Tcp;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 8443;
        Find<NumericUpDown>(dialog, "SettingsInterval").Value = 4;
        await fixture.AddTarget("127.0.0.2");
        var second = fixture.Engine.Sessions.Single(s => s.Id != first.Id);
        var secondOptions = second.Options;
        await Apply(dialog);
        Assert.Equal(ProbeProtocol.Tcp, first.Options.Protocol);
        Assert.Equal(8443, first.Options.DestinationPort);
        Assert.Equal(TimeSpan.FromSeconds(4), first.Options.Interval);
        Assert.Equal(secondOptions, second.Options);
        Assert.Equal(defaults, fixture.Configuration.DefaultProbeOptions);
        var saved = await fixture.LoadWorkspace();
        Assert.Equal(first.Options, saved.Targets.Single(t => t.Target == first.Target).Options);
        Assert.Equal(secondOptions, saved.Targets.Single(t => t.Target == second.Target).Options);
    }

    [AvaloniaFact]
    public async Task ProfilesAreStagedUntilApplyAndRoundTripThroughWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var canceled = fixture.OpenSettings();
        canceled.SelectPage("Monitoring");
        Find<TextBox>(canceled, "SettingsProfileName").Text = "Canceled profile";
        Click(Find<Button>(canceled, "SettingsSaveProfile"));
        Click(Find<Button>(canceled, "SettingsCancel"));
        Assert.DoesNotContain(fixture.Configuration.Profiles, p => p.Name == "Canceled profile");
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Monitoring");
        Find<ComboBox>(dialog, "SettingsProtocol").SelectedItem = ProbeProtocol.Tcp;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 9443;
        Find<TextBox>(dialog, "SettingsProfileName").Text = "Custom TCP";
        Click(Find<Button>(dialog, "SettingsSaveProfile"));
        Assert.DoesNotContain(fixture.Configuration.Profiles, p => p.Name == "Custom TCP");
        await Apply(dialog);
        var profile = Assert.Single((await fixture.LoadWorkspace()).Profiles, p => p.Name == "Custom TCP");
        Assert.Equal(ProbeProtocol.Tcp, profile.Options.Protocol);
        Assert.Equal(9443, profile.Options.DestinationPort);
        dialog.Close();
        var reopened = fixture.OpenSettings();
        reopened.SelectPage("Monitoring");
        Find<ComboBox>(reopened, "SettingsProfiles").SelectedItem = "Custom TCP";
        Assert.Equal(9443, Find<NumericUpDown>(reopened, "SettingsDestinationPort").Value);
        Click(Find<Button>(reopened, "SettingsDeleteProfile"));
        await Apply(reopened);
        Assert.DoesNotContain((await fixture.LoadWorkspace()).Profiles, p => p.Name == "Custom TCP");
    }

    [AvaloniaFact]
    public async Task CentralApiSettingsDefaultOffEnableDisableAndStopWithDesktop()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var session = fixture.Engine.Sessions.Single();
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Local web / API");
        var enabled = Find<CheckBox>(dialog, "SettingsWebEnabled");
        Assert.False(enabled.IsChecked);
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        var port = AvailablePort();
        Find<NumericUpDown>(dialog, "SettingsWebPort").Value = port;
        enabled.IsChecked = true;
        await Apply(dialog);
        var server = Field<LocalWebServer>(fixture.Window, "_webServer");
        var url = server.Url!;
        Assert.Equal("127.0.0.1", new Uri(url).Host);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Assert.Contains(session.Id.ToString(), await client.GetStringAsync(url + "/api/targets"));
        Assert.True((await fixture.LoadWorkspace()).WebEnabled);
        enabled.IsChecked = false;
        await Apply(dialog);
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        Assert.False((await fixture.LoadWorkspace()).WebEnabled);
        Assert.Equal(SessionState.Running, session.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api/targets"));
        AssertPortReusable(port);
        enabled.IsChecked = true;
        await Apply(dialog);
        fixture.Window.Close();
        await Until(() => !fixture.Window.IsVisible);
        Assert.False(dialog.IsVisible);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Null(Field<LocalWebServer?>(fixture.Window, "_webServer"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api/targets"));
        AssertPortReusable(port);
    }

    [AvaloniaFact]
    public async Task InvalidGraphThresholdsDoNotApplyAnyOtherDraftChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Configuration;
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("General");
        Find<CheckBox>(dialog, "SettingsRestore").IsChecked = false;
        dialog.SelectPage("Display");
        Find<NumericUpDown>(dialog, "SettingsWarningLatency").Value = 200;
        Find<NumericUpDown>(dialog, "SettingsCriticalLatency").Value = 100;
        await Apply(dialog, expectSuccess: false);
        Assert.Equal(before, fixture.Configuration);
        Assert.False(File.Exists(fixture.WorkspacePath));
        Assert.Contains("not applied", Find<TextBlock>(dialog, "SettingsStatus").Text);
        Assert.True(dialog.IsVisible);
    }

    [AvaloniaFact]
    public async Task OccupiedApiPortKeepsPreviousListenerSettingsAndSavedWorkspace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Local web / API");
        Find<CheckBox>(dialog, "SettingsWebEnabled").IsChecked = true;
        Find<NumericUpDown>(dialog, "SettingsWebPort").Value = AvailablePort();
        await Apply(dialog);
        var previous = Field<LocalWebServer>(fixture.Window, "_webServer");
        var url = previous.Url!;
        var before = await File.ReadAllTextAsync(fixture.WorkspacePath);
        var config = fixture.Configuration;
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            Find<NumericUpDown>(dialog, "SettingsWebPort").Value = ((IPEndPoint)occupied.LocalEndpoint).Port;
            dialog.SelectPage("History");
            Find<NumericUpDown>(dialog, "SettingsRetention").Value = 90;
            await Apply(dialog, expectSuccess: false);
            Assert.Same(previous, Field<LocalWebServer>(fixture.Window, "_webServer"));
            Assert.Equal(config, fixture.Configuration);
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.WorkspacePath));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Assert.Equal("[]", await client.GetStringAsync(url + "/api/targets"));
            Assert.Contains("not applied", Find<TextBlock>(dialog, "SettingsStatus").Text);
        }
        finally { occupied.Stop(); }
    }

    [AvaloniaFact]
    public async Task SettingsProtocolSwitchingPreservesSeparatePortsAndPersistsSelectedMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Monitoring");
        Find<ComboBox>(dialog, "ProbeScope").SelectedIndex = 1;
        var protocol = Find<ComboBox>(dialog, "SettingsProtocol");
        var port = Find<NumericUpDown>(dialog, "SettingsDestinationPort");
        Assert.False(port.IsEnabled);
        protocol.SelectedItem = ProbeProtocol.Udp;
        port.Value = 33625;
        protocol.SelectedItem = ProbeProtocol.Tcp;
        port.Value = 8443;
        protocol.SelectedItem = ProbeProtocol.Icmp;
        Assert.False(port.IsEnabled);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.Equal(33625m, port.Value);
        protocol.SelectedItem = ProbeProtocol.Tcp;
        Assert.Equal(8443m, port.Value);
        await Apply(dialog);
        var selected = Assert.IsType<TargetItem>(fixture.Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        Assert.Equal(33625, selected.UdpPort);
        Assert.Equal(8443, selected.TcpPort);
        Assert.Equal(8443, selected.Session!.Options.DestinationPort);
        var saved = await fixture.LoadWorkspace();
        Assert.Equal(33625, saved.Targets.Single().UdpPort);
        Assert.Equal(8443, saved.Targets.Single().TcpPort);
    }

    [AvaloniaFact]
    public async Task UdpRangeToolbarSwitchesImmediatelyAndRetainsPerTargetConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var first = fixture.Engine.Sessions.Single();
        var protocol = fixture.Window.FindControl<ComboBox>("ProtocolInput")!;
        var mode = fixture.Window.FindControl<ComboBox>("UdpModeInput")!;
        var end = fixture.Window.FindControl<NumericUpDown>("UdpEndPortInput")!;
        var port = fixture.Window.FindControl<NumericUpDown>("PortInput")!;
        Assert.False(mode.IsVisible);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.True(mode.IsVisible);
        Assert.False(end.IsVisible);
        mode.SelectedIndex = 1;
        end.Value = 33625;
        Assert.True(end.IsVisible);
        Assert.Equal(UdpPortMode.Range, first.Options.UdpPortMode);
        Assert.Equal(33625, first.Options.UdpEndPort);
        protocol.SelectedItem = ProbeProtocol.Tcp;
        Assert.False(mode.IsVisible);
        Assert.False(end.IsVisible);
        port.Value = 8443;
        protocol.SelectedItem = ProbeProtocol.Icmp;
        Assert.False(port.IsVisible);
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.True(end.IsVisible);
        Assert.Equal(33434m, port.Value);
        Assert.Equal(33625m, end.Value);
        var firstItem = Assert.IsType<TargetItem>(fixture.Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        await fixture.AddTarget("127.0.0.2");
        Assert.False(mode.IsVisible);
        fixture.Window.FindControl<ListBox>("TargetList")!.SelectedItem = firstItem;
        Assert.Equal(1, mode.SelectedIndex);
        Assert.True(end.IsVisible);
        Assert.False(end.ShowButtonSpinner);
        Assert.InRange(end.Width, 60, 80);
        Assert.False(end.IsEffectivelyVisible);
        Assert.Equal("UDP · 33434–33625", fixture.Window.FindControl<TextBlock>("ProbeSummary")!.Text);

    }

    [AvaloniaFact]
    public async Task UdpAdvancedSettingsProfilesWorkspaceAndRestartPreserveExactConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var session = fixture.Engine.Sessions.Single();
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Monitoring");
        Find<ComboBox>(dialog, "ProbeScope").SelectedIndex = 1;
        Find<ComboBox>(dialog, "SettingsProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 33434;
        Find<ComboBox>(dialog, "SettingsUdpPortMode").SelectedIndex = 1;
        Find<NumericUpDown>(dialog, "SettingsUdpEndPort").Value = 33625;
        Find<NumericUpDown>(dialog, "SettingsUdpPacketSize").Value = 1280;
        Find<CheckBox>(dialog, "SettingsUdpAllowFragmentation").IsChecked = true;
        Find<TextBox>(dialog, "SettingsProfileName").Text = "UDP range 1280";
        Click(Find<Button>(dialog, "SettingsSaveProfile"));
        await Apply(dialog);
        var expected = session.Options;
        Assert.Equal(UdpPortMode.Range, expected.UdpPortMode);
        Assert.Equal(1280, expected.UdpPacketSize);
        Assert.True(expected.UdpAllowFragmentation);
        var workspace = await fixture.LoadWorkspace();
        Assert.Equal(expected, workspace.Targets.Single().Options);
        Assert.Equal(expected, workspace.LastUsedProbeOptions);
        Assert.Equal(expected, workspace.Profiles.Single(p => p.Name == "UDP range 1280").Options);
        dialog.Close();
        var target = Assert.IsType<TargetItem>(fixture.Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        await InvokeTask(fixture.Window, "RestartTargetAsync", target);
        var restarted = fixture.Engine.Sessions.Single();
        Assert.NotEqual(session.Id, restarted.Id);
        Assert.Equal(expected, restarted.Options);
    }

    [AvaloniaFact]
    public async Task NativeUdpAndTcpToolbarHonorSubsecondTimeoutPreset()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var session = fixture.Engine.Sessions.Single();
        var protocol = fixture.Window.FindControl<ComboBox>("ProtocolInput")!;
        var timeout = fixture.Window.FindControl<ComboBox>("TimeoutInput")!;
        foreach (var packetType in new[] { ProbeProtocol.Udp, ProbeProtocol.Tcp })
        {
            protocol.SelectedItem = packetType;
            timeout.SelectedIndex = 1;
            timeout.SelectedIndex = 0;
            Assert.Equal(TimeSpan.FromMilliseconds(500), session.Options.Timeout);
        }
    }

    [AvaloniaFact]
    public async Task InvalidUdpRangeDoesNotApplyDraftOrOverwriteLiveOptions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTarget("127.0.0.1");
        var original = fixture.Engine.Sessions.Single().Options;
        var dialog = fixture.OpenSettings();
        dialog.SelectPage("Monitoring");
        Find<ComboBox>(dialog, "ProbeScope").SelectedIndex = 1;
        Find<ComboBox>(dialog, "SettingsProtocol").SelectedItem = ProbeProtocol.Udp;
        Find<ComboBox>(dialog, "SettingsUdpPortMode").SelectedIndex = 1;
        Find<NumericUpDown>(dialog, "SettingsDestinationPort").Value = 40000;
        Find<NumericUpDown>(dialog, "SettingsUdpEndPort").Value = 33434;
        await Apply(dialog, expectSuccess: false);
        Assert.Equal(original, fixture.Engine.Sessions.Single().Options);
    }

    private static async Task Apply(SettingsWindow dialog, bool expectSuccess = true)
    {
        var status = Find<TextBlock>(dialog, "SettingsStatus");
        status.Text = "";
        var button = Find<Button>(dialog, "SettingsApply");
        Click(button);
        await Until(() => button.IsEnabled && !string.IsNullOrEmpty(status.Text));
        if (expectSuccess) Assert.StartsWith("Settings saved.", status.Text);
        else Assert.StartsWith("Settings were not applied:", status.Text);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task InvokeTask(MainWindow window, string method, params object?[] args)
        => Assert.IsAssignableFrom<Task>(typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args));
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        { if (DateTime.UtcNow > end) throw new TimeoutException("Settings operation did not complete."); await Task.Delay(20); }
    }
    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); return port;
    }
    private static void AssertPortReusable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); } finally { listener.Stop(); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; }
        public string WorkspacePath => Path.Combine(Directory, "workspace.json");
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public WorkspaceConfiguration Configuration => Field<WorkspaceConfiguration>(Window, "_configuration");
        private Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate project test artifacts.");
            Directory = Path.Combine(root.FullName, "artifacts", "tests", "settings", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new ImmediateTransport());
            Window = new MainWindow(["--data-dir", Directory], Engine); Window.Show();
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await InvokeTask(fixture.Window, "InitializeProfessionalOnOpenAsync");
            await Task.Delay(20); return fixture;
        }
        public SettingsWindow OpenSettings()
        {
            Click(Window.FindControl<Button>("SettingsButton")!);
            return Assert.IsType<SettingsWindow>(Window.OwnedWindows.Single());
        }
        public async Task AddTarget(string target, bool useDefaults = false)
        {
            await InvokeTask(Window, "AddTarget", target, useDefaults ? null : new ProbeOptions
            { MaxHops = 2, Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromMilliseconds(100), ResolveHostnames = false }, null, false);
            await Until(() => Engine.Sessions.Any(s => s.Target == target && s.LatestSnapshot is not null));
        }
        public async Task<WorkspaceConfiguration> LoadWorkspace()
            => (await new WorkspaceStore(WorkspacePath).LoadAsync())!;
        public async ValueTask DisposeAsync()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close(); await Until(() => !Window.IsVisible);
            await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class ImmediateTransport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded)
                : new ProbeResult(destination.ToString(), 12, ProbeStatus.Success));
    }
}
