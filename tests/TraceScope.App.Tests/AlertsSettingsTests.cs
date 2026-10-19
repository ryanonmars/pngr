using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class AlertsSettingsTests
{
    [AvaloniaFact]
    public void ExistingRulesPreserveIdentifiersDoublePrecisionTimingTicksAndCombinedScopes()
    {
        var original = new AlertRule { Id = "existing-rule", Name = new string('a', 110),
            Condition = AlertCondition.Latency, Threshold = 123.45678901234567,
            EvaluationPeriod = TimeSpan.FromTicks(123456789), Cooldown = TimeSpan.FromTicks(3000000001),
            RecoveryPeriod = TimeSpan.FromTicks(987654321), Target = "legacy.example", Group = "Preserved group", Enabled = false };
        using var fixture = new Editor([original, new AlertRule { Id = "second" }]);
        fixture.Grid.SelectedIndex = 0;
        Assert.Equal(original, fixture.View.CaptureRules()[0]);
        fixture.Grid.SelectedIndex = 1;
        fixture.Grid.SelectedIndex = 0;
        Assert.Equal(original, fixture.View.CaptureRules()[0]);
        Assert.False(Assert.IsType<AlertRuleRow>(fixture.Grid.SelectedItem).Enabled);
    }

    [AvaloniaFact]
    public void EditingSelectionsAndAddingRemovingRulesChangesOnlyTheLocalDraft()
    {
        var first = new AlertRule { Id = "first", Name = "Original", Threshold = 5 };
        var second = new AlertRule { Id = "second", Name = "Other", Condition = AlertCondition.Latency, Threshold = 200 };
        using var fixture = new Editor([first, second]);
        fixture.Grid.SelectedIndex = 0;
        fixture.Find<TextBox>("AlertRuleName").Text = "Renamed";
        fixture.Find<TextBox>("AlertRuleThreshold").Text = "7.25";
        Assert.IsType<AlertRuleRow>(fixture.Grid.SelectedItem).Enabled = false;
        fixture.Grid.SelectedIndex = 1;
        var changed = fixture.View.CaptureRules();
        Assert.Equal(first with { Name = "Renamed", Threshold = 7.25, Enabled = false }, changed[0]);
        Assert.Equal(second, changed[1]);
        Assert.Equal("Original", first.Name);
        fixture.Click("AlertRuleAdd");
        Assert.Equal(3, fixture.View.CaptureRules().Count);
        Assert.NotEqual(first.Id, fixture.View.CaptureRules()[2].Id);
        fixture.Find<TextBox>("AlertRuleName").Text = "New rule";
        fixture.Click("AlertRuleRemove");
        Assert.Equal(2, fixture.View.CaptureRules().Count);
        Assert.Equal("Renamed", fixture.View.CaptureRules()[0].Name);
    }

    [AvaloniaFact]
    public void RuleListCheckboxTogglesEnabledWithoutChangingSelectionOrOtherFields()
    {
        var original = new AlertRule { Id = "checkbox-rule", Name = "Enable inline", Threshold = 17 };
        using var fixture = new Editor([original]);
        var row = Assert.IsType<AlertRuleRow>(fixture.Grid.SelectedItem);
        fixture.Find<CheckBox>("AlertRuleEnabled_checkbox-rule").IsChecked = false;
        Assert.Equal(original with { Enabled = false }, Assert.Single(fixture.View.CaptureRules()));
        Assert.Same(row, fixture.Grid.SelectedItem);
        fixture.Find<CheckBox>("AlertRuleEnabled_checkbox-rule").IsChecked = true;
        Assert.Equal(original, Assert.Single(fixture.View.CaptureRules()));
    }

    [AvaloniaFact]
    public void ClearingSelectionHidesTheEditorAndDisablesRemovalWithoutChangingRules()
    {
        var original = new AlertRule { Id = "existing", Name = "Existing" };
        using var fixture = new Editor([original, new AlertRule { Id = "second" }]);
        Assert.NotNull(fixture.Grid.SelectedItem);
        fixture.Grid.SelectedIndex = -1;
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Null(fixture.Grid.SelectedItem);
        Assert.False(fixture.Find<TextBox>("AlertRuleName").IsEffectivelyVisible);
        Assert.False(fixture.Find<Button>("AlertRuleRemove").IsEnabled);
        Assert.Equal(original, fixture.View.CaptureRules()[0]);
        fixture.Grid.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.True(fixture.Find<TextBox>("AlertRuleName").IsEffectivelyVisible);
        Assert.True(fixture.Find<Button>("AlertRuleRemove").IsEnabled);
    }

    [AvaloniaFact]
    public void EmptyRulesDisableRemovalAndNewRulesReceiveUniquePersistentIds()
    {
        using var fixture = new Editor([]);
        Assert.Empty(fixture.View.CaptureRules());
        Assert.False(fixture.Find<Button>("AlertRuleRemove").IsEnabled);
        fixture.Click("AlertRuleAdd");
        var first = Assert.Single(fixture.View.CaptureRules());
        Assert.False(string.IsNullOrWhiteSpace(first.Id));
        Assert.Equal(first.Id, Assert.Single(fixture.View.CaptureRules()).Id);
        fixture.Click("AlertRuleRemove");
        Assert.Empty(fixture.View.CaptureRules());
        Assert.False(fixture.Find<Button>("AlertRuleRemove").IsEnabled);
    }

    [AvaloniaFact]
    public void ScopeAndConditionEditingPreservesThresholdForConditionsWithoutMeasurements()
    {
        using var fixture = new Editor([new AlertRule { Threshold = 42.125 }]);
        fixture.Grid.SelectedIndex = 0;
        fixture.Find<ComboBox>("AlertRuleScope").SelectedIndex = 1;
        fixture.Find<TextBox>("AlertRuleTarget").Text = "custom.example";
        var target = Assert.Single(fixture.View.CaptureRules());
        Assert.Equal("custom.example", target.Target);
        Assert.Null(target.Group);
        fixture.Find<ComboBox>("AlertRuleScope").SelectedIndex = 2;
        fixture.Find<TextBox>("AlertRuleGroup").Text = "Work";
        var group = Assert.Single(fixture.View.CaptureRules());
        Assert.Null(group.Target);
        Assert.Equal("Work", group.Group);
        foreach (var condition in new[] { AlertCondition.RouteChanged, AlertCondition.Recovery, AlertCondition.Unreachable })
        {
            fixture.Find<ComboBox>("AlertRuleCondition").SelectedIndex = (int)condition;
            var draft = Assert.Single(fixture.View.CaptureRules());
            Assert.Equal(condition, draft.Condition);
            Assert.Equal(42.125, draft.Threshold);
            Assert.False(fixture.Find<TextBox>("AlertRuleThreshold").IsEffectivelyVisible);
        }
        fixture.Find<ComboBox>("AlertRuleScope").SelectedIndex = 0;
        Assert.Null(Assert.Single(fixture.View.CaptureRules()).Target);
        Assert.Null(Assert.Single(fixture.View.CaptureRules()).Group);
    }

    [AvaloniaTheory]
    [InlineData("AlertRuleThreshold", "invalid")]
    [InlineData("AlertRuleThreshold", "-1")]
    [InlineData("AlertRuleThreshold", "NaN")]
    [InlineData("AlertRuleThreshold", "Infinity")]
    [InlineData("AlertRuleEvaluation", "-1")]
    [InlineData("AlertRuleCooldown", "")]
    [InlineData("AlertRuleRecovery", "9999999999999999999999999999")]
    public void InvalidNumbersAreRejectedWithoutMutatingTheOriginal(string control, string value)
    {
        var original = new AlertRule();
        using var fixture = new Editor([original]);
        fixture.Grid.SelectedIndex = 0;
        fixture.Find<TextBox>(control).Text = value;
        Assert.ThrowsAny<Exception>(() => fixture.View.CaptureRules());
        Assert.Equal(10, original.Threshold);
        Assert.Equal(TimeSpan.FromSeconds(30), original.EvaluationPeriod);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Find<TextBlock>("AlertRuleValidation").Text));
    }

    [AvaloniaFact]
    public void EditingFractionalSecondsRetainsExactTimeSpanTicks()
    {
        using var fixture = new Editor([new AlertRule()]);
        fixture.Grid.SelectedIndex = 0;
        fixture.Find<TextBox>("AlertRuleEvaluation").Text = "1.2345678";
        fixture.Find<TextBox>("AlertRuleCooldown").Text = "300.0000001";
        fixture.Find<TextBox>("AlertRuleRecovery").Text = "0.0000001";
        var rule = Assert.Single(fixture.View.CaptureRules());
        Assert.Equal(TimeSpan.FromTicks(12345678), rule.EvaluationPeriod);
        Assert.Equal(TimeSpan.FromTicks(3000000001), rule.Cooldown);
        Assert.Equal(TimeSpan.FromTicks(1), rule.RecoveryPeriod);
    }

    [AvaloniaFact]
    public void NumericEditingUsesPlatformCultureWithoutLosingPrecision()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var fixture = new Editor([new AlertRule { Threshold = 12.25, EvaluationPeriod = TimeSpan.FromTicks(15000001) }]);
            fixture.Grid.SelectedIndex = 0;
            Assert.Equal("12,25", fixture.Find<TextBox>("AlertRuleThreshold").Text);
            fixture.Find<TextBox>("AlertRuleThreshold").Text = "15,125";
            fixture.Find<TextBox>("AlertRuleEvaluation").Text = "2,0000001";
            var edited = Assert.Single(fixture.View.CaptureRules());
            Assert.Equal(15.125, edited.Threshold);
            Assert.Equal(TimeSpan.FromTicks(20000001), edited.EvaluationPeriod);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [AvaloniaTheory]
    [InlineData(530)]
    [InlineData(600)]
    public void ControlsFitTheAvailableSettingsWidthAndHaveAccessibleNames(double width)
    {
        using var fixture = new Editor([new AlertRule { Name = new string('x', 80) }], width);
        fixture.Grid.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        foreach (var name in new[] { "AlertRuleName", "AlertRuleCondition", "AlertRuleThreshold", "AlertRuleEvaluation", "AlertRuleCooldown", "AlertRuleRecovery" })
        {
            var control = fixture.Find<Control>(name);
            var point = control.TranslatePoint(default, fixture.Window)!.Value;
            Assert.True(control.Bounds.Width > 0, name);
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(control)), name);
            Assert.True(point.X >= 0 && point.X + control.Bounds.Width <= fixture.Window.Bounds.Width + 1, name);
        }
        Assert.Empty(fixture.View.GetVisualDescendants().OfType<NumericUpDown>());
        Assert.False(fixture.Find<Button>("AcknowledgeEvent").IsEnabled);
        Assert.True(fixture.Find<TextBlock>("AlertEventsEmpty").IsVisible);
    }

    [AvaloniaFact]
    public async Task ParentSettingsApplyCancelAndReopenPreserveExistingActionsAndRules()
    {
        var original = new WorkspaceConfiguration { Alerts = [new AlertRule { Id = "persisted", Name = "Existing" }],
            Actions = new AlertActionConfiguration { Webhook = new() { Format = WebhookFormat.Telegram,
                TelegramChatId = "-100123456789012", TelegramBotTokenSecretId = "11111111111111111111111111111111",
                UrlSecretId = "22222222222222222222222222222222" },
                Smtp = new() { Host = "mail.example", Username = "kept" }, Script = new() { Arguments = ["preserved"] } } };
        var saved = original;
        var applies = 0;
        SettingsWindow Open()
        {
            var window = new SettingsWindow(saved, null, null, [new("Automatic", null, null)], Path.GetTempPath(),
                change => { saved = change.Configuration; applies++; return Task.CompletedTask; },
                () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { });
            window.Show(); window.SelectPage("Alerts & notifications"); return window;
        }
        var canceled = Open();
        Find<DataGrid>(canceled, "AlertRulesTable").SelectedIndex = 0;
        Find<TextBox>(canceled, "AlertRuleName").Text = "Discard this";
        Click(Find<Button>(canceled, "SettingsCancel"));
        Assert.Same(original, saved); Assert.Equal(0, applies);
        var editor = Open();
        Find<DataGrid>(editor, "AlertRulesTable").SelectedIndex = 0;
        Find<TextBox>(editor, "AlertRuleName").Text = "Saved name";
        Find<TextBox>(editor, "AlertRuleThreshold").Text = "invalid";
        Click(Find<Button>(editor, "SettingsApply"));
        Assert.Equal(0, applies);
        Assert.StartsWith("Settings were not applied:", Find<TextBlock>(editor, "SettingsStatus").Text);
        Find<TextBox>(editor, "AlertRuleThreshold").Text = "6.5";
        Find<CheckBox>(editor, "SettingsInAppNotifications").IsChecked = false;
        Click(Find<Button>(editor, "SettingsApply"));
        await Until(() => applies == 1);
        Assert.Equal(original.Alerts.Single() with { Name = "Saved name", Threshold = 6.5 }, saved.Alerts.Single());
        Assert.False(saved.DesktopNotifications);
        Assert.Equal(original.Actions, saved.Actions);
        Find<TextBox>(editor, "AlertRuleName").Text = "Discard changes after Apply";
        Click(Find<Button>(editor, "SettingsCancel"));
        Assert.Equal("Saved name", saved.Alerts.Single().Name);
        Assert.Equal(1, applies);
        var reopened = Open();
        Find<DataGrid>(reopened, "AlertRulesTable").SelectedIndex = 0;
        Assert.Equal("Saved name", Find<TextBox>(reopened, "AlertRuleName").Text);
        Assert.Equal("6.5", Find<TextBox>(reopened, "AlertRuleThreshold").Text);
        reopened.Close();
    }

    [AvaloniaFact]
    public async Task RecentEventsRequireAnUnacknowledgedSelectionAndPreserveHistory()
    {
        var entry = new MonitoringEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            "RouteChange", "Network route changed", "rule");
        var acknowledged = entry with { Id = Guid.NewGuid(), AcknowledgedAt = DateTimeOffset.UtcNow };
        var calls = new List<MonitoringEvent>();
        using var fixture = new Editor([], acknowledge: item => { calls.Add(item); return Task.FromResult(true); });
        fixture.View.SetEvents([entry, acknowledged]);
        var events = fixture.Find<DataGrid>("AlertEventsTable");
        Assert.False(fixture.Find<TextBlock>("AlertEventsEmpty").IsVisible);
        Assert.True(events.IsVisible);
        Assert.False(fixture.Find<Button>("AcknowledgeEvent").IsEnabled);
        events.SelectedItem = acknowledged;
        Assert.False(fixture.Find<Button>("AcknowledgeEvent").IsEnabled);
        events.SelectedItem = entry;
        Assert.True(fixture.Find<Button>("AcknowledgeEvent").IsEnabled);
        fixture.Click("AcknowledgeEvent");
        await Until(() => calls.Count == 1);
        Assert.Equal(entry, calls.Single());
        Assert.Equal(2, events.ItemsSource!.Cast<MonitoringEvent>().Count());
        Assert.Equal(entry.Timestamp, events.ItemsSource!.Cast<MonitoringEvent>().Single(e => e.Id == entry.Id).Timestamp);
        fixture.View.SetEvents([]);
        Assert.True(fixture.Find<TextBlock>("AlertEventsEmpty").IsVisible);
        Assert.False(fixture.Find<Button>("AcknowledgeEvent").IsEnabled);
    }

    [AvaloniaFact]
    public void RefreshingEventsPreservesSelectionAndDoesNotHideOlderLoadedHistory()
    {
        var events = Enumerable.Range(0, 240).Select(i => new MonitoringEvent(Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-i), "Alert", $"Historical event {i}")).ToArray();
        using var fixture = new Editor([], acknowledge: _ => Task.FromResult(true));
        fixture.View.SetEvents(events);
        var table = fixture.Find<DataGrid>("AlertEventsTable");
        Assert.Equal(240, table.ItemsSource!.Cast<MonitoringEvent>().Count());
        table.SelectedItem = events[^1];
        fixture.View.SetEvents(events.Append(new MonitoringEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "Recovery", "New event")));
        Assert.Equal(events[^1].Id, Assert.IsType<MonitoringEvent>(table.SelectedItem).Id);
        Assert.Equal(241, table.ItemsSource!.Cast<MonitoringEvent>().Count());
    }

    [AvaloniaTheory]
    [InlineData(" ", null)]
    [InlineData(null, " ")]
    [InlineData(" ", " ")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ExistingWhitespaceEmptyAndNullScopesRemainUnchanged(string? target, string? group)
    {
        var original = new AlertRule { Target = target, Group = group };
        using var fixture = new Editor([original]);
        Assert.Equal(original, Assert.Single(fixture.View.CaptureRules()));
    }

    [AvaloniaTheory]
    [InlineData(1, null)]
    [InlineData(1, "")]
    [InlineData(2, null)]
    [InlineData(2, "")]
    public void SelectingTargetOrGroupWithoutADestinationCannotSilentlySaveAnAllTargetsRule(int scope, string? empty)
    {
        var original = new AlertRule { Target = empty, Group = empty };
        using var fixture = new Editor([original], targets: [], groups: []);
        fixture.Find<ComboBox>("AlertRuleScope").SelectedIndex = scope;
        fixture.Find<TextBox>(scope == 1 ? "AlertRuleTarget" : "AlertRuleGroup").Text = empty;
        Assert.ThrowsAny<Exception>(() => fixture.View.CaptureRules());
        Assert.True(fixture.Find<TextBlock>("AlertRuleValidation").IsVisible);
        Assert.Equal(empty, original.Target);
        Assert.Equal(empty, original.Group);
    }

    [AvaloniaTheory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void ExistingTimingBoundaryValuesRoundTripExactly(long ticks)
    {
        var original = new AlertRule { EvaluationPeriod = TimeSpan.FromTicks(ticks), Cooldown = TimeSpan.FromTicks(ticks), RecoveryPeriod = TimeSpan.FromTicks(ticks) };
        using var fixture = new Editor([original]);
        Assert.Equal(original, Assert.Single(fixture.View.CaptureRules()));
    }

    [AvaloniaFact]
    public async Task FailedApplyRetainsTheRuleDraftAndRetriesWithoutAcceptingTheFailedBaseline()
    {
        var original = new WorkspaceConfiguration { Alerts = [new AlertRule { Id = "saved-rule", Name = "Saved" }] };
        var saved = original;
        var fail = true;
        var attempts = 0;
        var window = new SettingsWindow(original, null, null, [new("Automatic", null, null)], Path.GetTempPath(),
            change => { attempts++; if (fail) throw new IOException("Simulated persistence failure"); saved = change.Configuration; return Task.CompletedTask; },
            () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { });
        window.Show(); window.SelectPage("Alerts & notifications");
        try
        {
            Find<TextBox>(window, "AlertRuleName").Text = "Retry this draft";
            Click(Find<Button>(window, "SettingsApply"));
            await Until(() => attempts == 1 && Find<Button>(window, "SettingsApply").IsEnabled);
            Assert.Same(original, saved);
            Assert.Contains("Simulated persistence failure", Find<TextBlock>(window, "SettingsStatus").Text);
            Assert.Equal("Retry this draft", Find<TextBox>(window, "AlertRuleName").Text);
            Assert.True(window.AlertRulesEditor.RulesChanged);
            fail = false;
            Click(Find<Button>(window, "SettingsApply"));
            await Until(() => attempts == 2 && Find<TextBlock>(window, "SettingsStatus").Text!.StartsWith("Settings saved."));
            Assert.Equal("Retry this draft", Assert.Single(saved.Alerts).Name);
            Assert.False(window.AlertRulesEditor.RulesChanged);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AcceptingSavedSnapshotDoesNotMarkLaterEditsAsPersisted()
    {
        using var fixture = new Editor([new AlertRule { Name = "Initially saved" }]);
        fixture.Find<TextBox>("AlertRuleName").Text = "Captured for Apply";
        var snapshot = fixture.View.CaptureRules();
        fixture.Find<TextBox>("AlertRuleName").Text = "Edited while saving";
        fixture.View.AcceptChanges(snapshot);
        Assert.True(fixture.View.RulesChanged);
        Assert.Equal("Edited while saving", Assert.Single(fixture.View.CaptureRules()).Name);
        Assert.Equal("Captured for Apply", Assert.Single(snapshot).Name);
    }

    private static T Find<T>(Window window, string name) where T : Control
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
    }

    private sealed class Editor : IDisposable
    {
        public AlertSettingsView View { get; }
        public Window Window { get; }
        public DataGrid Grid => Find<DataGrid>("AlertRulesTable");
        public Editor(IEnumerable<AlertRule> rules, double width = 600, Func<MonitoringEvent, Task<bool>>? acknowledge = null,
            IEnumerable<string>? targets = null, IEnumerable<string>? groups = null)
        {
            View = new AlertSettingsView(rules, targets ?? ["8.8.8.8"], groups ?? ["Work"], new Border(), acknowledge);
            Window = new Window { Width = width, Height = 580, Content = new ScrollViewer { Content = View } };
            Window.Show(); Dispatcher.UIThread.RunJobs();
        }
        public T Find<T>(string name) where T : Control
        {
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
            return Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        }
        public void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public void Dispose() => Window.Close();
    }
}
