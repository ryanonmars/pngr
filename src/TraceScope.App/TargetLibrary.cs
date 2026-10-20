using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ui = TraceScope.App.Views.SettingsUi;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private readonly ObservableCollection<TargetItem> _visibleTargets = [];
    private readonly HashSet<Guid> _openingSavedTargets = [];
    private bool _refreshingLibrary;
    private bool _libraryReady;
    private bool _bulkMonitoring;
    private string? _visibleGroup;
    private Window? _bulkCloseWindow;

    private static WorkspaceTarget[] EnsureSavedIds(IEnumerable<WorkspaceTarget> targets) => targets
        .Select(t => t.Id == Guid.Empty ? t with { Id = Guid.NewGuid() } : t).ToArray();

    private WorkspaceTarget Definition(TargetItem item, bool closed = false) => new(item.Target, item.Session!.Options,
        item.Group, item.Session.State == SessionState.Paused, item.TcpPort, item.UdpPort, item.ProfileName,
        item.SavedTargetId, closed, item.Session.State is SessionState.Stopped or SessionState.Error,
        item.DisplayName, item.Id, item.Session.ResolvedAddress);

    private IReadOnlyList<WorkspaceTarget> CaptureSavedTargets()
    {
        if (_configuration.Targets.Any(t => t.Id == Guid.Empty))
            _configuration = _configuration with { Targets = EnsureSavedIds(_configuration.Targets) };
        var saved = _configuration.Targets.ToList();
        foreach (var item in _targets.Where(t => t.Session is not null))
        {
            var definition = Definition(item);
            var index = saved.FindIndex(t => t.Id == definition.Id);
            if (index < 0) saved.Add(definition); else saved[index] = definition;
        }
        return saved;
    }

    private void RememberSavedTarget(TargetItem item, bool closed)
    {
        if (item.Session is null) return;
        _configuration = _configuration with { Targets = CaptureSavedTargets()
            .Select(t => t.Id == item.SavedTargetId ? Definition(item, closed) : t).ToArray() };
    }

    private void InitializeTargetLibrary()
    {
        _libraryReady = true;
        TargetGroupFilter.SelectionChanged += (_, _) =>
        {
            if (_refreshingLibrary) return;
            _visibleGroup = TargetGroupFilter.SelectedIndex <= 0 ? null : TargetGroupFilter.SelectedItem as string;
            SetAllTargetsVisible(false);
            RefreshTargetLibraryUi();
            if (_visibleTargets.Count == 0) { _selected = null; RefreshSelected(); UpdateButtons(); }
        };
        TargetList.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var scroll = TargetList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null || scroll.Extent.Width <= scroll.Viewport.Width) return;
            var delta = Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y;
            scroll.Offset = scroll.Offset.WithX(Math.Clamp(scroll.Offset.X - delta * 60, 0, scroll.Extent.Width - scroll.Viewport.Width));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        RefreshTargetLibraryUi();
    }

    private void RefreshTargetLibraryUi(string? oldGroup = null, string? newGroup = null)
    {
        if (!_libraryReady || _refreshingLibrary) return;
        _refreshingLibrary = true;
        var overview = _allTargetsVisible;
        if (overview) _clearingOverviewTab = true;
        try
        {
            if (oldGroup is not null && _visibleGroup == oldGroup) _visibleGroup = newGroup;
            var groups = TargetGroupCatalog.List(_configuration with { Targets = CaptureSavedTargets() });
            if (_visibleGroup is not null && !groups.Contains(_visibleGroup)) _visibleGroup = null;
            TargetGroupFilter.ItemsSource = new[] { "All Groups" }.Concat(groups).ToArray();
            TargetGroupFilter.SelectedIndex = _visibleGroup is null ? 0 : Array.IndexOf(groups, _visibleGroup) + 1;
            var selection = TargetList.SelectedItem as TargetItem;
            var visible = _targets.Where(t => _visibleGroup is null || t.Group == _visibleGroup).ToArray();
            foreach (var item in _visibleTargets.Where(t => !visible.Contains(t)).ToArray()) _visibleTargets.Remove(item);
            for (var i = 0; i < visible.Length; i++)
            {
                var index = _visibleTargets.IndexOf(visible[i]);
                if (index < 0) _visibleTargets.Insert(i, visible[i]);
                else if (index != i) _visibleTargets.Move(index, i);
            }
            if (!overview) TargetList.SelectedItem = selection is not null && visible.Contains(selection) ? selection : visible.FirstOrDefault();
            TargetFilterEmpty.IsVisible = visible.Length == 0;
            TargetFilterEmpty.Text = _visibleGroup is null ? "No open targets · use + or Manage Groups" : "No open targets in this group";
            BulkGroupActions.Header = $"Current Group: {_visibleGroup}";
            BulkGroupActions.IsVisible = GroupActionsSeparator.IsVisible = _visibleGroup is not null;
            StartGroupAction.Header = $"Start / Resume {_visibleGroup}";
            PauseGroupAction.Header = $"Pause {_visibleGroup}";
            CloseGroupTabsAction.Header = $"Close All {_visibleGroup} Tabs…";
            foreach (var action in new[] { StartGroupAction, PauseGroupAction, CloseGroupTabsAction }) action.IsVisible = _visibleGroup is not null;
            UpdateBulkMonitoringActions();
        }
        finally { if (overview) _clearingOverviewTab = false; _refreshingLibrary = false; }
    }

    private string SavedTargetState(Guid id) => _targets.FirstOrDefault(t => t.Session is not null && t.SavedTargetId == id)?.StateLabel ?? "Saved";

    private void UpdateBulkMonitoringActions()
    {
        var available = !_closing && !_bulkMonitoring && !_addingTarget && !_restoringWorkspace
            && _monitoringOperations.Count == 0 && _closingTargets.Count == 0 && _openingSavedTargets.Count == 0;
        StartGroupAction.IsEnabled = available && _visibleGroup is not null &&
            (_configuration.Targets.Any(t => t.Group == _visibleGroup) || _targets.Any(t => t.Session is not null && t.Group == _visibleGroup));
        PauseGroupAction.IsEnabled = available && _visibleGroup is not null && _targets.Any(t => t.Group == _visibleGroup && t.Session?.State == SessionState.Running);
        CloseGroupTabsAction.IsEnabled = available && _visibleGroup is not null && _targets.Any(t => t.Group == _visibleGroup);
        StartAllAction.IsEnabled = available && (_configuration.Targets.Count > 0 || _targets.Any(t => t.Session is not null));
        PauseAllAction.IsEnabled = available && _targets.Any(t => t.Session?.State == SessionState.Running);
        CloseAllTabsAction.IsEnabled = available && _targets.Count > 0;
    }

    private void ScrollTargetTabs(object? sender, RoutedEventArgs e)
    {
        var scroll = TargetList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        var direction = (sender as Button)?.Tag as string == "Left" ? -1 : 1;
        scroll.Offset = scroll.Offset.WithX(Math.Clamp(scroll.Offset.X + direction * Math.Max(180, scroll.Viewport.Width * .7),
            0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)));
    }

    private void OpenTargetOverflow(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new MenuFlyout();
        foreach (var target in _targets)
        {
            var item = new MenuItem { Header = $"{target.Label} · {target.Group} · {target.StateLabel} · {target.Id.ToString()[..8]}" };
            item.Click += (_, _) =>
            {
                if (!_targets.Contains(target)) return;
                SelectOpenTarget(target);
            };
            menu.Items.Add(item);
        }
        if (_targets.Count == 0) menu.Items.Add(new MenuItem { Header = "No open targets", IsEnabled = false });
        button.Flyout = menu; menu.ShowAt(button);
    }

    private void SelectOpenTarget(TargetItem target)
    {
        if (_visibleGroup is not null && target.Group != _visibleGroup) _visibleGroup = target.Group;
        RefreshTargetLibraryUi(); TargetList.SelectedItem = target; TargetList.ScrollIntoView(target);
    }

    private async Task<TargetItem?> OpenSavedTargetAsync(WorkspaceTarget target, bool restore = false, bool openOnly = false)
    {
        if (_closing || !_openingSavedTargets.Add(target.Id)) return null;
        try
        {
            var existing = _targets.FirstOrDefault(t => t.Session is not null && t.SavedTargetId == target.Id);
            if (existing is not null) return existing;
            // A history tab may already show this archived GUID; give a new stopped tab its own runtime identity.
            var stoppedTarget = target.SessionId is { } previous && _targets.Any(t => t.Id == previous)
                ? target with { SessionId = null } : target;
            var session = openOnly || restore && target.Stopped
                ? await _engine.OpenStoppedTargetAsync(stoppedTarget, _applicationCancellation.Token)
                : await _engine.StartTargetAsync(target.Target, target.Options, _applicationCancellation.Token);
            if (_closing) { await _engine.StopAsync(session.Id); _engine.ForgetSession(session.Id); return null; }
            var item = new TargetItem(session) { SavedTargetId = target.Id, DisplayName = target.DisplayName,
                Group = target.Group, TcpPort = target.TcpPort, UdpPort = target.UdpPort, ProfileName = target.ProfileName };
            session.Group = target.Group; AssignAlerts(item);
            if (!openOnly && restore && target.Paused && !target.Stopped) _engine.Pause(session.Id);
            _targets.Add(item);
            if (_visibleTargets.Contains(item)) TargetList.SelectedItem = item;
            TargetCount.Text = _targets.Count.ToString();
            RememberSavedTarget(item, false);
            if (session.LatestSnapshot is { } snapshot) ReceiveSnapshot(snapshot);
            ScheduleWorkspaceSave();
            return item;
        }
        finally { _openingSavedTargets.Remove(target.Id); }
    }

    private async Task OpenLibraryTargetAsync(Guid id)
    {
        EnsureMonitoringActionAvailable();
        var target = GroupWorkspace().Targets.FirstOrDefault(t => t.Id == id)
            ?? throw new InvalidOperationException("This target is no longer saved.");
        var item = await OpenSavedTargetAsync(target, openOnly: true);
        if (item is null) return;
        SelectOpenTarget(item);
        await SaveWorkspaceNowAsync();
        StatusText.Text = item.Session!.State == SessionState.Stopped
            ? $"Opened {item.Label} · choose Start to monitor" : $"Selected {item.Label} · {item.StateLabel}";
    }

    private void EnsureMonitoringActionAvailable()
    {
        if (_closing || _bulkMonitoring || _addingTarget || _restoringWorkspace || _monitoringOperations.Count > 0 || _closingTargets.Count > 0 || _openingSavedTargets.Count > 0)
            throw new InvalidOperationException("Wait for the current monitoring action to finish.");
    }

    private async Task AddSavedTargetAsync(WorkspaceTarget target, bool monitor)
    {
        var workspace = GroupWorkspace();
        if (workspace.Targets.Count >= 500) throw new InvalidOperationException("The workspace supports up to 500 saved targets.");
        target = target with { Id = Guid.NewGuid(), Closed = true, Stopped = false, SessionId = null, ResolvedAddress = null };
        await CommitGroupsAsync(workspace with { Targets = workspace.Targets.Append(target).ToArray() }, null, null);
        if (monitor)
        {
            try { await RunTargetActionsAsync([target.Id], "Start"); }
            catch (Exception error) { StatusText.Text = "Target saved, but monitoring could not start. " + error.Message; }
        }
    }

    private async Task StopTargetAsync(TargetItem item)
    {
        if (item.Session is null || !_monitoringOperations.Add(item.Id)) return;
        UpdateButtons();
        try { await _engine.StopAsync(item.Id); item.Refresh(); RememberSavedTarget(item, false); ScheduleWorkspaceSave(); }
        finally { _monitoringOperations.Remove(item.Id); UpdateButtons(); RefreshSelected(); }
    }

    private async Task RunTargetActionsAsync(IReadOnlyList<Guid> ids, string operation)
    {
        EnsureMonitoringActionAvailable();
        _bulkMonitoring = true;
        UpdateButtons(); RefreshTargetLibraryUi();
        var errors = new List<string>();
        try
        {
            foreach (var id in ids.Distinct())
            {
                if (_closing) break;
                var target = GroupWorkspace().Targets.FirstOrDefault(t => t.Id == id);
                if (target is null) { errors.Add("A selected target is no longer saved."); continue; }
                try
                {
                    var item = _targets.FirstOrDefault(t => t.Session is not null && t.SavedTargetId == id);
                    if (item is not null && (_monitoringOperations.Contains(item.Id) || _closingTargets.Contains(item.Id)))
                        throw new InvalidOperationException("Target is busy.");
                    switch (operation)
                    {
                        case "Start":
                            if (item is null) await OpenSavedTargetAsync(target);
                            else if (item.Session!.State == SessionState.Paused) _engine.Resume(item.Id);
                            else if (item.Session.State != SessionState.Running) await RestartTargetCoreAsync(item);
                            break;
                        case "Resume": if (item?.Session?.State == SessionState.Paused) _engine.Resume(item.Id); break;
                        case "Pause": if (item?.Session?.State == SessionState.Running) _engine.Pause(item.Id); break;
                        case "Stop": if (item is not null) await StopTargetAsync(item); break;
                        case "Restart": if (item is not null) await RestartTargetCoreAsync(item); break;
                        case "Close": if (item is not null) await CloseTargetAsync(item); break;
                    }
                    item?.Refresh();
                }
                catch (Exception error) { errors.Add($"{target.DisplayName ?? target.Target}: {error.Message}"); }
                // Initialize one destination at a time, yielding between targets.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            try { await SaveWorkspaceNowAsync(); }
            catch (Exception error)
            {
                ScheduleWorkspaceSave();
                throw new InvalidOperationException("Monitoring changed, but the workspace could not be saved. Retry Save in Workspaces and Data. " + error.Message, error);
            }
        }
        finally { _bulkMonitoring = false; RefreshTargetLibraryUi(); (_groupWindow as Views.ManageGroupsWindow)?.Refresh(); UpdateButtons(); RefreshSelected(); }
        if (errors.Count > 0) throw new InvalidOperationException($"{errors.Count} target action(s) failed. " + string.Join("; ", errors.Take(5)));
    }

    private async void BulkMonitoringAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var parts = tag.Split(':');
        var targets = GroupWorkspace().Targets.Where(t => parts[0] == "All" || t.Group == _visibleGroup).Select(t => t.Id).ToArray();
        try
        {
            if (parts[1] == "Close") { await CloseTargetTabsAsync(parts[0] == "All" ? null : _visibleGroup); return; }
            await RunTargetActionsAsync(targets, parts[1]);
            StatusText.Text = parts[1] == "Start" ? "Start / Resume completed." : "Pause completed.";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async Task CloseTargetTabsAsync(string? group)
    {
        EnsureMonitoringActionAvailable();
        var tabs = _targets.Where(t => group is null || t.Group == group).ToArray();
        if (tabs.Length == 0) { StatusText.Text = "No open tabs to close."; return; }
        _bulkMonitoring = true; UpdateButtons(); RefreshTargetLibraryUi();
        try
        {
            if (tabs.Length > 1)
            {
                var dialog = new Window { Title = "Close target tabs?", Width = 460, Height = 260, MinWidth = 420, MinHeight = 240,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                _bulkCloseWindow = dialog;
                Ui.Apply(dialog);
                var close = new Button { Name = "ConfirmCloseTargetTabs", Content = "Close Tabs" };
                var cancel = new Button { Name = "CancelCloseTargetTabs", Content = "Cancel", IsDefault = true, IsCancel = true };
                close.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
                var active = tabs.Count(t => t.Session?.State == SessionState.Running);
                var message = Ui.Note($"Close {tabs.Length} {(group is null ? "target" : group)} tabs?" +
                    (active > 0 ? $"\n\n{active} target(s) are currently monitoring." : "") +
                    "\n\nMonitoring will stop and the tabs will close. Saved targets, groups, probe settings and historical data will remain.");
                var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14 };
                layout.Children.Add(Ui.Heading("Close target tabs?"));
                var body = new ScrollViewer { Content = message }; Grid.SetRow(body, 1); layout.Children.Add(body);
                var actions = Ui.Actions(cancel, close); actions.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(actions, 2); layout.Children.Add(actions); dialog.Content = layout;
                bool confirmed;
                try { confirmed = await dialog.ShowDialog<bool>(this); }
                finally { if (ReferenceEquals(_bulkCloseWindow, dialog)) _bulkCloseWindow = null; }
                if (!confirmed) { StatusText.Text = "Tab closure cancelled."; return; }
            }
            var errors = new List<string>();
            foreach (var tab in tabs)
            {
                if (!_targets.Contains(tab)) continue;
                await CloseTargetAsync(tab);
                if (_targets.Contains(tab)) errors.Add(tab.Label);
            }
            await SaveWorkspaceNowAsync();
            if (errors.Count > 0) throw new InvalidOperationException("Could not close: " + string.Join(", ", errors));
            StatusText.Text = $"Closed {tabs.Length} tab(s) · saved targets and history are preserved";
        }
        finally { _bulkMonitoring = false; RefreshTargetLibraryUi(); UpdateButtons(); RefreshSelected(); }
    }
}
