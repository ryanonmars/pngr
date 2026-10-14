using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.App.Views;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private Window? _profileWindow;

    internal void OpenLoadProfileMenu() => OpenProfileMenu("load");
    internal void OpenSaveCurrentProfileMenu() => OpenProfileMenu("save");
    internal void OpenManageProfilesMenu() => OpenProfileMenu("manage");

    private async void OpenProfileMenu(string operation)
    {
        if (_closing) return;
        if (_profileWindow is { IsVisible: true }) { _profileWindow.Activate(); return; }
        if (operation == "load" && !_creatingTarget && _selected is { IsHistorical: true })
        { StatusText.Text = "Select an active target or New Target to load a profile."; return; }
        var owner = _selected;
        var creating = _creatingTarget || owner is null;
        var options = CurrentProbeConfiguration();
        var title = operation switch { "load" => "Load Profile", "save" => "Save Current Configuration", _ => "Manage/Delete Profiles" };
        var dialog = ToolWindow(title, 450, 225);
        dialog.MinWidth = 360; dialog.MinHeight = 200; dialog.Background = Brush.Parse("#191A1D");
        var choice = new ComboBox { Name = "ProfileMenuChoice", MinHeight = 32, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Name = "ProfileMenuName", Text = creating ? _configuration.LastUsedProfile ?? "My profile" : owner?.ProfileName ?? "My profile",
            MaxLength = 80, MinHeight = 32, FontSize = 13, Padding = new Thickness(8, 5), VerticalContentAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Name = "ProfileMenuStatus", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#ADB2BC") };
        var action = new Button { Name = "ProfileMenuAction", Content = operation switch { "load" => "Load", "save" => "Save", _ => "Delete" }, MinHeight = 32, MinWidth = 80 };
        var cancel = new Button { Name = "ProfileMenuCancel", Content = operation == "manage" ? "Close" : "Cancel", MinHeight = 32, MinWidth = 80 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, action } };
        void Reload()
        {
            var selected = choice.SelectedItem as string;
            choice.ItemsSource = _configuration.Profiles.Select(p => p.Name).ToArray();
            choice.SelectedItem = _configuration.Profiles.Any(p => p.Name == selected) ? selected : _configuration.Profiles.FirstOrDefault()?.Name;
            action.IsEnabled = operation == "save" || _configuration.Profiles.Count > 0;
        }
        Reload();
        var context = operation == "load" ? (creating ? "Apply to the new-target configuration." : $"Apply to {owner!.Target} on its next round.")
            : operation == "save" ? "Save the current configuration as a probing profile." : "Delete saved profiles without changing active targets.";
        dialog.Content = ToolPanel(new TextBlock { Text = context, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            operation == "save" ? name : choice, status, actions);
        if (dialog.Content is Panel panel) panel.Background = Brush.Parse("#191A1D");
        var busy = false;
        cancel.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) { dialog.Close(); e.Handled = true; } };
        dialog.Closing += (_, e) => { if (busy) e.Cancel = true; };
        action.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; action.IsEnabled = cancel.IsEnabled = false;
            try
            {
                if (operation == "load")
                {
                    var profile = _configuration.Profiles.FirstOrDefault(p => p.Name == choice.SelectedItem as string)
                        ?? throw new InvalidOperationException("Select an available profile.");
                    var tcp = creating ? _configuration.LastTcpPort : owner!.TcpPort;
                    var udp = creating ? _configuration.LastUdpPort : owner!.UdpPort;
                    if (profile.Options.Protocol == ProbeProtocol.Tcp) tcp = profile.Options.DestinationPort;
                    if (profile.Options.Protocol == ProbeProtocol.Udp) udp = profile.Options.DestinationPort;
                    await CommitProbeConfigurationAsync(new ProbeConfigurationChange(profile.Options, tcp, udp, profile.Name, _configuration.Profiles), owner, creating);
                }
                else if (operation == "save")
                {
                    if (!ReferenceEquals(_selected, owner) || (_creatingTarget || owner is null) != creating)
                        throw new InvalidOperationException("The selected target changed. Reopen Save Current Configuration.");
                    var profileName = name.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("Enter a profile name.");
                    await CommitProfileListAsync(_configuration.Profiles.Where(p => p.Name != profileName).Append(new MonitoringProfile(profileName, options)).ToArray());
                }
                else
                {
                    var profileName = choice.SelectedItem as string ?? throw new InvalidOperationException("Select a profile to delete.");
                    await CommitProfileListAsync(_configuration.Profiles.Where(p => p.Name != profileName).ToArray());
                    status.Text = "Profile deleted. Active targets are unchanged.";
                }
                busy = false;
                if (operation == "manage") Reload(); else dialog.Close();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; cancel.IsEnabled = true; Reload(); }
        };
        _profileWindow = dialog;
        try { await dialog.ShowDialog(this); }
        finally { if (ReferenceEquals(_profileWindow, dialog)) _profileWindow = null; }
    }

    private async Task CommitProfileListAsync(IReadOnlyList<MonitoringProfile> profiles)
    {
        var draft = _draftOptions;
        await CommitSettingsAsync(_configuration with { Profiles = profiles });
        // Profile storage alone must not discard an unrelated new-target draft.
        if (_creatingTarget) { _draftOptions = draft; SynchronizeProbeControls(draft); }
    }
}
