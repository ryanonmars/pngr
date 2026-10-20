using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using TraceScope.App.Views;
using TraceScope.Core;
using Ui = TraceScope.App.Views.SettingsUi;

namespace TraceScope.App;

public partial class MainWindow
{
    private Window? _groupWindow;
    private TargetItem[] GroupTargets() => _targets.Where(t => t.Session is not null && !t.IsHistorical).ToArray();

    private WorkspaceConfiguration GroupWorkspace() => _configuration with
    {
        Targets = GroupTargets()
            .Select(t => new WorkspaceTarget(t.Target, t.Session!.Options, t.Group,
                t.Session.State == SessionState.Paused, t.TcpPort, t.UdpPort, t.ProfileName)).ToArray()
    };

    private async Task CommitGroupsAsync(WorkspaceConfiguration next, string? oldName, string? newName)
    {
        next.Validate();
        _workspaceSaveTimer.Stop();
        // Persist first; a failed write must leave the running sessions and rules unchanged.
        var items = GroupTargets();
        if (next.Targets.Count != items.Length || next.Targets.Where((target, index) => target.Target != items[index].Target).Any())
            throw new InvalidOperationException("The target list changed. Reopen group management and try again.");
        var saved = await _workspaceStore.SaveAsync(next with { Targets = next.Targets.Where((_, index) => items[index].Session!.State is SessionState.Running or SessionState.Paused).ToArray(),
            HistoryLayout = CaptureHistoryLayout(), WindowLayout = CaptureMainWindowLayout() });
        _configuration = saved;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!_targets.Contains(item)) continue;
            item.Group = next.Targets[index].Group; item.Session!.Group = item.Group; item.Refresh();
            AssignAlerts(item);
        }
        if (_selected is not null)
        {
            _settingOptions = true;
            try { GroupInput.Text = _selected.Group; }
            finally { _settingOptions = false; }
        }
        _settingsWindow?.RefreshGroups(TargetGroupCatalog.List(GroupWorkspace()), oldName, newName);
        foreach (var summary in OwnedWindows.OfType<SummaryWindow>()) summary.RefreshRows();
        RefreshAllTargets();
    }

    private WorkspaceConfiguration MoveGroupSessions(WorkspaceConfiguration workspace, IReadOnlyList<Guid> sessions, string group)
    {
        if (!TargetGroupCatalog.List(workspace).Contains(group, StringComparer.Ordinal))
            throw new InvalidDataException("Select an existing group.");
        var items = GroupTargets();
        if (workspace.Targets.Count != items.Length) throw new InvalidOperationException("The target list changed. Try again.");
        var selected = sessions.ToHashSet();
        if (selected.Any(id => items.All(item => item.Id != id)))
            throw new InvalidOperationException("A selected target is no longer available. Reopen group management.");
        return workspace with { Groups = TargetGroupCatalog.List(workspace), Targets = workspace.Targets
            .Select((target, index) => selected.Contains(items[index].Id) ? target with { Group = group } : target).ToArray() };
    }

    private async Task RemoveGroupTargetsAsync(IReadOnlyList<Guid> sessions)
    {
        var selected = sessions.ToHashSet();
        var items = GroupTargets().Where(item => selected.Contains(item.Id)).ToArray();
        if (items.Length != selected.Count)
            throw new InvalidOperationException("A selected target is no longer available. Refresh the group and try again.");
        if (items.Any(item => _monitoringOperations.Contains(item.Id) || _closingTargets.Contains(item.Id)))
            throw new InvalidOperationException("A selected target is busy. Wait for its current operation and try again.");
        // Retain even the final member's group, and use the tab's exact close pathway.
        _configuration = _configuration with { Groups = TargetGroupCatalog.List(GroupWorkspace()) };
        _workspaceSaveTimer.Stop();
        try
        {
            foreach (var item in items)
            {
                await CloseTargetAsync(item);
                _workspaceSaveTimer.Stop();
                if (_targets.Contains(item))
                    throw new InvalidOperationException($"Could not remove {item.Target}. {StatusText.Text}");
            }
        }
        finally
        {
            // Persist completed removals even if a later target could not close.
            try { await SaveWorkspaceNowAsync(); }
            catch (Exception error)
            {
                ScheduleWorkspaceSave();
                throw new InvalidOperationException("Targets were closed, but the workspace could not be saved. Retry Save in Workspaces and Data. " + error.Message, error);
            }
            _settingsWindow?.RefreshGroups(TargetGroupCatalog.List(GroupWorkspace()), null, null);
            foreach (var summary in OwnedWindows.OfType<SummaryWindow>()) summary.RefreshRows();
            RefreshAllTargets();
        }
    }

    private async void OpenManageGroups(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_groupWindow is { IsVisible: true }) { _groupWindow.Activate(); return; }
        var dialog = new ManageGroupsWindow(GroupWorkspace,
            index => GroupTargets()[index].Session?.ResolvedAddress ?? "—",
            CommitGroupsAsync, ArgumentValue("--group-selection") ?? _selected?.Group, group => _configuration.Alerts.Where(r => r.Group == group)
                .Concat(_settingsWindow?.GroupReferences(group) ?? []).DistinctBy(r => r.Id).ToArray(),
            index => GroupTargets()[index].Id,
            (sessions, group) => CommitGroupsAsync(MoveGroupSessions(GroupWorkspace(), sessions, group), null, null),
            RemoveGroupTargetsAsync);
        _groupWindow = dialog;
        await dialog.ShowDialog(this);
        if (ReferenceEquals(_groupWindow, dialog)) _groupWindow = null;
    }

    private async void OpenTargetGroup(object? sender, RoutedEventArgs e)
    {
        var target = (sender as Control)?.DataContext as TargetItem ?? _selected;
        if (target?.Session is null || target.IsHistorical) return;
        var dialog = ToolWindow("Assign group", 430, 320);
        dialog.MinWidth = 380; dialog.MinHeight = 300; Ui.Apply(dialog);
        var choice = new ComboBox { Name = "TargetGroupChoice", ItemsSource = TargetGroupCatalog.List(GroupWorkspace()),
            SelectedItem = target.Group, HorizontalAlignment = HorizontalAlignment.Stretch };
        var input = new TextBox { Name = "TargetGroupName", MaxLength = 80, Watermark = "New group name (optional)" };
        var status = Ui.Note("Choose a group, or enter a new name. Clear the selection to use Default.");
        status.Name = "TargetGroupStatus";
        var cancel = new Button { Name = "CancelTargetGroup", Content = "Cancel" };
        var save = new Button { Name = "SaveTargetGroup", Content = "Save" };
        var busy = false;
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, args) => args.Cancel = busy;
        save.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                if (!_targets.Contains(target)) { busy = false; dialog.Close(); return; }
                var next = GroupWorkspace();
                var name = choice.SelectedItem as string ?? TargetGroupCatalog.Default;
                if (!string.IsNullOrWhiteSpace(input.Text))
                {
                    name = input.Text.Trim();
                    // Typing an existing spelling selects it; newly created names use catalog validation.
                    var existing = TargetGroupCatalog.List(next).FirstOrDefault(g => g.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) name = existing;
                    else { next = TargetGroupCatalog.Create(next, name); }
                }
                await CommitGroupsAsync(MoveGroupSessions(next, [target.Id], name), null, null);
                busy = false; dialog.Close();
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; save.IsEnabled = cancel.IsEnabled = true; }
        };
        var heading = Ui.Heading($"Group for {target.Target}");
        heading.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(heading, target.Target);
        dialog.Content = new StackPanel { Margin = new Thickness(20, 16), Spacing = 12, Children = {
            heading, choice, Ui.Field("Create a group", input), status,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } } } };
        await dialog.ShowDialog(this);
    }
}
