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

    private WorkspaceConfiguration GroupWorkspace() => _configuration with { Targets = CaptureSavedTargets() };

    private async Task CommitGroupsAsync(WorkspaceConfiguration next, string? oldName, string? newName)
    {
        if (_bulkMonitoring || _addingTarget || _openingSavedTargets.Count > 0 || _monitoringOperations.Count > 0)
            throw new InvalidOperationException("Wait for the monitoring action to finish before editing groups.");
        next.Validate();
        _workspaceSaveTimer.Stop();
        // Persist first; a failed write must leave the running sessions and rules unchanged.
        var items = GroupTargets();
        var currentIds = GroupWorkspace().Targets.Select(t => t.Id).ToHashSet();
        if (!currentIds.IsSubsetOf(next.Targets.Select(t => t.Id)))
            throw new InvalidOperationException("The target list changed. Refresh group management and try again.");
        var saved = await _workspaceStore.SaveAsync(next with {
            HistoryLayout = CaptureHistoryLayout(), WindowLayout = CaptureMainWindowLayout() });
        _configuration = saved;
        foreach (var item in items)
        {
            var target = next.Targets.FirstOrDefault(t => t.Id == item.SavedTargetId);
            if (target is null || !_targets.Contains(item)) continue;
            item.Group = target.Group; item.Session!.Group = item.Group; item.Refresh();
            AssignAlerts(item);
        }
        RefreshTargetLibraryUi(oldName, newName);
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
        var selected = sessions.Select(id => _targets.FirstOrDefault(t => t.Id == id)?.SavedTargetId ?? id).ToHashSet();
        if (!selected.IsSubsetOf(workspace.Targets.Select(t => t.Id)))
            throw new InvalidOperationException("A selected target is no longer saved. Refresh group management.");
        return workspace with { Groups = TargetGroupCatalog.List(workspace), Targets = workspace.Targets
            .Select(target => selected.Contains(target.Id) ? target with { Group = group } : target).ToArray() };
    }

    private async Task RemoveGroupTargetsAsync(IReadOnlyList<Guid> sessions)
    {
        if (_bulkMonitoring) throw new InvalidOperationException("Wait for the monitoring action to finish before removing targets.");
        var selected = sessions.Select(id => _targets.FirstOrDefault(t => t.Id == id)?.SavedTargetId ?? id).ToHashSet();
        var workspace = GroupWorkspace();
        if (!selected.IsSubsetOf(workspace.Targets.Select(t => t.Id)))
            throw new InvalidOperationException("A selected target is no longer saved. Refresh group management.");
        _configuration = workspace with { Groups = TargetGroupCatalog.List(workspace) };
        _workspaceSaveTimer.Stop();
        try
        {
            foreach (var id in selected)
            {
                var item = _targets.FirstOrDefault(t => t.Session is not null && t.SavedTargetId == id);
                if (item is not null)
                {
                    await CloseTargetAsync(item);
                    _workspaceSaveTimer.Stop();
                    if (_targets.Contains(item)) throw new InvalidOperationException($"Could not remove {item.Target}. {StatusText.Text}");
                }
                _configuration = _configuration with { Targets = _configuration.Targets.Where(t => t.Id != id).ToArray() };
            }
        }
        finally
        {
            try { await SaveWorkspaceNowAsync(); }
            catch (Exception error) { ScheduleWorkspaceSave(); throw new InvalidOperationException("The workspace could not be saved. Retry Save in Workspaces and Data. " + error.Message, error); }
            _settingsWindow?.RefreshGroups(TargetGroupCatalog.List(GroupWorkspace()), null, null);
            foreach (var summary in OwnedWindows.OfType<SummaryWindow>()) summary.RefreshRows();
            RefreshTargetLibraryUi(); RefreshAllTargets();
        }
    }

    private async void OpenManageGroups(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_groupWindow is { IsVisible: true }) { _groupWindow.Activate(); return; }
        var dialog = new ManageGroupsWindow(GroupWorkspace,
            index => GroupWorkspace().Targets[index].Target,
            CommitGroupsAsync, ArgumentValue("--group-selection") ?? _selected?.Group, group => _configuration.Alerts.Where(r => r.Group == group)
                .Concat(_settingsWindow?.GroupReferences(group) ?? []).DistinctBy(r => r.Id).ToArray(),
            index => GroupWorkspace().Targets[index].Id,
            (sessions, group) => CommitGroupsAsync(MoveGroupSessions(GroupWorkspace(), sessions, group), null, null),
            RemoveGroupTargetsAsync, AddSavedTargetAsync,
            index => SavedTargetState(GroupWorkspace().Targets[index].Id), OpenLibraryTargetAsync, NewTargetOptions,
            SourceInput.ItemsSource!.Cast<SourceChoice>().Select(s => new ProbeSourceChoice(s.Label, s.Interface, s.Source)).ToArray());
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
