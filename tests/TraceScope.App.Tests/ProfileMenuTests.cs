using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProfileMenuTests
{
    [AvaloniaFact]
    public async Task LoadFromMenuChangesOnlySelectedTargetAndKeepsDefaultsAndProfiles()
    {
        await using var f = new Fixture();
        var first = await f.Add("127.0.0.1");
        var second = await f.Add("127.0.0.2");
        var other = second.Session!.Options;
        f.Select(first);
        var defaults = f.Configuration.DefaultProbeOptions;
        var profile = f.Configuration.Profiles.First(p => p.Options.Protocol == ProbeProtocol.Tcp);
        var dialog = f.Open("Load Profile…");
        Find<ComboBox>(dialog, "ProfileMenuChoice").SelectedItem = profile.Name;
        Click(Find<Button>(dialog, "ProfileMenuAction"));
        await Until(() => !dialog.IsVisible);
        Assert.Equal(profile.Options, first.Session!.Options);
        Assert.Equal(profile.Name, first.ProfileName);
        Assert.Equal(other, second.Session.Options);
        Assert.Equal(defaults, f.Configuration.DefaultProbeOptions);
        Assert.Contains("TCP", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        Assert.Equal(SessionState.Running, first.Session.State);
    }

    [AvaloniaFact]
    public async Task SaveAndDeleteMenusPreserveTargetOptionsAndPersistExistingProfileFormat()
    {
        await using var f = new Fixture();
        var target = await f.Add("127.0.0.1");
        var original = target.Session!.Options;
        var defaults = f.Configuration.DefaultProbeOptions;
        var save = f.Open("Save Current Configuration…");
        Find<TextBox>(save, "ProfileMenuName").Text = "Saved through menu";
        Click(Find<Button>(save, "ProfileMenuAction"));
        await Until(() => !save.IsVisible);
        Assert.Equal(original, f.Configuration.Profiles.Single(p => p.Name == "Saved through menu").Options);
        var stored = (await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal(original, stored.Profiles.Single(p => p.Name == "Saved through menu").Options);
        var manage = f.Open("Manage/Delete Profiles…");
        Find<ComboBox>(manage, "ProfileMenuChoice").SelectedItem = "Saved through menu";
        Click(Find<Button>(manage, "ProfileMenuAction"));
        await Until(() => !f.Configuration.Profiles.Any(p => p.Name == "Saved through menu"));
        Assert.True(manage.IsVisible);
        Assert.Equal(original, target.Session.Options);
        Assert.Equal(defaults, f.Configuration.DefaultProbeOptions);
        stored = (await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync())!;
        Assert.DoesNotContain(stored.Profiles, p => p.Name == "Saved through menu");
    }

    [AvaloniaFact]
    public async Task CancelAndEmptyNameDoNotSaveProfiles()
    {
        await using var f = new Fixture();
        await f.Add("127.0.0.1");
        var profiles = f.Configuration.Profiles.ToArray();
        var save = f.Open("Save Current Configuration…");
        Find<TextBox>(save, "ProfileMenuName").Text = " ";
        Click(Find<Button>(save, "ProfileMenuAction"));
        await Until(() => Find<TextBlock>(save, "ProfileMenuStatus").Text == "Enter a profile name.");
        Assert.True(save.IsVisible);
        Assert.Equal(profiles, f.Configuration.Profiles);
        Find<TextBox>(save, "ProfileMenuName").Text = "Discard this";
        Click(Find<Button>(save, "ProfileMenuCancel"));
        Assert.Equal(profiles, f.Configuration.Profiles);
    }

    [AvaloniaFact]
    public async Task LoadMenuRejectsChangedOwnerWithoutApplyingToAnotherTarget()
    {
        await using var f = new Fixture();
        var first = await f.Add("127.0.0.1");
        var second = await f.Add("127.0.0.2");
        var original = first.Session!.Options;
        f.Select(first);
        var dialog = f.Open("Load Profile…");
        Find<ComboBox>(dialog, "ProfileMenuChoice").SelectedItem = f.Configuration.Profiles.First(p => p.Options.Protocol == ProbeProtocol.Tcp).Name;
        f.Select(second);
        Click(Find<Button>(dialog, "ProfileMenuAction"));
        await Until(() => !string.IsNullOrEmpty(Find<TextBlock>(dialog, "ProfileMenuStatus").Text));
        Assert.True(dialog.IsVisible);
        Assert.Equal(original, first.Session.Options);
        Assert.Equal(ProbeProtocol.Icmp, second.Session!.Options.Protocol);
    }

    [AvaloniaFact]
    public async Task LoadMenuForNewTargetChangesDraftWithoutStartingMonitoringOrChangingDefaults()
    {
        await using var f = new Fixture();
        var defaults = f.Configuration.DefaultProbeOptions;
        var profile = f.Configuration.Profiles.First(p => p.Options.Protocol == ProbeProtocol.Tcp);
        var dialog = f.Open("Load Profile…");
        Find<ComboBox>(dialog, "ProfileMenuChoice").SelectedItem = profile.Name;
        Click(Find<Button>(dialog, "ProfileMenuAction"));
        await Until(() => !dialog.IsVisible);
        Assert.Empty(f.Engine.Sessions);
        Assert.Equal(defaults, f.Configuration.DefaultProbeOptions);
        Assert.Contains("TCP", f.Window.FindControl<TextBlock>("ProbeSummary")!.Text);
        f.Window.FindControl<TextBox>("TargetInput")!.Text = "127.0.0.1";
        typeof(MainWindow).GetMethod("StartTrace", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Window, [null, new RoutedEventArgs()]);
        await Until(() => f.Engine.Sessions.Any());
        Assert.Equal(profile.Options, Assert.Single(f.Engine.Sessions).Options);
    }

    private static T Find<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tracescope-profile-menu", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public WorkspaceConfiguration Configuration => (WorkspaceConfiguration)typeof(MainWindow).GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window)!;
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new Transport());
            Window = new MainWindow(["--data-dir", Directory], Engine); Window.Show();
        }
        public Window Open(string menu)
        {
            typeof(MainWindow).GetMethod("ExecuteAppMenuCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, [menu]);
            return Assert.Single(Window.OwnedWindows);
        }
        public void Select(TargetItem item) => Window.FindControl<ListBox>("TargetList")!.SelectedItem = item;
        public async Task<TargetItem> Add(string address)
        {
            await (Task)typeof(MainWindow).GetMethod("AddTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Window,
                [address, new ProbeOptions { MaxHops = 2, Interval = TimeSpan.FromSeconds(2.5), ResolveHostnames = false }, null, false])!;
            return Assert.IsType<TargetItem>(Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close();
            Window.Close(); await Until(() => !Window.IsVisible); await Engine.DisposeAsync(); System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class Transport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(ttl == 1 ? new ProbeResult("192.0.2.1", 2, ProbeStatus.TimeExceeded) : new ProbeResult(destination.ToString(), 10, ProbeStatus.Success));
    }
}
