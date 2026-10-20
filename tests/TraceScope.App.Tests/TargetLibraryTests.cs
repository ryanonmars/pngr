using System.Net;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class TargetLibraryTests
{
    [AvaloniaFact]
    public async Task SaveOnlyDoesNotCreateSessionAndDuplicateAddressesHaveDistinctSavedIds()
    {
        await using var f = new Fixture();
        await f.Save("127.0.0.1", "Work"); await f.Save("127.0.0.1", "Gaming");
        var targets = f.Workspace.Targets;
        Assert.Equal(2, targets.Count); Assert.Equal(2, targets.Select(t => t.Id).Distinct().Count());
        Assert.All(targets, t => Assert.True(t.Closed)); Assert.Empty(f.Engine.Sessions); Assert.Empty(f.Tabs.Items);
        Assert.Equal(0, f.Transport.Calls);
        await f.Actions(targets.Select(t => t.Id), "Start");
        Assert.Equal(2, f.Engine.Sessions.Count); Assert.Equal(2, f.Tabs.Items.Count);
        Assert.Equal(2, f.Tabs.Items.Cast<TargetItem>().Select(t => t.Id).Distinct().Count());
        await f.Actions(targets.Select(t => t.Id), "Start"); Assert.Equal(2, f.Engine.Sessions.Count);
        Assert.All(f.Tabs.Items.Cast<TargetItem>(), t => Assert.Equal(2, t.Session!.Options.MaxHops));
    }

    [AvaloniaFact]
    public async Task GroupFilterOnlyChangesVisibilityAndHiddenTargetsKeepCollecting()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work", true); await f.Save("127.0.0.2", "Gaming", true);
        var original = f.Tabs.Items.Cast<TargetItem>().ToArray();
        f.Filter.SelectedItem = "Work";
        Assert.Same(original[0], Assert.Single(f.Tabs.Items.Cast<TargetItem>()));
        await Until(() => original.All(t => t.Snapshot is not null));
        var round = original[1].Snapshot!.Round;
        await Until(() => original[1].Snapshot!.Round > round);
        Assert.All(original, t => Assert.Equal(SessionState.Running, t.Session!.State));
        f.Filter.SelectedItem = "Empty"; Assert.Empty(f.Tabs.Items); Assert.Null(f.Tabs.SelectedItem);
        Assert.True(f.Window.FindControl<TextBlock>("TargetFilterEmpty")!.IsVisible);
        Assert.False(f.Window.FindControl<Grid>("PrimaryGraphFrame")!.IsVisible);
        f.Filter.SelectedItem = "Gaming"; Assert.Same(original[1], f.Tabs.SelectedItem);
        f.Filter.SelectedIndex = 0; Assert.Equal(original, f.Tabs.Items.Cast<TargetItem>());
        Assert.Equal(original.Select(t => t.Id), f.Engine.Sessions.Select(s => s.Id));
    }

    [AvaloniaFact]
    public async Task BulkActionsPreservePausedStoppedClosedAndHiddenScopes()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work"); await f.Save("127.0.0.2", "Gaming");
        var ids = f.Workspace.Targets.Select(t => t.Id).ToArray();
        await f.Actions(ids, "Resume"); Assert.Empty(f.Engine.Sessions);
        await f.Actions(ids, "Start"); var items = f.Tabs.Items.Cast<TargetItem>().ToArray();
        f.Filter.SelectedItem = "Work";
        await f.Actions(ids, "Pause"); Assert.All(items, t => Assert.Equal(SessionState.Paused, t.Session!.State));
        await f.Actions([ids[0]], "Resume"); Assert.Equal(SessionState.Running, items[0].Session!.State); Assert.Equal(SessionState.Paused, items[1].Session!.State);
        await f.Actions(ids, "Stop"); Assert.All(items, t => Assert.Equal(SessionState.Stopped, t.Session!.State));
        Assert.Single(f.Tabs.Items); Assert.Equal("Work", f.Filter.SelectedItem);
        await f.Actions(ids, "Resume"); Assert.All(items, t => Assert.Equal(SessionState.Stopped, t.Session!.State));
        await f.Actions(ids, "Start"); Assert.All(f.Engine.Sessions, t => Assert.Equal(SessionState.Running, t.State));
        Assert.Equal(2, f.Engine.Sessions.Count); Assert.DoesNotContain(items[0].Id, f.Engine.Sessions.Select(t => t.Id));
        await f.Actions(ids, "Close"); Assert.Empty(f.Engine.Sessions); Assert.Empty(f.Tabs.Items);
        Assert.Equal(2, f.Workspace.Targets.Count); Assert.All(f.Workspace.Targets, t => Assert.True(t.Closed));
        Assert.Contains("Work", f.Workspace.Groups);
        await f.Actions(ids, "Resume"); Assert.Empty(f.Engine.Sessions);
        await f.Actions(ids, "Start"); Assert.Equal(2, f.Engine.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task ClosedAndStoppedTargetsReloadWithoutStartingAndRetainHistoryAndIds()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work", true); await f.Save("127.0.0.2", "Gaming", true);
        var ids = f.Workspace.Targets.Select(t => t.Id).ToArray();
        await Until(() => f.Engine.Sessions.All(s => s.LatestSnapshot is not null));
        await f.Actions([ids[0]], "Close"); await f.Actions([ids[1]], "Stop");
        var workspace = f.Workspace; var stopped = workspace.Targets[1];
        var archived = await f.Engine.Storage.LoadSessionsAsync();
        var previousCalls = f.Transport.Calls;
        await f.Apply(workspace);
        Assert.Equal(previousCalls, f.Transport.Calls);
        var restored = Assert.Single(f.Tabs.Items.Cast<TargetItem>());
        Assert.Equal(stopped.Id, restored.SavedTargetId); Assert.Equal(stopped.SessionId, restored.Id);
        Assert.Equal(SessionState.Stopped, restored.Session!.State);
        Assert.Equal(ids, f.Workspace.Targets.Select(t => t.Id));
        Assert.Equal(archived.Select(t => t.Id), (await f.Engine.Storage.LoadSessionsAsync()).Select(t => t.Id));
        var loaded = await new WorkspaceStore(Path.Combine(f.Directory, "workspace.json")).LoadAsync();
        Assert.Equal(ids, loaded!.Targets.Select(t => t.Id)); Assert.True(loaded.Targets[0].Closed);
    }

    [AvaloniaFact]
    public async Task RemovingSavedTargetDoesNotRemoveSameAddressSiblingHistoryOrAlertFilters()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work", true); await f.Save("127.0.0.1", "Work", true);
        var targets = f.Workspace.Targets.ToArray();
        await Until(() => f.Engine.Sessions.All(s => s.LatestSnapshot is not null));
        var current = f.Workspace with { Alerts = [new() { Name = "Exact", Target = "127.0.0.1", Group = "Work" }] };
        await f.Commit(current);
        await (Task)Invoke(f.Window, "RemoveGroupTargetsAsync", new Guid[] { targets[0].Id })!;
        Assert.Equal(targets[1].Id, Assert.Single(f.Workspace.Targets).Id);
        Assert.Equal(2, (await f.Engine.Storage.LoadSessionsAsync()).Count);
        Assert.Single(f.Engine.Sessions); Assert.Equal("127.0.0.1", Assert.Single(f.Workspace.Alerts).Target);
        Assert.Equal("Work", f.Workspace.Alerts[0].Group);
        await (Task)Invoke(f.Window, "RemoveGroupTargetsAsync", new Guid[] { targets[1].Id })!;
        Assert.Contains("Work", f.Workspace.Groups); Assert.Empty(f.Workspace.Targets);
    }

    [AvaloniaFact]
    public async Task GroupRenameMoveAndDeletionPreserveFilterAndSavedMembership()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work", true); await f.Save("127.0.0.2", "Work");
        f.Filter.SelectedItem = "Work";
        await f.Commit(TargetGroupCatalog.Rename(f.Workspace, "Work", "Office"), "Work", "Office");
        Assert.Equal("Office", f.Filter.SelectedItem); Assert.Single(f.Tabs.Items);
        await f.Save("127.0.0.3", "Gaming"); Assert.Equal("Office", f.Filter.SelectedItem);
        await f.Commit(TargetGroupCatalog.Delete(f.Workspace, "Office"), "Office", null);
        Assert.Equal(0, f.Filter.SelectedIndex); Assert.Equal("Default", f.Workspace.Targets[0].Group);
    }

    [AvaloniaFact]
    public async Task PartialStartupFailureKeepsDefinitionsAndStartsOtherTargets()
    {
        await using var f = new Fixture(); await f.Save("127.0.0.1", "Work"); await f.Save("127.0.0.2", "Work");
        var broken = f.Workspace.Targets[0] with { Target = "not a valid host" };
        await f.Commit(f.Workspace with { Targets = [broken, f.Workspace.Targets[1]] });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Actions(f.Workspace.Targets.Select(t => t.Id), "Start"));
        Assert.Contains("failed", error.Message); Assert.Equal(2, f.Workspace.Targets.Count); Assert.Single(f.Engine.Sessions);
        Assert.True(f.Workspace.Targets[0].Closed); Assert.False(f.Workspace.Targets[1].Closed);
    }

    [AvaloniaTheory]
    [InlineData(1200,800)] [InlineData(980,640)]
    public async Task DozensOfTabsScrollWithoutCoveringControlsAndFilterKeepsContextMenus(double width, double height)
    {
        await using var f = new Fixture(); f.Window.Width=width; f.Window.Height=height;
        var definitions = Enumerable.Range(1, 35).Select(i => new WorkspaceTarget("127.0.0.1", new() { MaxHops=1 },
            i%2==0 ? "Work" : "Gaming", Id:Guid.NewGuid(), Stopped:true, DisplayName:$"Target {i:00}")).ToArray();
        await f.Apply(f.Workspace with { Targets = definitions });
        Dispatcher.UIThread.RunJobs(); f.Window.UpdateLayout();
        var scroll = f.Tabs.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
        var left = f.Window.FindControl<Button>("ScrollTargetsLeft")!;
        var right = f.Window.FindControl<Button>("ScrollTargetsRight")!;
        Assert.True(f.Tabs.Bounds.Right <= left.Bounds.Left); Assert.True(left.Bounds.Right <= right.Bounds.Left);
        right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(scroll.Offset.X>0);
        var rightOffset = scroll.Offset.X;
        left.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(scroll.Offset.X < rightOffset);
        scroll.Offset = scroll.Offset.WithX(0);
        var pointer=f.Tabs.TranslatePoint(new Point(40,10),f.Window)!.Value;
        f.Window.MouseWheel(pointer,new Vector(0,-1)); Assert.True(scroll.Offset.X>0);
        var wheelOffset=scroll.Offset.X;
        f.Window.MouseWheel(pointer,new Vector(-1,0)); Assert.True(scroll.Offset.X>wheelOffset);
        Assert.Contains(f.Tabs.GetVisualDescendants().OfType<StackPanel>(), p => p.ContextMenu is not null);
        f.Filter.SelectedItem="Work"; Assert.Equal(17,f.Tabs.Items.Count);
        Assert.All(f.Engine.Sessions,s=>Assert.Equal(SessionState.Stopped,s.State)); Assert.Equal(0,f.Transport.Calls);
    }

    [AvaloniaFact]
    public async Task AddTargetDialogValidatesCancelsAndSavesWithoutProbes()
    {
        await using var f = new Fixture();
        Invoke(f.Window,"OpenManageGroups",null,new RoutedEventArgs());
        var groups=f.Window.OwnedWindows.OfType<ManageGroupsWindow>().Single();
        Click(groups,"AddGroupTarget"); var dialog=groups.OwnedWindows.OfType<SavedTargetWindow>().Single();
        Click(dialog,"SaveTargetOnly"); Assert.Empty(f.Workspace.Targets);
        Get<TextBox>(dialog,"SavedTargetAddress").Text="127.0.0.1"; Get<TextBox>(dialog,"SavedTargetName").Text="Router";
        Click(dialog,"CancelSavedTarget"); await Until(()=>groups.OwnedWindows.Count==0); Assert.Empty(f.Workspace.Targets);
        Click(groups,"AddGroupTarget"); dialog=groups.OwnedWindows.OfType<SavedTargetWindow>().Single();
        Get<TextBox>(dialog,"SavedTargetAddress").Text="127.0.0.1"; Get<TextBox>(dialog,"SavedTargetName").Text="Router";
        Click(dialog,"SaveTargetOnly"); await Until(()=>f.Workspace.Targets.Count==1 && groups.OwnedWindows.Count==0);
        Assert.Equal("Router",f.Workspace.Targets[0].DisplayName); Assert.Empty(f.Engine.Sessions); Assert.Equal(0,f.Transport.Calls);
        var row=Assert.Single(Get<DataGrid>(groups,"GroupMembers").ItemsSource!.Cast<ManageGroupsWindow.MemberRow>());
        Assert.Equal("Saved",row.Monitoring);
        Click(groups,"AddGroupTarget"); dialog=groups.OwnedWindows.OfType<SavedTargetWindow>().Single();
        Get<TextBox>(dialog,"SavedTargetAddress").Text="127.0.0.1"; Click(dialog,"SaveAndMonitorTarget");
        await Until(()=>f.Workspace.Targets.Count==2 && f.Engine.Sessions.Count==1 && groups.OwnedWindows.Count==0);
        Assert.True(f.Workspace.Targets[0].Closed); Assert.False(f.Workspace.Targets[1].Closed); groups.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task StartupPreferenceControlsOnlyPreviouslyOpenTargets(bool restore)
    {
        var options = new ProbeOptions { MaxHops=1, ResolveHostnames=false, Protocol=ProbeProtocol.Tcp, DestinationPort=8443 };
        var open = new WorkspaceTarget("127.0.0.1",options,"Work",Paused:true,Id:Guid.NewGuid());
        var closed = open with { Id=Guid.NewGuid(), Closed=true, DisplayName="Saved only" };
        var initial = new WorkspaceConfiguration { Name="Startup fixture", RestoreOnStartup=restore, Groups=["Work"], Targets=[open,closed] };
        await using var f=new Fixture(initial);
        await Until(()=>f.Workspace.Name=="Startup fixture" && (!restore || f.Engine.Sessions.Count==1));
        Assert.Equal(restore ? 1 : 0, f.Engine.Sessions.Count);
        if(restore) { var session=Assert.Single(f.Engine.Sessions); Assert.Equal(SessionState.Paused,session.State); Assert.Equal(options,session.Options); }
        Assert.Equal(new[] {open.Id,closed.Id},f.Workspace.Targets.Select(t=>t.Id));
    }

    [AvaloniaFact]
    public async Task MainBulkMenuScopesGroupAndAllAcrossHiddenTabs()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work"); await f.Save("127.0.0.2","Gaming");
        f.Filter.SelectedItem="Work";
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="Group:Start" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window) && f.Engine.Sessions.Count==1);
        Assert.Equal("Work",f.Engine.Sessions[0].Group);
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="All:Start" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window) && f.Engine.Sessions.Count==2); Assert.Single(f.Tabs.Items);
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="All:Pause" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window)); Assert.All(f.Engine.Sessions,t=>Assert.Equal(SessionState.Paused,t.State));
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="Group:Resume" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window)); Assert.Equal(SessionState.Paused,f.Engine.Sessions.Single(t=>t.Group=="Gaming").State);
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="All:Resume" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window)); Assert.All(f.Engine.Sessions,t=>Assert.Equal(SessionState.Running,t.State));
        Invoke(f.Window,"BulkMonitoringAction",new MenuItem { Tag="All:Stop" },new RoutedEventArgs());
        await Until(()=>!Busy(f.Window)); Assert.All(f.Engine.Sessions,t=>Assert.Equal(SessionState.Stopped,t.State));
        Assert.Equal("Work",f.Filter.SelectedItem);
    }

    [AvaloniaFact]
    public async Task RepeatedStartAndConcurrentEditsCannotDuplicateOrRemoveStartingDefinitions()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work"); await f.Save("127.0.0.2","Work");
        var ids=f.Workspace.Targets.Select(t=>t.Id).ToArray();
        var pending=f.Actions(ids,"Start");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Actions(ids,"Start"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Commit(f.Workspace));
        await pending; Assert.Equal(2,f.Engine.Sessions.Count);
        Assert.Equal(2,f.Engine.Sessions.Select(t=>t.Id).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task SettingsChangeForOneSessionDoesNotEditSameAddressLibrarySiblings()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.1","Work");
        var open=Assert.Single(f.Engine.Sessions); var sibling=f.Workspace.Targets[1];
        var updated=open.Options with { MaxHops=3 };
        await (Task)Invoke(f.Window,"ApplySettingsAsync",new SettingsChange(f.Workspace,updated), (Guid?)open.Id)!;
        Assert.Equal(updated,f.Workspace.Targets[0].Options); Assert.Equal(sibling.Options,f.Workspace.Targets[1].Options);
        Assert.True(f.Workspace.Targets[1].Closed); Assert.Single(f.Engine.Sessions);
    }

    [AvaloniaFact]
    public async Task OverflowDistinguishesDuplicateSessionsAndRevealsChosenHiddenGroup()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.1","Gaming",true);
        f.Filter.SelectedItem="Work"; var button=f.Window.FindControl<Button>("TargetOverflow")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu=Assert.IsType<MenuFlyout>(button.Flyout); var items=menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(2,items.Length); Assert.NotEqual(items[0].Header,items[1].Header);
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("Gaming",f.Filter.SelectedItem);
        Assert.Equal(f.Workspace.Targets[1].Id,Assert.IsType<TargetItem>(f.Tabs.SelectedItem).SavedTargetId);
        Assert.Equal(2,f.Engine.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task BulkActionsWaitForIndividualStopToReleaseItsWorker()
    {
        await using var f=new Fixture(); f.Transport.Hold=true;
        await f.Save("127.0.0.1","Work",true); await Until(()=>f.Transport.Calls>0);
        var item=Assert.Single(f.Tabs.Items.Cast<TargetItem>());
        var stopping=(Task)Invoke(f.Window,"StopTargetAsync",item)!;
        try
        {
            Assert.False(f.Window.FindControl<Button>("StartButton")!.IsEnabled);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Actions([item.SavedTargetId],"Start"));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Commit(f.Workspace));
            Assert.Single(f.Engine.Sessions);
        }
        finally { f.Transport.Release.TrySetResult(new("127.0.0.1",2,ProbeStatus.Success)); await stopping; }
        Assert.Equal(SessionState.Stopped,item.Session!.State); Assert.Single(f.Workspace.Targets);
    }

    [AvaloniaFact]
    public async Task OpenSavedHostnameCreatesStoppedTabWithoutDnsOrProbesAndCanEditOptions()
    {
        await using var f=new Fixture(); await f.Save("saved-target.invalid","Work");
        var definition=Assert.Single(f.Workspace.Targets);
        await (Task)Invoke(f.Window,"OpenLibraryTargetAsync",definition.Id)!;
        var item=Assert.Single(f.Tabs.Items.Cast<TargetItem>());
        Assert.Equal(definition.Id,item.SavedTargetId); Assert.Equal(SessionState.Stopped,item.Session!.State);
        Assert.Equal("saved-target.invalid",item.Address);
        Assert.Equal(0,f.Transport.Calls); Assert.Null(item.Session.LatestSnapshot);
        f.Engine.UpdateOptions(item.Id,item.Session.Options with { MaxHops=5,Protocol=ProbeProtocol.Tcp,DestinationPort=8443 });
        var captured=f.Workspace; Assert.False(captured.Targets[0].Closed); Assert.True(captured.Targets[0].Stopped);
        await f.Apply(captured);
        var restored=Assert.Single(f.Tabs.Items.Cast<TargetItem>());
        Assert.Equal(item.Id,restored.Id); Assert.Equal(5,restored.Session!.Options.MaxHops); Assert.Equal(0,f.Transport.Calls);
    }

    [AvaloniaTheory]
    [InlineData("Start",SessionState.Running)] [InlineData("Pause",SessionState.Paused)] [InlineData("Stop",SessionState.Stopped)]
    public async Task OpenExistingDuplicateAddressRevealsExactTabWithoutChangingItsState(string action,SessionState state)
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.1","Gaming",true);
        var work=f.Workspace.Targets[0]; var original=f.Engine.Sessions.Single(t=>t.Group=="Work");
        await f.Actions([work.Id],action); f.Filter.SelectedItem="Gaming";
        await (Task)Invoke(f.Window,"OpenLibraryTargetAsync",work.Id)!;
        Assert.Equal("Work",f.Filter.SelectedItem); Assert.Equal(original.Id,Assert.IsType<TargetItem>(f.Tabs.SelectedItem).Id);
        Assert.Same(original,f.Engine.Sessions.Single(t=>t.Id==original.Id)); Assert.Equal(state,original.State);
        Assert.Equal(2,f.Engine.Sessions.Count); Assert.Equal(2,f.Workspace.Targets.Count);
    }

    [AvaloniaFact]
    public async Task ManageGroupsUsesClearStatusesAndOpenButtonOrEnterReturnsToStoppedTab()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work"); await f.Save("127.0.0.2","Work",true); await f.Save("127.0.0.3","Work",true);
        var saved=f.Workspace.Targets[0]; await f.Actions([f.Workspace.Targets[2].Id],"Pause");
        var dialog=Groups(f); var members=Get<DataGrid>(dialog,"GroupMembers");
        Assert.Equal(new[] {"Saved","Monitoring","Paused"},members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>().Select(r=>r.Monitoring));
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(),b=>b.Name=="GroupMonitoringActions");
        Assert.False(Get<Button>(dialog,"OpenGroupTarget").IsEnabled);
        members.SelectedItem=members.ItemsSource.Cast<ManageGroupsWindow.MemberRow>().First(); Click(dialog,"OpenGroupTarget");
        await Until(()=>!dialog.IsVisible); var opened=Assert.IsType<TargetItem>(f.Tabs.SelectedItem);
        Assert.Equal(saved.Id,opened.SavedTargetId); Assert.Equal(SessionState.Stopped,opened.Session!.State); Assert.Null(opened.Snapshot);
        dialog=Groups(f); members=Get<DataGrid>(dialog,"GroupMembers");
        members.SelectedItem=members.ItemsSource!.Cast<ManageGroupsWindow.MemberRow>().Single(r=>r.Id==saved.Id);
        Assert.Equal("Stopped",((ManageGroupsWindow.MemberRow)members.SelectedItem).Monitoring);
        members.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter });
        await Until(()=>!dialog.IsVisible); Assert.Equal(opened.Id,Assert.IsType<TargetItem>(f.Tabs.SelectedItem).Id);
        Assert.Equal(3,f.Engine.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task DoubleClickOpensOnlySelectedSavedTargetWithoutStartingIt()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work"); await f.Save("127.0.0.1","Work");
        var second=f.Workspace.Targets[1]; var dialog=Groups(f); var members=Get<DataGrid>(dialog,"GroupMembers");
        dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var row=members.GetVisualDescendants().OfType<DataGridRow>().Single(r=>r.DataContext is ManageGroupsWindow.MemberRow m && m.Id==second.Id);
        var point=row.TranslatePoint(new Point(80,row.Bounds.Height/2),dialog)!.Value;
        dialog.MouseMove(point);
        dialog.MouseDown(point,MouseButton.Left); if(dialog.IsVisible) dialog.MouseUp(point,MouseButton.Left);
        dialog.MouseDown(point,MouseButton.Left); if(dialog.IsVisible) dialog.MouseUp(point,MouseButton.Left);
        await Until(()=>!dialog.IsVisible);
        Assert.Equal(second.Id,Assert.IsType<TargetItem>(f.Tabs.SelectedItem).SavedTargetId);
        Assert.Equal(SessionState.Stopped,Assert.Single(f.Engine.Sessions).State); Assert.Equal(0,f.Transport.Calls);
        Assert.True(f.Workspace.Targets[0].Closed);
    }

    [AvaloniaFact]
    public async Task CloseGroupConfirmsCancelsThenPreservesLibraryHistoryAndHiddenMonitoring()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.2","Work",true);
        await f.Save("127.0.0.3","Gaming",true); await f.Save("127.0.0.4","Work"); f.Filter.SelectedItem="Work";
        await Until(()=>f.Engine.Sessions.All(t=>t.LatestSnapshot is not null));
        var original=f.Engine.Sessions.ToArray(); var hidden=original.Single(t=>t.Group=="Gaming");
        var closing=(Task)Invoke(f.Window,"CloseTargetTabsAsync","Work")!;
        var confirmation=Assert.Single(f.Window.OwnedWindows); var cancel=Get<Button>(confirmation,"CancelCloseTargetTabs");
        Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel);
        var round=hidden.LatestSnapshot!.Round; await Until(()=>hidden.LatestSnapshot!.Round>round);
        Click(confirmation,"CancelCloseTargetTabs"); await closing;
        Assert.Equal(original.Select(t=>t.Id),f.Engine.Sessions.Select(t=>t.Id)); Assert.Equal(4,f.Workspace.Targets.Count);
        closing=(Task)Invoke(f.Window,"CloseTargetTabsAsync","Work")!;
        confirmation=Assert.Single(f.Window.OwnedWindows);
        Assert.Contains("Saved targets",string.Join(" ",confirmation.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text)));
        Click(confirmation,"ConfirmCloseTargetTabs"); await closing;
        Assert.Same(hidden,Assert.Single(f.Engine.Sessions)); Assert.Empty(f.Tabs.Items); Assert.Equal("Work",f.Filter.SelectedItem);
        Assert.Equal(4,f.Workspace.Targets.Count); Assert.All(f.Workspace.Targets.Where(t=>t.Group=="Work"),t=>Assert.True(t.Closed));
        Assert.Equal(original.Select(t=>t.Id).Order(),(await f.Engine.Storage.LoadSessionsAsync()).Select(t=>t.Id).Order());
        Assert.Contains("Work",f.Workspace.Groups);
        f.Window.FindControl<MenuItem>("StartGroupAction")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>!Busy(f.Window) && f.Engine.Sessions.Count==4);
        Assert.Equal(3,f.Tabs.Items.Count); Assert.Equal(4,f.Engine.Sessions.Select(t=>t.Id).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task CloseAllIncludesHiddenAndHistoricalTabsWithoutDeletingDefinitions()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.2","Gaming",true);
        var definitions=f.Workspace.Targets.ToArray();
        var targets=(System.Collections.ObjectModel.ObservableCollection<TargetItem>)typeof(MainWindow).GetField("_targets",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Window)!;
        targets.Add(new TargetItem(new StoredSession(Guid.NewGuid(),"Archived","127.0.0.1",DateTimeOffset.UtcNow,null,0)));
        f.Filter.SelectedItem="Work";
        var pending=(Task)Invoke(f.Window,"CloseTargetTabsAsync",new object?[] {null})!;
        Click(Assert.Single(f.Window.OwnedWindows),"ConfirmCloseTargetTabs"); await pending;
        Assert.Empty(targets); Assert.Empty(f.Engine.Sessions); Assert.Empty(f.Tabs.Items);
        Assert.Equal(definitions.Select(t=>t.Id),f.Workspace.Targets.Select(t=>t.Id));
        Assert.All(f.Workspace.Targets,t=>Assert.True(t.Closed)); Assert.Contains("Gaming",f.Workspace.Groups);
        f.Window.FindControl<MenuItem>("StartAllAction")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>!Busy(f.Window) && f.Engine.Sessions.Count==2); Assert.Single(f.Tabs.Items);
        f.Window.FindControl<MenuItem>("PauseAllAction")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>!Busy(f.Window)); Assert.All(f.Engine.Sessions,t=>Assert.Equal(SessionState.Paused,t.State));
    }

    [AvaloniaFact]
    public async Task SingleGroupTabClosesWithoutPromptAndReopensAsStoppedWithoutProbes()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true); await f.Save("127.0.0.2","Gaming",true);
        var work=f.Workspace.Targets[0]; var previous=f.Engine.Sessions.Single(t=>t.Group=="Work").Id;
        await (Task)Invoke(f.Window,"CloseTargetTabsAsync","Work")!; Assert.Empty(f.Window.OwnedWindows);
        await (Task)Invoke(f.Window,"OpenLibraryTargetAsync",work.Id)!;
        var opened=Assert.IsType<TargetItem>(f.Tabs.SelectedItem); Assert.Equal(previous,opened.Id); Assert.Equal(SessionState.Stopped,opened.Session!.State);
        Assert.Equal(2,f.Workspace.Targets.Count); Assert.Equal(2,f.Engine.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task MonitoringMenuHasDirectGroupAndGlobalActionsAndOneAdvancedSubmenu()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work"); f.Filter.SelectedItem="Work";
        var menu=Assert.IsType<MenuFlyout>(f.Window.FindControl<Button>("MonitoringMenuButton")!.Flyout);
        var direct=menu.Items.OfType<MenuItem>().ToArray();
        foreach(var name in new[] {"StartGroupAction","PauseGroupAction","CloseGroupTabsAction","StartAllAction","PauseAllAction","CloseAllTabsAction"})
        { var action=f.Window.FindControl<MenuItem>(name)!; Assert.Contains(action,direct); Assert.Empty(action.Items); }
        Assert.Equal("Start / Resume Work",f.Window.FindControl<MenuItem>("StartGroupAction")!.Header);
        Assert.Equal("Close All Work Tabs…",f.Window.FindControl<MenuItem>("CloseGroupTabsAction")!.Header);
        Assert.Single(direct,i=>i.Items.Count>0);
        Assert.False(f.Window.FindControl<MenuItem>("PauseGroupAction")!.IsEnabled);
        f.Filter.SelectedIndex=0;
        Assert.False(f.Window.FindControl<MenuItem>("StartGroupAction")!.IsVisible);
        Assert.False(f.Window.FindControl<MenuItem>("BulkGroupActions")!.IsVisible);
        Assert.True(f.Window.FindControl<MenuItem>("StartAllAction")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task OpenSavedTargetDoesNotReuseGuidAlreadyDisplayedByArchivedHistoryTab()
    {
        await using var f=new Fixture(); await f.Save("127.0.0.1","Work",true);
        var saved=Assert.Single(f.Workspace.Targets); var original=Assert.Single(f.Engine.Sessions).Id;
        await (Task)Invoke(f.Window,"CloseTargetTabsAsync","Work")!;
        var targets=(System.Collections.ObjectModel.ObservableCollection<TargetItem>)typeof(MainWindow).GetField("_targets",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(f.Window)!;
        var history=new TargetItem(new StoredSession(original,"127.0.0.1","127.0.0.1",DateTimeOffset.UtcNow,null,0)); targets.Add(history);
        await (Task)Invoke(f.Window,"OpenLibraryTargetAsync",saved.Id)!;
        var opened=Assert.IsType<TargetItem>(f.Tabs.SelectedItem);
        Assert.NotEqual(history.Id,opened.Id); Assert.Equal(saved.Id,opened.SavedTargetId);
        Assert.Equal(SessionState.Stopped,opened.Session!.State); Assert.Equal(2,targets.Select(t=>t.Id).Distinct().Count());
        Assert.Contains(await f.Engine.Storage.LoadSessionsAsync(),s=>s.Id==original);
    }

    private static ManageGroupsWindow Groups(Fixture f)
    {
        Invoke(f.Window,"OpenManageGroups",null,new RoutedEventArgs());
        var dialog=f.Window.OwnedWindows.OfType<ManageGroupsWindow>().Single();
        var groups=Get<DataGrid>(dialog,"GroupsTable"); groups.SelectedItem=groups.ItemsSource!.Cast<ManageGroupsWindow.GroupRow>().Single(g=>g.Name=="Work");
        return dialog;
    }

    private static bool Busy(MainWindow window)=>(bool)typeof(MainWindow).GetField("_bulkMonitoring",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;

    private static T Get<T>(Window window,string name) where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    private static void Click(Window window,string name)=>Get<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Invoke(object owner,string method,params object?[] args)=>owner.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(owner,args);
    private static async Task Until(Func<bool> ready) { for(var i=0;!ready() && i<400;i++) await Task.Delay(10); Assert.True(ready()); }
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly string Directory=Path.Combine(Path.GetTempPath(),"pngr-library-tests",Guid.NewGuid().ToString("N"));
        public readonly Transport Transport=new(); public readonly TraceEngine Engine; public readonly MainWindow Window;
        public ListBox Tabs=>Window.FindControl<ListBox>("TargetList")!; public ComboBox Filter=>Window.FindControl<ComboBox>("TargetGroupFilter")!;
        public WorkspaceConfiguration Workspace=>(WorkspaceConfiguration)Invoke(Window,"GroupWorkspace")!;
        public Fixture(WorkspaceConfiguration? initial=null)
        {
            if(initial is not null) new WorkspaceStore(Path.Combine(Directory,"workspace.json")).SaveAsync(initial).GetAwaiter().GetResult();
            Engine=new TraceEngine(Path.Combine(Directory,"history.db"),transport:Transport);
            Window=new MainWindow(["--data-dir",Directory,"--capture"],Engine);
            if(initial is not null) typeof(MainWindow).GetField("_runtimeFeatures",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(Window,true);
            else typeof(MainWindow).GetField("_configuration",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(Window,new WorkspaceConfiguration { Groups=["Work","Gaming","Empty"] });
            Window.Show();
        }
        public Task Save(string address,string group,bool monitor=false)=>(Task)Invoke(Window,"AddSavedTargetAsync",new WorkspaceTarget(address,new() { MaxHops=2,ResolveHostnames=false,Interval=TimeSpan.FromMilliseconds(500) },group),monitor)!;
        public Task Actions(IEnumerable<Guid> ids,string action)=>(Task)Invoke(Window,"RunTargetActionsAsync",ids.ToArray(),action)!;
        public Task Apply(WorkspaceConfiguration workspace)=>(Task)Invoke(Window,"ApplyWorkspaceAsync",workspace)!;
        public Task Commit(WorkspaceConfiguration workspace,string? old=null,string? next=null)=>(Task)Invoke(Window,"CommitGroupsAsync",workspace,old,next)!;
        public async ValueTask DisposeAsync() { foreach(var child in Window.OwnedWindows.ToArray()) child.Close(); Window.Close(); await Until(()=>!Window.IsVisible); await Engine.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory,true); }
    }
    private sealed class Transport:IProbeTransport
    {
        public int Calls; public bool Hold;
        public readonly TaskCompletionSource<ProbeResult> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProbeResult> ProbeAsync(IPAddress destination,int ttl,TimeSpan timeout,CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Hold ? Release.Task : Task.FromResult(new ProbeResult(destination.ToString(),2,ProbeStatus.Success)); }
    }
}
