using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App.Views;

/// <summary>A private alert draft. Only the owning Settings window persists CaptureRules().</summary>
public sealed class AlertSettingsView : UserControl
{
    private static readonly IBrush Muted = Brush.Parse("#A8ADB7");
    private AlertRule[] _original;
    private readonly ObservableCollection<AlertRuleRow> _rules;
    private readonly DataGrid _table = Table("AlertRulesTable", 115);
    private readonly TextBox _name = Input("AlertRuleName", "Rule name");
    private readonly ComboBox _condition = Choice("AlertRuleCondition", "Condition", ["Latency", "Packet loss", "Jitter", "Unreachable", "Route changed", "Recovery"]);
    private readonly ComboBox _scope = Choice("AlertRuleScope", "Applies to", ["All targets", "Target", "Group", "Target and group"]);
    private readonly TextBox _target = Input("AlertRuleTarget", "Target");
    private readonly TextBox _group = Input("AlertRuleGroup", "Group");
    private readonly TextBox _threshold = Input("AlertRuleThreshold", "Threshold");
    private readonly TextBox _evaluation = Input("AlertRuleEvaluation", "Evaluation period in seconds");
    private readonly TextBox _cooldown = Input("AlertRuleCooldown", "Cooldown in seconds");
    private readonly TextBox _recovery = Input("AlertRuleRecovery", "Recovery period in seconds");
    private readonly TextBlock _thresholdLabel = Label("Threshold (ms)");
    private readonly TextBlock _evaluationLabel = Label("Evaluation (seconds)");
    private readonly TextBlock _conditionHint = Note("");
    private readonly TextBlock _timingHint = Note("");
    private readonly TextBlock _validation = new() { Name = "AlertRuleValidation", Foreground = Brush.Parse("#E1AD91"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _emptyRules = Note("No alert rules. Add a rule to choose what to monitor.");
    private readonly TextBlock _emptyEditor = Note("Add a rule to configure its condition and timing.");
    private readonly Button _add = Action("AlertRuleAdd", "Add rule");
    private readonly Button _remove = Action("AlertRuleRemove", "Remove");
    private readonly StackPanel _editor = new() { Spacing = 12 };
    private readonly StackPanel _thresholdField;
    private readonly StackPanel _targetField;
    private readonly StackPanel _groupField;
    private readonly StackPanel _evaluationField;
    private readonly StackPanel _recoveryField;
    private readonly Grid _scopeRow;
    private readonly Grid _conditionRow;
    private readonly Grid _timingRow;
    private readonly DataGrid _events = Table("AlertEventsTable", 152);
    private readonly TextBlock _emptyEvents = Note("No recent events. Alerts, recoveries and route changes will appear here.");
    private readonly TextBlock _eventStatus = Note("");
    private readonly Button _acknowledge = Action("AcknowledgeEvent", "Acknowledge");
    private readonly Func<MonitoringEvent, Task<bool>>? _acknowledgeEvent;
    private readonly string[] _targets;
    private readonly string[] _groups;
    private AlertRuleRow? _selected;
    private bool _loading;
    private bool _acknowledging;

    public AlertSettingsView(IEnumerable<AlertRule> rules, IEnumerable<string> targets, IEnumerable<string> groups,
        Control notificationSection, Func<MonitoringEvent, Task<bool>>? acknowledge = null, Func<Guid, string>? eventTarget = null)
    {
        _original = rules.ToArray();
        _rules = new(_original.Select(rule => new AlertRuleRow(rule)));
        _targets = targets.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _groups = groups.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        _acknowledgeEvent = acknowledge;
        _target.MaxLength = 253; _group.MaxLength = 80;
        _target.Watermark = _targets.FirstOrDefault() ?? "Hostname or IP address";
        _group.Watermark = _groups.FirstOrDefault() ?? "Group name";
        ToolTip.SetTip(_target, "Matches the monitored target, ignoring letter case. You can enter an inactive target.");
        ToolTip.SetTip(_group, "Matches the group name exactly, including letter case. You can enter an inactive group.");
        _table.ItemsSource = _rules;
        _table.Columns.Add(new DataGridTemplateColumn { Header = "Enabled", Width = new DataGridLength(60), CanUserResize = false, CanUserSort = false,
            CellTemplate = new FuncDataTemplate<AlertRuleRow>((row, _) =>
            {
                var check = new CheckBox { Name = row is null ? null : "AlertRuleEnabled_" + row.Rule.Id, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinHeight = 26 };
                if (row is null) return check;
                check.Bind(AutomationProperties.NameProperty, new Binding(nameof(AlertRuleRow.Name)) { Source = row, StringFormat = "Enable {0}" });
                check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(AlertRuleRow.Enabled)) { Source = row, Mode = BindingMode.TwoWay });
                return check;
            }) });
        AddRuleColumn("Rule", nameof(AlertRuleRow.Name), 1.1);
        AddRuleColumn("Trigger", nameof(AlertRuleRow.Trigger), 1.2);
        AddRuleColumn("Applies to", nameof(AlertRuleRow.AppliesTo), 1.1);
        _table.SelectionChanged += (_, _) => SelectRule();
        _table.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _selected is not null) { RemoveRule(); e.Handled = true; }
            if (e.Key == Key.Enter && _selected is not null) { _name.Focus(); e.Handled = true; }
        };
        _add.Click += (_, _) => AddRule();
        _remove.Click += (_, _) => RemoveRule();
        _thresholdField = Field(_thresholdLabel, _threshold);
        _targetField = Field("Target", _target); _groupField = Field("Group", _group);
        _evaluationField = Field(_evaluationLabel, _evaluation);
        _recoveryField = Field("Recovery (seconds)", _recovery);
        _editor.Children.Add(Columns(Field("Rule name", _name), Field("Applies to", _scope)));
        _scopeRow = Columns(_targetField, _groupField);
        _conditionRow = Columns(Field("Condition", _condition), _thresholdField);
        _timingRow = Columns(_evaluationField, Field("Cooldown (seconds)", _cooldown), _recoveryField);
        _editor.Children.Add(_scopeRow);
        _editor.Children.Add(_conditionRow);
        _editor.Children.Add(_timingRow);
        _editor.Children.Add(_validation);
        foreach (var input in new[] { _name, _target, _group, _threshold, _evaluation, _cooldown, _recovery })
            input.TextChanged += (_, _) => { if (!_loading) TryCaptureEditor(); };
        _condition.SelectionChanged += (_, _) => { if (!_loading) { UpdateCondition(); TryCaptureEditor(); } };
        _scope.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _loading = true;
            if (_scope.SelectedIndex is 1 or 3 && string.IsNullOrEmpty(_target.Text)) _target.Text = _targets.FirstOrDefault();
            if (_scope.SelectedIndex is 2 or 3 && string.IsNullOrEmpty(_group.Text)) _group.Text = _groups.FirstOrDefault();
            _loading = false; UpdateScope(); TryCaptureEditor();
        };
        AddColumn(_events, "Time", nameof(MonitoringEvent.Timestamp), 0, 74, "{0:HH:mm:ss}");
        _events.Columns.Add(new DataGridTemplateColumn { Header = "Event", SortMemberPath = nameof(MonitoringEvent.Kind), Width = new DataGridLength(80),
            CellTemplate = new FuncDataTemplate<MonitoringEvent>((item, _) => Cell(item?.Kind == "RouteChange" ? "Route change" : item?.Kind)) });
        _events.Columns.Add(new DataGridTemplateColumn { Header = "Target", CanUserSort = false, Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<MonitoringEvent>((item, _) => Cell(item is null ? "" : eventTarget?.Invoke(item.SessionId) ?? "—")) });
        _events.Columns.Add(new DataGridTemplateColumn { Header = "Message", SortMemberPath = nameof(MonitoringEvent.Message), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<MonitoringEvent>((item, _) => Cell(item?.Message)) });
        AddColumn(_events, "Ack.", nameof(MonitoringEvent.AcknowledgedAt), 0, 68, "{0:HH:mm:ss}");
        _events.SelectionChanged += (_, _) => UpdateAcknowledge();
        _acknowledge.Click += async (_, _) => await AcknowledgeAsync();
        _emptyEvents.Name = "AlertEventsEmpty";
        _emptyEvents.Text = "No alert events recorded\nTriggered alerts, recoveries, and route changes will appear here.";
        _emptyEvents.TextAlignment = TextAlignment.Center;
        _emptyEvents.Margin = new Thickness(0, 7, 0, 12);
        _emptyRules.Margin = new Thickness(0, 10);
        _emptyRules.TextAlignment = TextAlignment.Center;
        var ruleList = new Grid(); ruleList.Children.Add(_table); ruleList.Children.Add(_emptyRules);
        var eventList = new Grid(); eventList.Children.Add(_events); eventList.Children.Add(_emptyEvents);
        var rulesHeader = SectionHeader("Alert Rules", _add, _remove);
        var eventsHeader = SectionHeader("Recent Events", _acknowledge);
        var layout = new StackPanel { Spacing = 8 };
        layout.Children.Add(rulesHeader); layout.Children.Add(ruleList);
        layout.Children.Add(Divider()); layout.Children.Add(SectionHeader("Rule Configuration"));
        layout.Children.Add(_emptyEditor); layout.Children.Add(_editor);
        layout.Children.Add(notificationSection);
        layout.Children.Add(Divider()); layout.Children.Add(eventsHeader);
        layout.Children.Add(eventList); layout.Children.Add(_eventStatus);
        Content = layout;
        SetEvents([]);
        _table.SelectedItem = _rules.FirstOrDefault();
        LoadEditor();
    }

    public bool RulesChanged => !CaptureRules().SequenceEqual(_original);

    public void AcceptChanges(IReadOnlyList<AlertRule>? savedRules = null)
        => _original = (savedRules ?? CaptureRules()).ToArray();

    public IReadOnlyList<AlertRule> CaptureRules()
    {
        if (!TryCaptureEditor()) throw new InvalidOperationException(_validation.Text);
        return _rules.Select(row => row.Rule).ToArray();
    }

    public void SetEvents(IEnumerable<MonitoringEvent> events)
    {
        var selectedId = (_events.SelectedItem as MonitoringEvent)?.Id;
        var recent = events.OrderByDescending(item => item.Timestamp).ToArray();
        _events.ItemsSource = recent;
        _events.SelectedItem = recent.FirstOrDefault(item => item.Id == selectedId);
        _emptyEvents.IsVisible = recent.Length == 0;
        _events.IsVisible = recent.Length > 0;
        _events.Height = Math.Min(152, 25 + 30 * recent.Length);
        UpdateAcknowledge();
    }

    private void AddRule()
    {
        if (!TryCaptureEditor()) return;
        if (_rules.Count >= 200) { ShowError("A workspace supports up to 200 alert rules."); return; }
        var row = new AlertRuleRow(new AlertRule { Name = "New alert", Threshold = 5 });
        _rules.Add(row); _table.SelectedItem = row;
        _table.ScrollIntoView(row, null);
        _name.Focus(); _name.SelectAll();
    }

    private void RemoveRule()
    {
        if (_selected is null) return;
        var index = _rules.IndexOf(_selected);
        _loading = true; _rules.Remove(_selected); _selected = null; _loading = false;
        _table.SelectedItem = _rules.Count > 0 ? _rules[Math.Min(index, _rules.Count - 1)] : null;
        _selected = _table.SelectedItem as AlertRuleRow;
        LoadEditor();
    }

    private void SelectRule()
    {
        if (_loading) return;
        if (!TryCaptureEditor())
        { _loading = true; _table.SelectedItem = _selected; _loading = false; return; }
        _selected = _table.SelectedItem as AlertRuleRow;
        LoadEditor();
    }

    private void LoadEditor()
    {
        _loading = true;
        _selected = _table.SelectedItem as AlertRuleRow;
        _editor.IsVisible = _selected is not null;
        _emptyEditor.IsVisible = _selected is null;
        _emptyEditor.Text = _rules.Count == 0 ? "Add a rule to configure its condition and timing." : "Select a rule to view or edit its configuration.";
        _emptyRules.IsVisible = _rules.Count == 0;
        _table.IsVisible = _rules.Count > 0;
        _table.Height = Math.Min(115, 25 + 30 * _rules.Count);
        _remove.IsEnabled = _selected is not null;
        _add.IsEnabled = _rules.Count < 200;
        _validation.IsVisible = false;
        if (_selected is { Rule: var rule })
        {
            _name.Text = rule.Name;
            _condition.SelectedIndex = (int)rule.Condition;
            _scope.SelectedIndex = (string.IsNullOrEmpty(rule.Target) ? 0 : 1) + (string.IsNullOrEmpty(rule.Group) ? 0 : 2);
            _target.Text = rule.Target; _group.Text = rule.Group;
            _threshold.Text = rule.Threshold.ToString("R", CultureInfo.CurrentCulture);
            _evaluation.Text = Seconds(rule.EvaluationPeriod); _cooldown.Text = Seconds(rule.Cooldown); _recovery.Text = Seconds(rule.RecoveryPeriod);
            UpdateCondition(); UpdateScope();
        }
        _loading = false;
    }

    private void UpdateScope()
    {
        _targetField.IsVisible = _scope.SelectedIndex is 1 or 3;
        _groupField.IsVisible = _scope.SelectedIndex is 2 or 3;
        Reflow(_scopeRow);
        _scopeRow.IsVisible = _targetField.IsVisible || _groupField.IsVisible;
        ToolTip.SetTip(_scope, _scope.SelectedIndex == 3 ? "The rule applies only when both the target and group match." : null);
    }

    private void UpdateCondition()
    {
        var condition = (AlertCondition)_condition.SelectedIndex;
        _thresholdField.IsVisible = condition is AlertCondition.Latency or AlertCondition.PacketLoss or AlertCondition.Jitter;
        _thresholdLabel.Text = condition switch { AlertCondition.PacketLoss => "Packet loss exceeds (%)", AlertCondition.Jitter => "Jitter exceeds (ms)", _ => "Latency exceeds (ms)" };
        AutomationProperties.SetName(_threshold, _thresholdLabel.Text);
        _evaluationField.IsVisible = _recoveryField.IsVisible = condition != AlertCondition.RouteChanged;
        Reflow(_conditionRow); Reflow(_timingRow);
        _timingRow.HorizontalAlignment = condition == AlertCondition.RouteChanged ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        _timingRow.Width = condition == AlertCondition.RouteChanged ? 200 : double.NaN;
        if (_thresholdField.IsVisible) _conditionRow.ColumnDefinitions[1].Width = new GridLength(176);
        _evaluationLabel.Text = condition switch
        {
            AlertCondition.Recovery => "Outage (seconds)",
            AlertCondition.Unreachable => "Unreachable for (seconds)",
            _ => "Window & duration (seconds)"
        };
        AutomationProperties.SetName(_evaluation, _evaluationLabel.Text);
        AutomationProperties.SetName(_evaluation, _evaluationLabel.Text);
        _conditionHint.Text = condition switch
        {
            AlertCondition.Latency => "Triggers when the average round-trip time of responding destination probes exceeds the threshold.",
            AlertCondition.PacketLoss => "Triggers when failed eligible destination probes exceed this percentage. Local probe errors are excluded; diagnostic loss does not prove forwarding loss.",
            AlertCondition.Jitter => "Triggers when the mean absolute latency change between consecutive responding destination probes exceeds the threshold. Failed probes break pairs.",
            AlertCondition.Unreachable => "Triggers when the destination remains unreachable.",
            AlertCondition.RouteChanged => "Triggers when a confirmed network route change is detected.",
            _ => "Triggers after an outage when the destination responds again."
        };
        _timingHint.Text = condition switch
        {
            AlertCondition.RouteChanged => "Cooldown is the minimum time between route-change alerts.",
            AlertCondition.Recovery => "Outage is the required unreachable duration; recovery is the healthy duration. Cooldown separates recovery alerts.",
            AlertCondition.Unreachable => "Evaluation is the unreachable duration. Recovery is the healthy duration; cooldown separates repeated alerts.",
            _ => "This sets both the measurement window and sustained breach duration. Recovery is the healthy duration; cooldown separates repeated alerts."
        };
        ToolTip.SetTip(_condition, _conditionHint.Text);
        ToolTip.SetTip(_timingRow, _timingHint.Text);
        ToolTip.SetTip(_evaluation, condition is AlertCondition.Latency or AlertCondition.PacketLoss or AlertCondition.Jitter
            ? "The rolling measurement window and the time the breach must persist before an alert."
            : condition == AlertCondition.Recovery ? "The unreachable duration required before a subsequent recovery can trigger."
            : "How long the destination must remain unreachable before an alert.");
        ToolTip.SetTip(_cooldown, "Minimum time between repeated alerts. Ordinary recovery events are not suppressed by cooldown.");
        ToolTip.SetTip(_recovery, "How long the destination must remain healthy before a recovery event.");
    }

    private bool TryCaptureEditor()
    {
        if (_loading || _selected is null) return true;
        try
        {
            var original = _selected.Rule;
            if (string.IsNullOrWhiteSpace(_name.Text)) throw new InvalidOperationException("Enter a rule name.");
            var condition = (AlertCondition)_condition.SelectedIndex;
            var threshold = original.Threshold;
            if (_thresholdField.IsVisible && (!double.TryParse(_threshold.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out threshold) || !double.IsFinite(threshold) || threshold < 0))
                throw new InvalidOperationException("Threshold must be a finite number of zero or greater.");
            var evaluation = condition == AlertCondition.RouteChanged ? original.EvaluationPeriod : ParseSeconds(_evaluation, "Evaluation");
            var cooldown = ParseSeconds(_cooldown, "Cooldown");
            var recovery = condition == AlertCondition.RouteChanged ? original.RecoveryPeriod : ParseSeconds(_recovery, "Recovery");
            if (_scope.SelectedIndex is 1 or 3 && string.IsNullOrWhiteSpace(_target.Text) && (string.IsNullOrEmpty(original.Target) || _target.Text != original.Target)) throw new InvalidOperationException("Enter the target this rule applies to.");
            if (_scope.SelectedIndex is 2 or 3 && string.IsNullOrWhiteSpace(_group.Text) && (string.IsNullOrEmpty(original.Group) || _group.Text != original.Group)) throw new InvalidOperationException("Enter the group this rule applies to.");
            // Preserve existing strings, dormant values, IDs and exact ticks when they are not edited.
            _selected.Rule = original with { Name = _name.Text!, Condition = condition,
                Threshold = threshold, EvaluationPeriod = evaluation, Cooldown = cooldown, RecoveryPeriod = recovery,
                Target = _scope.SelectedIndex is 1 or 3 ? _target.Text : string.IsNullOrEmpty(original.Target) ? original.Target : null,
                Group = _scope.SelectedIndex is 2 or 3 ? _group.Text : string.IsNullOrEmpty(original.Group) ? original.Group : null };
            _validation.IsVisible = false;
            return true;
        }
        catch (InvalidOperationException ex) { ShowError(ex.Message); return false; }
    }

    private static string Seconds(TimeSpan value) => ((decimal)value.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.CurrentCulture);

    private static TimeSpan ParseSeconds(TextBox field, string label)
    {
        if (!decimal.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) || seconds < 0 || seconds > (decimal)long.MaxValue / TimeSpan.TicksPerSecond)
            throw new InvalidOperationException($"{label} must be a non-negative number of seconds within the supported range.");
        var ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks)) throw new InvalidOperationException($"{label} supports up to seven decimal places in seconds.");
        return TimeSpan.FromTicks((long)ticks);
    }

    private void ShowError(string message) { _validation.Text = message; _validation.IsVisible = true; }

    private void UpdateAcknowledge() => _acknowledge.IsEnabled = !_acknowledging && _acknowledgeEvent is not null
        && _events.SelectedItem is MonitoringEvent { AcknowledgedAt: null };

    private async Task AcknowledgeAsync()
    {
        if (_acknowledging || _acknowledgeEvent is null || _events.SelectedItem is not MonitoringEvent { AcknowledgedAt: null } selected) return;
        _acknowledging = true; UpdateAcknowledge();
        try
        {
            if (await _acknowledgeEvent(selected))
            {
                var updated = selected with { AcknowledgedAt = DateTimeOffset.UtcNow, AcknowledgmentNote = "Acknowledged in Pngr" };
                SetEvents(_events.ItemsSource!.Cast<MonitoringEvent>().Select(item => item.Id == selected.Id ? updated : item));
                _eventStatus.Text = "Event acknowledged.";
            }
            else _eventStatus.Text = "This event could not be acknowledged.";
        }
        catch (Exception ex) { _eventStatus.Text = "Acknowledgment failed: " + ex.Message; }
        finally { _acknowledging = false; UpdateAcknowledge(); }
    }

    private static TextBox Input(string name, string label)
    {
        var field = new TextBox { Name = name, MinHeight = 29, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        AutomationProperties.SetName(field, label); return field;
    }
    private static ComboBox Choice(string name, string label, string[] values)
    {
        var field = new ComboBox { Name = name, ItemsSource = values, SelectedIndex = 0, MinHeight = 29, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        AutomationProperties.SetName(field, label); return field;
    }
    private static Button Action(string name, string text)
    {
        var button = new Button { Name = name, Content = text, MinHeight = 28, Padding = new Thickness(10, 3), FontSize = 11 };
        AutomationProperties.SetName(button, text); return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = Muted };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Cell(string? text)
    {
        var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        ToolTip.SetTip(cell, text); return cell;
    }
    private static StackPanel Field(string label, Control control) => Field(Label(label), control);
    private static StackPanel Field(TextBlock label, Control control) => new() { Spacing = 5, Children = { label, control } };
    private static Grid Columns(params Control[] controls)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", controls.Select(_ => "*"))) };
        for (var i = 0; i < controls.Length; i++)
        { controls[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, i == controls.Length - 1 ? 0 : 6, 0); Grid.SetColumn(controls[i], i); grid.Children.Add(controls[i]); }
        return grid;
    }
    private static void Reflow(Grid grid)
    {
        var visible = grid.Children.Where(control => control.IsVisible).ToArray();
        for (var i = 0; i < grid.Children.Count; i++)
            grid.ColumnDefinitions[i].Width = grid.Children[i].IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        for (var i = 0; i < visible.Length; i++)
            visible[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, i == visible.Length - 1 ? 0 : 6, 0);
        grid.InvalidateMeasure();
    }
    private static Control SectionHeader(string title, params Button[] actions)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var action in actions) buttons.Children.Add(action);
        Grid.SetColumn(buttons, 1); grid.Children.Add(buttons); return grid;
    }
    private static Border Divider() => new() { Height = 1, Background = Brush.Parse("#35383F"), Margin = new Thickness(0, 4, 0, 3) };
    private static DataGrid Table(string name, double height)
    {
        var table = new DataGrid { Name = name, Height = height, AutoGenerateColumns = false, IsReadOnly = true,
            RowHeight = 30, ColumnHeaderHeight = 25, SelectionMode = DataGridSelectionMode.Single,
            CanUserReorderColumns = false, CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brush.Parse("#202227") };
        AutomationProperties.SetName(table, name == "AlertRulesTable" ? "Alert rules" : "Recent events"); return table;
    }
    private static void AddColumn(DataGrid table, string header, string property, double star, double width = 0, string? format = null)
        => table.Columns.Add(new DataGridTextColumn { Header = header,
            Binding = new Binding(property) { Mode = BindingMode.OneWay, StringFormat = format },
            Width = star > 0 ? new DataGridLength(star, DataGridLengthUnitType.Star) : new DataGridLength(width) });
    private void AddRuleColumn(string header, string property, double star)
        => _table.Columns.Add(new DataGridTemplateColumn { Header = header, SortMemberPath = property,
            Width = new DataGridLength(star, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<AlertRuleRow>((row, _) =>
            {
                var cell = Cell(null);
                cell.Bind(TextBlock.TextProperty, new Binding(property) { Source = row });
                cell.Bind(ToolTip.TipProperty, new Binding(property) { Source = row });
                return cell;
            }) });
}

public sealed class AlertRuleRow(AlertRule rule) : INotifyPropertyChanged
{
    private AlertRule _rule = rule;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AlertRule Rule
    {
        get => _rule;
        set { if (_rule == value) return; _rule = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    }
    public string Name => Rule.Name;
    public bool Enabled { get => Rule.Enabled; set => Rule = Rule with { Enabled = value }; }
    public string Trigger => Rule.Condition switch
    {
        AlertCondition.Latency => $"Latency > {Number} ms",
        AlertCondition.PacketLoss => $"Packet loss > {Number}%",
        AlertCondition.Jitter => $"Jitter > {Number} ms",
        AlertCondition.Unreachable => "Unreachable",
        AlertCondition.RouteChanged => "Route changed",
        _ => "Recovery"
    };
    private string Number => Rule.Threshold.ToString("G", CultureInfo.CurrentCulture);
    public string AppliesTo => (Rule.Target, Rule.Group) switch
    {
        ({ Length: > 0 } target, { Length: > 0 } group) => $"{target} · {group}",
        ({ Length: > 0 } target, _) => target,
        (_, { Length: > 0 } group) => $"Group: {group}",
        _ => "All targets"
    };
}
