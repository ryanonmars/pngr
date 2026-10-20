using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TraceScope.Core;
using Ui = TraceScope.App.Views.SettingsUi;

namespace TraceScope.App.Views;

public sealed class ManageGroupsWindow : Window
{
    private readonly Func<WorkspaceConfiguration> _workspace;
    private readonly Func<int, string> _address;
    private readonly Func<WorkspaceConfiguration, string?, string?, Task> _commit;
    private readonly DataGrid _groups = Table("GroupsTable");
    private readonly DataGrid _members = Table("GroupMembers");
    private readonly TextBlock _heading = Ui.Heading("Targets");
    private readonly TextBlock _status = Ui.Note("Changes save immediately. Default is reserved.");
    private readonly TextBox _name = new() { Name = "GroupName", MaxLength = 80, Watermark = "Group name" };
    private readonly ComboBox _destination = new() { Name = "MoveGroupChoice", MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _rename = Action("RenameGroup", "Rename Group");
    private readonly Button _delete = Action("DeleteGroup", "Delete Group");
    private readonly Button _move = Action("MoveGroupTargets", "Move Selected");
    private readonly Button _remove = Action("RemoveGroupTargets", "Remove Target…");
    private readonly TextBlock _emptyMembers = Ui.Note("No targets in this group.");
    private readonly StackPanel _editor;
    private readonly Func<string, IReadOnlyList<AlertRule>>? _references;
    private readonly Func<int, Guid>? _identity;
    private readonly Func<IReadOnlyList<Guid>, string, Task>? _moveTargets;
    private readonly Func<IReadOnlyList<Guid>, Task>? _removeTargets;
    private readonly Func<WorkspaceTarget, bool, Task>? _addTarget;
    private readonly Func<int, string>? _monitoring;
    private readonly Func<Guid, Task>? _openTarget;
    private readonly Button _open = Action("OpenGroupTarget", "Open Target");
    private readonly Func<ProbeOptions>? _defaultOptions;
    private readonly IReadOnlyList<ProbeSourceChoice> _sources;
    private bool _busy;
    private bool _refreshing;
    private string? _renaming;

    public ManageGroupsWindow(Func<WorkspaceConfiguration> workspace, Func<int, string> address,
        Func<WorkspaceConfiguration, string?, string?, Task> commit, string? initialGroup = null, Func<string, IReadOnlyList<AlertRule>>? references = null,
        Func<int, Guid>? identity = null, Func<IReadOnlyList<Guid>, string, Task>? moveTargets = null, Func<IReadOnlyList<Guid>, Task>? removeTargets = null,
        Func<WorkspaceTarget, bool, Task>? addTarget = null, Func<int, string>? monitoring = null,
        Func<Guid, Task>? openTarget = null, Func<ProbeOptions>? defaultOptions = null,
        IReadOnlyList<ProbeSourceChoice>? sources = null)
    {
        _workspace = workspace; _address = address; _commit = commit; _references = references; _identity = identity; _moveTargets = moveTargets; _removeTargets = removeTargets;
        _addTarget = addTarget; _monitoring = monitoring; _openTarget = openTarget; _defaultOptions = defaultOptions;
        _sources = sources ?? [new("System route", null, null)];
        Title = "Manage Groups"; Width = 620; Height = 590; MinWidth = 520; MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Ui.Apply(this);
        _status.Name = "GroupStatus";
        _heading.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        Column(_groups, "Group", nameof(GroupRow.Name)); Column(_groups, "Targets", nameof(GroupRow.Count), 80);
        Column(_members, "Target", nameof(MemberRow.Target)); Column(_members, "Address", nameof(MemberRow.Address));
        Column(_members, "Monitoring", nameof(MemberRow.Monitoring), 100);
        _groups.SelectionMode = DataGridSelectionMode.Single;
        _groups.MinHeight = 140;
        _members.SelectionMode = DataGridSelectionMode.Extended;
        var create = Action("NewGroup", "New Group");
        var save = Action("SaveGroupName", "Save"); var cancel = Action("CancelGroupName", "Cancel");
        _editor = new StackPanel { IsVisible = false, Spacing = 8, Children = { _name, Ui.Actions(save, cancel) } };
        create.Click += (_, _) => Edit(null);
        _rename.Click += (_, _) => Edit(SelectedGroup);
        cancel.Click += (_, _) => { _editor.IsVisible = false; _groups.MinHeight = 140; _status.Text = "Group edit cancelled."; };
        save.Click += async (_, _) => await Run(async () =>
        {
            var next = _renaming is null ? TargetGroupCatalog.Create(_workspace(), _name.Text)
                : TargetGroupCatalog.Rename(_workspace(), _renaming, _name.Text);
            var name = _name.Text!.Trim();
            await _commit(next, _renaming, name);
            _editor.IsVisible = false; _groups.MinHeight = 140; Refresh(name); _status.Text = "Group saved.";
        });
        _delete.Click += async (_, _) => await Run(DeleteAsync);
        _move.Click += async (_, _) => await Run(async () =>
        {
            var selected = _members.SelectedItems.Cast<MemberRow>().ToArray();
            if (selected.Length == 0 || _destination.SelectedItem is not string destination) return;
            if (_moveTargets is not null) await _moveTargets(selected.Select(m => m.Id).ToArray(), destination);
            else await _commit(TargetGroupCatalog.Move(_workspace(), selected.Select(m => m.Target), destination), null, null);
            Refresh(SelectedGroup); _status.Text = $"Moved {selected.Length} target(s) to {destination}.";
        });
        _remove.Click += async (_, _) => await Run(RemoveTargetsAsync);
        _members.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete && _remove.IsEnabled && !_busy)
            { e.Handled = true; await Run(RemoveTargetsAsync); }
        };
        _groups.SelectionChanged += (_, _) => { if (!_refreshing) { _editor.IsVisible = false; _groups.MinHeight = 140; RefreshMembers(); } };
        _members.SelectionChanged += (_, _) => UpdateActions();
        _destination.SelectionChanged += (_, _) => UpdateActions();
        var close = Action("CloseGroups", "Close"); close.Click += (_, _) => Close();
        var layout = new Grid { Margin = new Thickness(20, 16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 10 };
        Add(layout, Ui.Heading("Groups"), 0); Add(layout, _groups, 1);
        Add(layout, Ui.Actions(create, _rename, _delete), 2); Add(layout, _editor, 3);
        var add = Action("AddGroupTarget", "Add Target…"); add.IsVisible = _addTarget is not null;
        add.Click += async (_, _) => await Run(async () =>
        {
            var dialog = new SavedTargetWindow(_workspace(), SelectedGroup ?? TargetGroupCatalog.Default,
                _defaultOptions?.Invoke() ?? _workspace().DefaultProbeOptions, _sources, _addTarget!);
            await dialog.ShowDialog(this); Refresh(SelectedGroup);
        });
        _open.IsVisible = _openTarget is not null;
        ToolTip.SetTip(_open, "Open or reveal the selected target's tab without starting or resuming monitoring.");
        _open.Click += async (_, _) => await Run(OpenSelectedTargetAsync);
        _members.DoubleTapped += async (_, e) =>
        {
            if (_busy || !_open.IsEnabled || e.Source is not Control source) return;
            if (source is not DataGridRow && !source.GetVisualAncestors().OfType<DataGridRow>().Any()) return;
            e.Handled = true; await Run(OpenSelectedTargetAsync);
        };
        _members.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter || _busy || !_open.IsEnabled) return;
            e.Handled = true; await Run(OpenSelectedTargetAsync);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ToolTip.SetTip(_members, "Saved: configured, but not currently open or monitoring.");
        var memberHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        memberHeading.Children.Add(_heading); Grid.SetColumn(add, 1); memberHeading.Children.Add(add);
        Grid.SetColumn(_open, 2); memberHeading.Children.Add(_open);
        Add(layout, memberHeading, 4);
        _emptyMembers.Name = "GroupMembersEmpty";
        _emptyMembers.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyMembers.VerticalAlignment = VerticalAlignment.Center;
        _emptyMembers.IsHitTestVisible = false;
        var memberList = new Grid { Children = { _members, _emptyMembers } };
        Add(layout, memberList, 5);
        var moveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        moveRow.Children.Add(_destination); Grid.SetColumn(_move, 1); moveRow.Children.Add(_move); Grid.SetColumn(_remove, 2); moveRow.Children.Add(_remove);
        Add(layout, moveRow, 6); Add(layout, _status, 7);
        close.HorizontalAlignment = HorizontalAlignment.Right; Add(layout, close, 8);
        Content = layout;
        Closing += (_, e) => e.Cancel = _busy;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) { if (_editor.IsVisible) { _editor.IsVisible = false; _groups.MinHeight = 140; } else Close(); e.Handled = true; } };
        Refresh(initialGroup);
        Opened += (_, _) => _groups.ScrollIntoView(_groups.SelectedItem, _groups.Columns[0]);
    }

    private string? SelectedGroup => (_groups.SelectedItem as GroupRow)?.Name;
    public void Refresh(string? selection = null)
    {
        _refreshing = true;
        var workspace = _workspace();
        selection ??= SelectedGroup;
        var groups = TargetGroupCatalog.List(workspace);
        _groups.ItemsSource = groups.Select(g => new GroupRow(g, workspace.Targets.Count(t => t.Group == g))).ToArray();
        _groups.SelectedItem = _groups.ItemsSource.Cast<GroupRow>().FirstOrDefault(g => g.Name == selection)
            ?? _groups.ItemsSource.Cast<GroupRow>().First();
        var destination = _destination.SelectedItem as string;
        _destination.ItemsSource = groups;
        _destination.SelectedItem = groups.Contains(destination) ? destination : TargetGroupCatalog.Default;
        _refreshing = false; RefreshMembers();
        if (IsVisible) _groups.ScrollIntoView(_groups.SelectedItem, _groups.Columns[0]);
    }
    private void RefreshMembers()
    {
        var targets = _workspace().Targets.Select((target, index) => (Target: target, Index: index))
            .Where(t => t.Target.Group == SelectedGroup).ToArray();
        ToolTip.SetTip(_heading, SelectedGroup);
        _heading.Text = $"{SelectedGroup} — {targets.Length} target{(targets.Length == 1 ? "" : "s")}";
        var selectedIds = _members.SelectedItems.Cast<MemberRow>().Select(m => m.Id).ToHashSet();
        _members.SelectedItems.Clear();
        _members.SelectedItem = null;
        _emptyMembers.IsVisible = targets.Length == 0;
        _members.ItemsSource = targets.Select(t => new MemberRow(t.Target.DisplayName ?? t.Target.Target, _address(t.Index), _identity?.Invoke(t.Index) ?? t.Target.Id, _monitoring?.Invoke(t.Index) ?? "Saved")).ToArray();
        foreach (var row in _members.ItemsSource.Cast<MemberRow>().Where(m => selectedIds.Contains(m.Id))) _members.SelectedItems.Add(row);
        UpdateActions();
    }
    private void UpdateActions()
    {
        _rename.IsEnabled = _delete.IsEnabled = SelectedGroup is { } g && !TargetGroupCatalog.IsDefault(g);
        _open.IsEnabled = _openTarget is not null && _members.SelectedItems.Count == 1;
        _remove.IsEnabled = _removeTargets is not null && _members.SelectedItems.Count > 0;
        _move.IsEnabled = _members.SelectedItems.Count > 0 && _destination.SelectedItem is string destination && destination != SelectedGroup;
    }
    private async Task OpenSelectedTargetAsync()
    {
        if (_openTarget is null || _members.SelectedItems.Count != 1) return;
        var target = (MemberRow)_members.SelectedItems[0]!;
        await _openTarget(target.Id);
        // Return to the tab immediately; organization remains a modal dialog.
        _busy = false; Close();
    }

    private void Edit(string? group)
    {
        _renaming = group; _name.Text = group ?? ""; _editor.IsVisible = true; _groups.MinHeight = 65;
        _status.Text = group is null ? "New names ignore surrounding spaces and cannot duplicate another name, ignoring case."
            : "Renaming also updates alert rules. Existing legacy names keep their exact spelling.";
        _name.Focus(); _name.SelectAll();
    }
    private async Task DeleteAsync()
    {
        if (SelectedGroup is not { } group) return;
        var rules = (_references?.Invoke(group) ?? _workspace().Alerts.Where(r => r.Group == group).ToArray()).ToArray();
        var confirm = new Window { Title = "Delete Group", Width = 470, Height = 320, MinWidth = 400, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.Apply(confirm);
        var remove = Action("ConfirmDeleteGroup", rules.Length == 0 ? "Delete Group" : "Delete Group & Rules");
        var cancel = Action("CancelDeleteGroup", "Cancel");
        remove.Click += (_, _) => confirm.Close(true); cancel.Click += (_, _) => confirm.Close(false);
        var message = Ui.Note($"Delete “{group}”? Its targets will move to Default." + (rules.Length == 0 ? "" :
            $"\n\n{rules.Length} alert rule(s) reference this group and will be removed, including rules filtered by both target and group. Cancel to keep them.\n\n" + string.Join("\n", rules.Select(r => "• " + r.Name))));
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        body.Children.Add(new ScrollViewer { Content = message });
        Add(body, Ui.Actions(cancel, remove), 1); confirm.Content = body;
        if (!await confirm.ShowDialog<bool>(this)) return;
        await _commit(TargetGroupCatalog.Delete(_workspace(), group, true), group, null);
        Refresh(TargetGroupCatalog.Default); _status.Text = "Group deleted. Targets moved to Default.";
    }
    private async Task RemoveTargetsAsync()
    {
        var selected = _members.SelectedItems.Cast<MemberRow>().ToArray();
        if (selected.Length == 0 || _removeTargets is null) return;
        var group = SelectedGroup;
        var plural = selected.Length != 1;
        var confirm = new Window { Title = plural ? "Remove monitoring targets?" : "Remove monitoring target?",
            Width = 480, Height = 300, MinWidth = 420, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.Apply(confirm);
        var heading = Ui.Heading(plural ? $"Remove {selected.Length} monitoring targets?" : "Remove monitoring target?");
        heading.Foreground = Avalonia.Media.Brush.Parse("#DDB779");
        var names = string.Join("\n", selected.Take(10).Select(item => "• " + item.Target));
        if (selected.Length > 10) names += $"\n…and {selected.Length - 10} more.";
        var content = Ui.Page(heading,
            Ui.Note(plural ? $"Remove these {selected.Length} targets from Pngr?\n\n{names}" : $"Remove “{selected[0].Target}” from Pngr?"),
            Ui.Note("This will stop monitoring and remove the selected targets from your saved targets. Existing historical monitoring data and recorded events will be preserved."),
            Ui.Note("Target-specific alert rules keep their filters. The group will remain available."));
        var remove = Action("ConfirmRemoveGroupTargets", plural ? "Remove Targets" : "Remove Target");
        var cancel = Action("CancelRemoveGroupTargets", "Cancel");
        cancel.IsDefault = true; cancel.IsCancel = true;
        remove.Click += (_, _) => confirm.Close(true); cancel.Click += (_, _) => confirm.Close(false);
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16 };
        body.Children.Add(new ScrollViewer { Content = content });
        var buttons = Ui.Actions(cancel, remove); buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Add(body, buttons, 1); confirm.Content = body;
        if (!await confirm.ShowDialog<bool>(this)) return;
        try
        {
            await _removeTargets(selected.Select(item => item.Id).ToArray());
            _status.Text = plural ? $"Removed {selected.Length} targets. Saved history is preserved." : "Target removed. Saved history is preserved.";
        }
        finally { Refresh(group); }
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { ((Control)Content!).IsEnabled = false; await action(); }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _busy = false; ((Control)Content!).IsEnabled = true; UpdateActions(); }
    }
    private static Button Action(string name, string text) => new() { Name = name, Content = text };
    private static DataGrid Table(string name) => new() { Name = name, IsReadOnly = true, AutoGenerateColumns = false,
        CanUserReorderColumns = false, CanUserResizeColumns = true, CanUserSortColumns = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 27, MinHeight = 65 };
    private static void Column(DataGrid grid, string title, string property, double? width = null)
        => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property),
            Width = width.HasValue ? new DataGridLength(width.Value) : new DataGridLength(1, DataGridLengthUnitType.Star) });
    private static void Add(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    public sealed record GroupRow(string Name, int Count);
    public sealed record MemberRow(string Target, string Address, Guid Id = default, string Monitoring = "Saved");
}
