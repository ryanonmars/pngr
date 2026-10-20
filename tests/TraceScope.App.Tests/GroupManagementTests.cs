using Avalonia;
using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class GroupManagementTests
{
    [AvaloniaFact]
    public void DialogCreatesEmptyGroupsRejectsDuplicatesAndCancelsEdits()
    {
        var configuration = new WorkspaceConfiguration();
        var window = Editor(() => configuration, next => configuration = next); window.Show();
        try
        {
            Click(window, "NewGroup"); Get<TextBox>(window, "GroupName").Text = "  Gaming  "; Click(window, "SaveGroupName");
            Assert.Contains("Gaming", configuration.Groups);
            Assert.Equal(0, Rows(window).Single(g => g.Name == "Gaming").Count);
            Click(window, "NewGroup"); Get<TextBox>(window, "GroupName").Text = "gaming"; Click(window, "SaveGroupName");
            Assert.Contains("already exists", Get<TextBlock>(window, "GroupStatus").Text);
            Click(window, "CancelGroupName");
            Assert.Single(configuration.Groups, g => g == "Gaming");
            Click(window, "NewGroup"); Get<TextBox>(window, "GroupName").Text = "Discard"; Click(window, "CancelGroupName");
            Assert.DoesNotContain("Discard", configuration.Groups);
            Select(window, "Default"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("Default", ((ManageGroupsWindow.GroupRow)Get<DataGrid>(window, "GroupsTable").SelectedItem!).Name);
            Assert.False(Get<Button>(window, "RenameGroup").IsEnabled); Assert.False(Get<Button>(window, "DeleteGroup").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DialogMovesMultipleTargetsAndRequiresExplicitRuleDeletion()
    {
        var configuration = new WorkspaceConfiguration { Groups = ["Gaming"], Targets = [new("one", new(), "Gaming"), new("two", new(), "Gaming")],
            Alerts = [new() { Name = "Gaming alert", Group = "Gaming", Target = "one" }, new() { Name = "Global" }] };
        var window = Editor(() => configuration, next => configuration = next); window.Show();
        try
        {
            Select(window, "Gaming");
            var members = Get<DataGrid>(window, "GroupMembers");
            foreach (var row in members.ItemsSource!.Cast<object>()) members.SelectedItems.Add(row);
            Click(window, "MoveGroupTargets");
            Assert.All(configuration.Targets, t => Assert.Equal("Default", t.Group));
            Assert.Equal(0, Rows(window).Single(g => g.Name == "Gaming").Count);
            Click(window, "RenameGroup"); Get<TextBox>(window, "GroupName").Text = "Games"; Click(window, "SaveGroupName");
            Assert.Equal("Games", configuration.Alerts[0].Group); Assert.Equal("one", configuration.Alerts[0].Target);
            Click(window, "DeleteGroup");
            var confirmation = Assert.Single(window.OwnedWindows);
            Assert.Contains("Gaming alert", string.Join(" ", confirmation.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
            Click(confirmation, "CancelDeleteGroup"); await Until(() => Get<Button>(window, "DeleteGroup").IsEffectivelyEnabled);
            Assert.Contains("Games", configuration.Groups); Assert.Equal(2, configuration.Alerts.Count);
            Click(window, "DeleteGroup"); confirmation = Assert.Single(window.OwnedWindows);
            Click(confirmation, "ConfirmDeleteGroup"); await Until(() => !configuration.Groups.Contains("Games"));
            Assert.Single(configuration.Alerts); Assert.Equal("Global", configuration.Alerts[0].Name);
            Assert.All(configuration.Targets, t => Assert.Equal("Default", t.Group));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(620, 590)] [InlineData(520, 510)]
    public void GroupLayoutFitsNormalAndMinimumDimensions(double width, double height)
    {
        var configuration = new WorkspaceConfiguration { Groups = ["Work"] };
        var window = Editor(() => configuration, _ => { }); window.Width = width; window.Height = height; window.Show();
        try
        {
            Click(window, "NewGroup"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            foreach (var name in new[] { "GroupsTable", "GroupName", "GroupMembers", "MoveGroupChoice", "RemoveGroupTargets", "CloseGroups" })
            {
                var control = window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                Assert.True(control.Bounds.Width > 0); Assert.True(control.Bounds.Height > 0);
                var point = control.TranslatePoint(default, (Control)window.Content!);
                Assert.NotNull(point); Assert.True(point!.Value.Y >= 0);
                Assert.True(point.Value.Y + control.Bounds.Height <= ((Control)window.Content!).Bounds.Height + 1);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GroupChangesPreserveRunningSessionObjectsOptionsStateAndStoredHistory()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Add("127.0.0.1"); var second = await fixture.Add("127.0.0.2");
        await Until(() => first.Session!.LatestSnapshot is not null);
        fixture.Engine.Pause(first.Id);
        var session = first.Session; var options = session!.Options;
        var history = await fixture.Engine.Storage.LoadSessionsAsync();
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        var next = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Work"), [first.Target], "Work");
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", next, null, null)!;
        Assert.Same(session, first.Session); Assert.Equal(options, session.Options); Assert.Equal(SessionState.Paused, session.State);
        Assert.Equal("Work", first.Group); Assert.Equal("Work", session.Group); Assert.Equal("Default", second.Group);
        Assert.Equal(history.Select(s => s.Id), (await fixture.Engine.Storage.LoadSessionsAsync()).Select(s => s.Id));
        var loaded = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Contains("Work", loaded.Groups); Assert.Equal("Work", loaded.Targets.Single(t => t.Target == first.Target).Group);
        Assert.True(loaded.Targets.Single(t => t.Target == first.Target).Paused);
    }

    [AvaloniaFact]
    public async Task WorkspaceDialogUsesSharedSettingsAndSavesOnlyDisposableData()
    {
        await using var fixture = new Fixture();
        var config = new WorkspaceConfiguration { RetentionDays = 37, AggregationSeconds = 13, RestoreOnStartup = false };
        typeof(MainWindow).GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, config);
        Invoke(fixture.Window, "ExecuteAppMenuCommand", "Workspace…"); var dialog = Assert.Single(fixture.Window.OwnedWindows);
        try
        {
            Assert.Empty(dialog.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.Empty(dialog.GetVisualDescendants().OfType<SettingsNumberField>());
            foreach (var name in new[] { "WorkspaceSave", "WorkspaceImport", "WorkspaceExport", "WorkspaceCompact", "WorkspaceSettings" }) Assert.NotNull(Get<Button>(dialog, name));
            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; dialog.UpdateLayout();
            Assert.True(Get<Button>(dialog, "WorkspaceClose").Bounds.Height > 0);
            Click(dialog, "WorkspaceSave"); await Until(() => Get<TextBlock>(dialog, "WorkspaceStatus").Text?.StartsWith("Workspace saved") == true);
            var saved = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
            Assert.Equal(37, saved.RetentionDays); Assert.Equal(13, saved.AggregationSeconds); Assert.False(saved.RestoreOnStartup);
            Click(dialog, "WorkspaceCompact"); await Until(() => Get<TextBlock>(dialog, "WorkspaceStatus").Text?.StartsWith("Database compacted:") == true);
            Assert.Empty(await fixture.Engine.Storage.LoadSessionsAsync());
            Click(dialog, "WorkspaceSettings");
            var settings = fixture.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            Assert.Equal(37, Get<SettingsNumberField>(settings, "SettingsRetention").Value);
            Assert.Equal(13, Get<SettingsNumberField>(settings, "SettingsAggregation").Value);
            Get<SettingsNumberField>(settings, "SettingsRetention").Text = "0";
            Click(settings, "SettingsApply");
            Assert.Contains("not applied", Get<TextBlock>(settings, "SettingsStatus").Text);
            Click(settings, "SettingsCancel");
            Assert.Equal(37, ((WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!).RetentionDays);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task AssignmentOffersEmptyGroupsAndCancelDoesNotChangeTheTarget()
    {
        await using var fixture = new Fixture();
        var target = await fixture.Add("127.0.0.1");
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", TargetGroupCatalog.Create(workspace, "Empty"), null, null)!;
        void Open() => Invoke(fixture.Window, "OpenTargetGroup", new MenuItem { DataContext = target }, new RoutedEventArgs());
        Open(); var dialog = Assert.Single(fixture.Window.OwnedWindows);
        Get<ComboBox>(dialog, "TargetGroupChoice").SelectedItem = "Empty"; Click(dialog, "CancelTargetGroup");
        Assert.Equal("Default", target.Group);
        Open(); dialog = Assert.Single(fixture.Window.OwnedWindows);
        Assert.Contains("Empty", Get<ComboBox>(dialog, "TargetGroupChoice").ItemsSource!.Cast<string>());
        Get<ComboBox>(dialog, "TargetGroupChoice").SelectedItem = "Empty"; Click(dialog, "SaveTargetGroup");
        await Until(() => !dialog.IsVisible); Assert.Equal("Empty", target.Group);
        Open(); dialog = Assert.Single(fixture.Window.OwnedWindows);
        Get<ComboBox>(dialog, "TargetGroupChoice").SelectedItem = "Default"; Click(dialog, "SaveTargetGroup");
        await Until(() => !dialog.IsVisible); Assert.Equal("Default", target.Group);
    }

    [AvaloniaFact]
    public void OpenAlertDraftsMigrateExactGroupReferencesWithoutLosingOtherEdits()
    {
        var rule = new AlertRule { Name = "Original", Target = "one", Group = "Work" };
        var view = new AlertSettingsView([rule], ["one"], ["Work"], new Border());
        var window = new Window { Content = view }; window.Show();
        try
        {
            Get<TextBox>(window, "AlertRuleName").Text = "Draft edit";
            Invoke(view, "RefreshGroups", new[] { "Default", "Office" }, "Work", "Office");
            var draft = Assert.Single(view.CaptureRules());
            Assert.Equal("Draft edit", draft.Name); Assert.Equal("one", draft.Target); Assert.Equal("Office", draft.Group);
            Assert.Contains("Office", Get<ComboBox>(window, "AlertGroupChoice").ItemsSource!.Cast<string>());
            Invoke(view, "RefreshGroups", new[] { "Default" }, "Office", null);
            Assert.Empty(view.CaptureRules());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SaveFailureLeavesSessionsAndGroupsUnchanged()
    {
        await using var fixture = new Fixture();
        var target = await fixture.Add("127.0.0.1");
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        var next = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Fail"), [target.Target], "Fail");
        var blocked = Path.Combine(fixture.Directory, "blocked"); System.IO.Directory.CreateDirectory(blocked);
        typeof(MainWindow).GetField("_workspaceStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Window, new WorkspaceStore(blocked));
        await Assert.ThrowsAnyAsync<IOException>(() => (Task)Invoke(fixture.Window, "CommitGroupsAsync", next, null, null)!);
        Assert.Equal("Default", target.Group); Assert.Equal("Default", target.Session!.Group);
        var unchanged = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        Assert.DoesNotContain("Fail", unchanged.Groups);
        typeof(MainWindow).GetField("_workspaceStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Window, new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")));
    }

    [AvaloniaFact]
    public async Task RenameAndDeleteKeepTheTargetAndGroupAlertIntersection()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Add("127.0.0.1"); var second = await fixture.Add("127.0.0.2");
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Work"), [first.Target, second.Target], "Work") with
        {
            Alerts = [new() { Id = "intersection", Target = first.Target, Group = "Work" },
                new() { Id = "global" }, new() { Id = "other-case", Group = "work" }]
        };
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        var rules = (System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<AlertRule>>)typeof(TraceEngine)
            .GetField("_alertRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Engine)!;
        Assert.Equal(new[] { "intersection", "global" }, rules[first.Id].Select(r => r.Id));
        Assert.Equal("global", Assert.Single(rules[second.Id]).Id);
        workspace = TargetGroupCatalog.Rename(workspace, "Work", "Office");
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, "Work", "Office")!;
        Assert.Equal("Office", rules[first.Id].Single(r => r.Id == "intersection").Group);
        Assert.Equal(first.Target, rules[first.Id].Single(r => r.Id == "intersection").Target);
        Assert.Equal("global", Assert.Single(rules[second.Id]).Id);
        workspace = TargetGroupCatalog.Delete(workspace, "Office", true);
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, "Office", null)!;
        Assert.Equal("global", Assert.Single(rules[first.Id]).Id);
        Assert.Equal("global", Assert.Single(rules[second.Id]).Id);
        Assert.Equal("Default", first.Group); Assert.Equal("Default", second.Group);
    }

    [AvaloniaFact]
    public async Task DuplicateAddressesKeepIndependentSessionGroupsAndOnlyActiveTargetsPersist()
    {
        await using var fixture = new Fixture();
        var stopped = await fixture.Add("127.0.0.1");
        await fixture.Engine.StopAsync(stopped.Id);
        var running = await fixture.Add("127.0.0.1");
        Assert.NotEqual(stopped.Id, running.Id);
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = TargetGroupCatalog.Create(TargetGroupCatalog.Create(workspace, "Work"), "Other");
        workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "MoveGroupSessions", workspace, new[] { stopped.Id }, "Work")!;
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        Assert.Equal("Work", stopped.Group); Assert.Equal("Default", running.Group);
        Invoke(fixture.Window, "ExecuteAppMenuCommand", "Manage Groups…");
        var dialog = fixture.Window.OwnedWindows.OfType<ManageGroupsWindow>().Single();
        Select(dialog, "Default");
        var members = Get<DataGrid>(dialog, "GroupMembers");
        var row = Assert.Single(members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>());
        Assert.Equal(running.Id, row.Id); members.SelectedItem = row;
        Get<ComboBox>(dialog, "MoveGroupChoice").SelectedItem = "Other"; Click(dialog, "MoveGroupTargets");
        await Until(() => running.Group == "Other");
        Assert.Equal("Work", stopped.Group); Assert.Equal(SessionState.Stopped, stopped.Session!.State);
        Assert.Equal(SessionState.Running, running.Session!.State);
        var saved = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal("Other", Assert.Single(saved.Targets).Group);
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task RemoveSelectedTargetStopsWorkerPreservesHistoryAndPersistsAcrossRestore(bool paused)
    {
        await using var fixture = new Fixture();
        var other = await fixture.Add("127.0.0.1"); var target = await fixture.Add("127.0.0.2");
        await Until(() => target.Session!.LatestSnapshot is not null);
        if (paused) fixture.Engine.Pause(target.Id);
        var samples = await fixture.Engine.Storage.LoadSamplesAsync(target.Id);
        Assert.NotEmpty(samples);
        var marker = new MonitoringEvent(Guid.NewGuid(), target.Id, DateTimeOffset.UtcNow, "Alert", "Recorded before removal", "retained");
        await fixture.Engine.Storage.AppendEventAsync(marker);
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Work"), [target.Target], "Work") with
        { Alerts = [new() { Id = "retained", Target = target.Target }, new() { Id = "group", Group = "Work" }, new() { Id = "both", Target = target.Target, Group = "Work" }] };
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        var dialog = OpenGroups(fixture);
        Select(dialog, "Work");
        var members = Get<DataGrid>(dialog, "GroupMembers"); members.SelectedItem = Assert.Single(members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>());
        Assert.True(Get<Button>(dialog, "RemoveGroupTargets").IsEnabled);
        Click(dialog, "RemoveGroupTargets"); var confirm = Assert.Single(dialog.OwnedWindows);
        Assert.Contains(target.Target, string.Join(" ", confirm.GetVisualDescendants().OfType<TextBlock>().Select(c => c.Text)));
        Click(confirm, "ConfirmRemoveGroupTargets");
        await Until(() => Get<TextBlock>(dialog, "GroupStatus").Text == "Target removed. Saved history is preserved.");
        Assert.Equal(SessionState.Stopped, target.Session!.State);
        Assert.True(((Task)target.Session.GetType().GetProperty("Worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target.Session)!).IsCompleted);
        Assert.DoesNotContain(fixture.Engine.Sessions, session => session.Id == target.Id);
        var tabs = fixture.Window.FindControl<ListBox>("TargetList")!;
        Assert.Same(other, Assert.Single(tabs.ItemsSource!.Cast<TargetItem>())); Assert.Same(other, tabs.SelectedItem);
        Assert.Equal("1", fixture.Window.FindControl<TextBlock>("TargetCount")!.Text);
        Assert.Equal("Work", ((ManageGroupsWindow.GroupRow)Get<DataGrid>(dialog, "GroupsTable").SelectedItem!).Name);
        Assert.Equal(0, Rows(dialog).Single(row => row.Name == "Work").Count);
        Assert.Empty(members.SelectedItems); Assert.Empty(members.ItemsSource!.Cast<object>());
        Assert.False(Get<Button>(dialog, "RemoveGroupTargets").IsEnabled); Assert.True(Get<TextBlock>(dialog, "GroupMembersEmpty").IsVisible);
        Assert.Equal(samples, (await fixture.Engine.Storage.LoadSamplesAsync(target.Id)).Take(samples.Count));
        Assert.Contains((await fixture.Engine.Storage.LoadSessionsAsync()), session => session.Id == target.Id && session.StoppedAt.HasValue);
        Assert.Contains((await fixture.Engine.Storage.LoadEventsAsync(target.Id)), item => item.Id == marker.Id);
        var saved = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Equal(other.Target, Assert.Single(saved.Targets).Target); Assert.Contains("Work", saved.Groups);
        Assert.Equal(workspace.Alerts, saved.Alerts); Assert.All(saved.Alerts.Where(r => r.Id != "group"), rule => Assert.Equal(target.Target, rule.Target));
        var otherOptions = other.Session!.Options;
        dialog.Close();
        await (Task)Invoke(fixture.Window, "ApplyWorkspaceAsync", saved)!;
        Assert.Equal(other.Target, Assert.Single(fixture.Window.FindControl<ListBox>("TargetList")!.ItemsSource!.Cast<TargetItem>()).Target);
        Assert.Equal(otherOptions, fixture.Engine.Sessions.Single().Options);
        Assert.Contains("Work", ((WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!).Groups);
        Assert.NotEmpty(await fixture.Engine.Storage.LoadSamplesAsync(target.Id));
    }

    [AvaloniaFact]
    public async Task BulkRemovalKeepsGroupAndUnrelatedTargetsAndCancelMakesNoChanges()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Add("127.0.0.1"); var second = await fixture.Add("127.0.0.2"); var other = await fixture.Add("127.0.0.3");
        fixture.Engine.Pause(second.Id);
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Work"), [first.Target, second.Target], "Work");
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        var dialog = OpenGroups(fixture); Select(dialog, "Work");
        var members = Get<DataGrid>(dialog, "GroupMembers");
        foreach (var row in members.ItemsSource!.Cast<object>()) members.SelectedItems.Add(row);
        Click(dialog, "RemoveGroupTargets"); var confirm = Assert.Single(dialog.OwnedWindows);
        Assert.Equal("Remove monitoring targets?", confirm.Title);
        var text = string.Join(" ", confirm.GetVisualDescendants().OfType<TextBlock>().Select(c => c.Text));
        Assert.Contains("2 targets", text); Assert.Contains(first.Target, text); Assert.Contains(second.Target, text);
        Click(confirm, "CancelRemoveGroupTargets"); await Until(() => Get<Button>(dialog, "RemoveGroupTargets").IsEffectivelyEnabled);
        Assert.Equal(3, fixture.Engine.Sessions.Count); Assert.Equal(2, members.SelectedItems.Count);
        Click(dialog, "RemoveGroupTargets"); Click(Assert.Single(dialog.OwnedWindows), "ConfirmRemoveGroupTargets");
        await Until(() => Get<TextBlock>(dialog, "GroupStatus").Text?.StartsWith("Removed 2 targets.") == true);
        Assert.Same(other.Session, Assert.Single(fixture.Engine.Sessions)); Assert.Equal(SessionState.Running, other.Session!.State);
        Assert.Equal(0, Rows(dialog).Single(row => row.Name == "Work").Count);
        Assert.Contains("Work", (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!.Groups);
        Assert.False(Get<Button>(dialog, "RemoveGroupTargets").IsEnabled); dialog.Close();
    }

    [AvaloniaFact]
    public async Task RemovalUsesSessionIdsForSameAddressTabsAndRetainsExactAlertFilters()
    {
        await using var fixture = new Fixture();
        var stopped = await fixture.Add("127.0.0.1"); await fixture.Engine.StopAsync(stopped.Id);
        var running = await fixture.Add("127.0.0.1");
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = workspace with { Alerts = [new() { Target = running.Target, Group = "Default" }] };
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        var dialog = OpenGroups(fixture);
        var members = Get<DataGrid>(dialog, "GroupMembers");
        members.SelectedItem = members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>().Single(row => row.Id == stopped.Id);
        Click(dialog, "RemoveGroupTargets"); Click(Assert.Single(dialog.OwnedWindows), "ConfirmRemoveGroupTargets");
        await Until(() => Get<TextBlock>(dialog, "GroupStatus").Text?.StartsWith("Target removed.") == true);
        Assert.Same(running.Session, Assert.Single(fixture.Engine.Sessions)); Assert.Equal(SessionState.Running, running.Session!.State);
        Assert.Same(running, Assert.Single(fixture.Window.FindControl<ListBox>("TargetList")!.ItemsSource!.Cast<TargetItem>()));
        Assert.Equal(running.Id, Assert.Single(members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>()).Id);
        var rules = (System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<AlertRule>>)typeof(TraceEngine).GetField("_alertRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Engine)!;
        Assert.Equal(workspace.Alerts, rules[running.Id]);
        var saved = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Single(saved.Targets); Assert.Equal(workspace.Alerts, saved.Alerts);
        Assert.Contains((await fixture.Engine.Storage.LoadSessionsAsync()), session => session.Id == stopped.Id);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task RemovingFinalTabLeavesAnEmptyPersistentGroupAndWorkingGroupControls()
    {
        await using var fixture = new Fixture();
        var target = await fixture.Add("127.0.0.1");
        var workspace = (WorkspaceConfiguration)Invoke(fixture.Window, "GroupWorkspace")!;
        workspace = TargetGroupCatalog.Move(TargetGroupCatalog.Create(workspace, "Keep me"), [target.Target], "Keep me");
        await (Task)Invoke(fixture.Window, "CommitGroupsAsync", workspace, null, null)!;
        var dialog = OpenGroups(fixture);
        Assert.False(Get<Button>(dialog, "RemoveGroupTargets").IsEnabled);
        var members = Get<DataGrid>(dialog, "GroupMembers"); members.SelectedItem = Assert.Single(members.ItemsSource!.Cast<object>());
        members.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Delete });
        var confirmation = Assert.Single(dialog.OwnedWindows);
        Assert.True(Get<Button>(confirmation, "CancelRemoveGroupTargets").IsDefault);
        Assert.True(Get<Button>(confirmation, "CancelRemoveGroupTargets").IsCancel);
        Click(confirmation, "ConfirmRemoveGroupTargets");
        await Until(() => Get<TextBlock>(dialog, "GroupStatus").Text?.StartsWith("Target removed.") == true);
        Assert.Empty(fixture.Engine.Sessions);
        var tabs = fixture.Window.FindControl<ListBox>("TargetList")!; Assert.Empty(tabs.ItemsSource!.Cast<object>()); Assert.Null(tabs.SelectedItem);
        Assert.Equal("0", fixture.Window.FindControl<TextBlock>("TargetCount")!.Text);
        var saved = (await new WorkspaceStore(Path.Combine(fixture.Directory, "workspace.json")).LoadAsync())!;
        Assert.Empty(saved.Targets); Assert.Contains("Keep me", saved.Groups);
        Click(dialog, "NewGroup"); Get<TextBox>(dialog, "GroupName").Text = "Created after removal"; Click(dialog, "SaveGroupName");
        await Until(() => Rows(dialog).Any(row => row.Name == "Created after removal"));
        Assert.Contains("Keep me", Rows(dialog).Select(row => row.Name)); dialog.Close();
    }

    private static ManageGroupsWindow OpenGroups(Fixture fixture)
    {
        Invoke(fixture.Window, "ExecuteAppMenuCommand", "Manage Groups…");
        return fixture.Window.OwnedWindows.OfType<ManageGroupsWindow>().Single();
    }

    private static ManageGroupsWindow Editor(Func<WorkspaceConfiguration> read, Action<WorkspaceConfiguration> write)
        => new(read, index => index.ToString(), (next, _, _) => { write(next); return Task.CompletedTask; });
    private static IEnumerable<ManageGroupsWindow.GroupRow> Rows(Window window) => Get<DataGrid>(window, "GroupsTable").ItemsSource!.Cast<ManageGroupsWindow.GroupRow>();
    private static void Select(Window window, string name) => Get<DataGrid>(window, "GroupsTable").SelectedItem = Rows(window).Single(g => g.Name == name);
    private static T Get<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window window, string name) => Get<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pngr-group-ui", Guid.NewGuid().ToString("N"));
        public TraceEngine Engine { get; }
        public MainWindow Window { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Engine = new TraceEngine(Path.Combine(Directory, "history.db"), transport: new Transport());
            Window = new MainWindow(["--data-dir", Directory], Engine); Window.Show();
        }
        public async Task<TargetItem> Add(string target)
        {
            await (Task)Invoke(Window, "AddTarget", target, new ProbeOptions { MaxHops = 2, ResolveHostnames = false }, null, false)!;
            return Assert.IsType<TargetItem>(Window.FindControl<ListBox>("TargetList")!.SelectedItem);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var child in Window.OwnedWindows.ToArray()) child.Close(); Window.Close();
            await Until(() => !Window.IsVisible); await Engine.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class Transport : IProbeTransport
    {
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken token)
            => Task.FromResult(new ProbeResult(destination.ToString(), 2, ProbeStatus.Success));
    }
}
