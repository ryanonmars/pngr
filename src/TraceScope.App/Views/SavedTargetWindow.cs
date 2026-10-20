using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TraceScope.Core;
using Ui = TraceScope.App.Views.SettingsUi;

namespace TraceScope.App.Views;

public sealed class SavedTargetWindow : Window
{
    public SavedTargetWindow(WorkspaceConfiguration workspace, string group, ProbeOptions options,
        IReadOnlyList<ProbeSourceChoice> sources, Func<WorkspaceTarget, bool, Task> save)
    {
        Title = "Add Target"; Width = 470; Height = 430; MinWidth = 430; MinHeight = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Apply(this);
        var address = new TextBox { Name = "SavedTargetAddress", Watermark = "Hostname or IP address", MaxLength = 253 };
        var name = new TextBox { Name = "SavedTargetName", Watermark = "Optional display name", MaxLength = 80 };
        var groups = new ComboBox { Name = "SavedTargetGroup", ItemsSource = TargetGroupCatalog.List(workspace), SelectedItem = group,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var configuration = new ComboBox { Name = "SavedTargetConfiguration", ItemsSource = new[] { "New target settings" }.Concat(workspace.Profiles.Select(p => p.Name)).ToArray(),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var probe = options;
        var tcpPort = workspace.LastTcpPort; var udpPort = workspace.LastUdpPort; string? profile = null;
        configuration.SelectionChanged += (_, _) =>
        {
            var selected = configuration.SelectedIndex - 1;
            probe = selected < 0 ? options : workspace.Profiles[selected].Options;
            profile = selected < 0 ? null : workspace.Profiles[selected].Name;
        };
        var configure = new Button { Name = "ConfigureSavedProbe", Content = "Configure Probe…" };
        configure.Click += async (_, _) =>
        {
            var editor = new ProbeConfigurationWindow(probe, tcpPort, udpPort, sources, workspace.Profiles, profile, change =>
            { probe = change.Options; tcpPort = change.TcpPort; udpPort = change.UdpPort; profile = change.ProfileName; return Task.CompletedTask; });
            await editor.ShowDialog(this);
        };
        var status = Ui.Note("Save a target without opening a tab or sending probes."); status.Name = "SavedTargetStatus";
        var cancel = new Button { Name = "CancelSavedTarget", Content = "Cancel", IsCancel = true };
        var onlySave = new Button { Name = "SaveTargetOnly", Content = "Save Target", IsDefault = true };
        var monitor = new Button { Name = "SaveAndMonitorTarget", Content = "Save & Monitor" };
        var busy = false;
        cancel.Click += (_, _) => Close(); Closing += (_, e) => e.Cancel = busy;
        async Task Submit(bool start)
        {
            if (busy) return;
            try
            {
                var host = address.Text?.Trim() ?? "";
                if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                    throw new InvalidDataException("Enter a hostname or IP address without a URL or port.");
                var definition = new WorkspaceTarget(host, probe, groups.SelectedItem as string ?? TargetGroupCatalog.Default,
                    TcpPort: probe.Protocol == ProbeProtocol.Tcp ? probe.DestinationPort : tcpPort,
                    UdpPort: probe.Protocol == ProbeProtocol.Udp ? probe.DestinationPort : udpPort,
                    ProfileName: profile, DisplayName: string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim());
                busy = true; ((Control)Content!).IsEnabled = false;
                await save(definition, start); busy = false; Close();
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; ((Control)Content!).IsEnabled = true; }
        }
        onlySave.Click += async (_, _) => await Submit(false); monitor.Click += async (_, _) => await Submit(true);
        var body = Ui.Page(Ui.Heading("Add target"), Ui.Field("Address", address), Ui.Field("Display name", name),
            Ui.Field("Group", groups), Ui.Field("Probe configuration", configuration), configure, status);
        var layout = new Grid { Margin = new Thickness(20,16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        layout.Children.Add(new ScrollViewer { Content = body });
        var actions = Ui.Actions(cancel, onlySave, monitor); Grid.SetRow(actions, 1); layout.Children.Add(actions); Content = layout;
    }
}
