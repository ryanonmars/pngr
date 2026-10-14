using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.App.Controls;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed class SummaryWindow : Window
{
    private readonly Func<IReadOnlyList<TargetItem>> _targets;
    private readonly Action<TargetItem> _activate;
    private readonly Action<IReadOnlyList<SummaryDefinition>>? _definitionsChanged;
    private readonly List<SummaryDefinition> _definitions;
    private readonly DataGrid _table;
    private readonly ComboBox _group;
    private readonly ComboBox _window;
    private readonly ComboBox _summary;
    private readonly ComboBox _layout;
    private readonly CheckBox _problems;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _timer;
    private readonly ScrollViewer _tiles;
    private readonly Border _combined;
    private bool _refreshing;
    private string? _summaryId;

    public SummaryWindow(Func<IReadOnlyList<TargetItem>> targets, Action<TargetItem> activate,
        IReadOnlyList<SummaryDefinition>? savedDefinitions = null,
        Action<IReadOnlyList<SummaryDefinition>>? definitionsChanged = null)
    {
        _targets = targets;
        _activate = activate;
        _definitionsChanged = definitionsChanged;
        _definitions = (savedDefinitions ?? []).ToList();
        foreach (var definition in _definitions) definition.Validate();
        if (_definitions.Count > 100 || _definitions.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _definitions.Count)
            throw new InvalidDataException("Invalid summary collection.");
        Title = "Pngr · Professional Summaries";
        Width = 1280; Height = 560; MinWidth = 800; MinHeight = 350;
        Background = Brush.Parse("#191A1D");
        Foreground = Brush.Parse("#D5D8DE");
        _summary = new ComboBox { Name = "SummaryViews", Width = 190 };
        _layout = new ComboBox { Name = "SummaryLayout", Width = 95, ItemsSource = Enum.GetValues<SummaryLayout>(), SelectedIndex = 0 };
        _group = new ComboBox { Name = "SummaryGroup", Width = 125, ItemsSource = new[] { "All groups" }, SelectedIndex = 0 };
        _window = new ComboBox { Name = "SummaryFocus", Width = 100, ItemsSource = new[] { "1 minute", "5 minutes", "15 minutes", "1 hour", "6 hours", "24 hours" }, SelectedIndex = 1 };
        _problems = new CheckBox { Name = "SummaryProblems", Content = "Problems only", VerticalAlignment = VerticalAlignment.Center };
        _status = new TextBlock { Foreground = Brush.Parse("#A8ADB7"), Margin = new Thickness(10, 7), TextWrapping = TextWrapping.Wrap };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6) };
        AddToolbar(toolbar, _summary);
        AddToolbar(toolbar, MakeButton("New", async () => await EditSummaryAsync(true), "SummaryNew"));
        AddToolbar(toolbar, MakeButton("Edit / targets", async () => await EditSummaryAsync(false), "SummaryEdit"));
        AddToolbar(toolbar, MakeButton("Delete", () => { if (_summaryId is { } id) RemoveDefinition(id); }, "SummaryDelete"));
        AddToolbar(toolbar, _layout);
        AddToolbar(toolbar, _group);
        AddToolbar(toolbar, new TextBlock { Text = "Focus", VerticalAlignment = VerticalAlignment.Center });
        AddToolbar(toolbar, _window);
        AddToolbar(toolbar, _problems);
        _table = new DataGrid { Name = "SummaryTable", AutoGenerateColumns = false, IsReadOnly = true,
            RowHeight = 23, ColumnHeaderHeight = 26, SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.None, Background = Brush.Parse("#1E2024") };
        _table.Columns.Add(new DataGridTemplateColumn { Header = "Health", Width = new DataGridLength(110),
            CellTemplate = new FuncDataTemplate<SummaryRow>((row, _) => new TextBlock
            { Text = row?.Health, Foreground = row?.HealthBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) }) });
        AddColumn("Target", nameof(SummaryRow.Target), 145);
        _table.Columns.Add(new DataGridTemplateColumn { Header = "Latency + loss", Width = new DataGridLength(180),
            CellTemplate = new FuncDataTemplate<SummaryRow>((row, _) => row is null ? new Border() : CreateTrend(row, false)) });
        AddColumn("Group", nameof(SummaryRow.Group), 85);
        AddColumn("Protocol", nameof(SummaryRow.Protocol), 60);
        AddColumn("State", nameof(SummaryRow.State), 75);
        AddColumn("Current", nameof(SummaryRow.Current), 60);
        AddColumn("Avg 30s", nameof(SummaryRow.Average30s), 60);
        AddColumn("Avg 5m", nameof(SummaryRow.Average5m), 60);
        AddColumn("Focus avg", nameof(SummaryRow.FocusAverage), 68);
        AddColumn("Loss", nameof(SummaryRow.Loss), 55);
        AddColumn("Jitter", nameof(SummaryRow.Jitter), 55);
        AddColumn("MOS est.", nameof(SummaryRow.Mos), 60);
        AddColumn("Samples", nameof(SummaryRow.Samples), 60);
        AddColumn("Route", nameof(SummaryRow.Route), 80);
        _tiles = new ScrollViewer { Name = "SummaryTiles", IsVisible = false };
        _combined = new Border { Name = "SummaryCombined", IsVisible = false, Height = 170, Padding = new Thickness(8, 2) };
        var open = new Button { Content = "Show selected trace", Margin = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => ActivateSelected();
        _table.DoubleTapped += (_, _) => ActivateSelected();
        var footer = new StackPanel();
        footer.Children.Add(open);
        footer.Children.Add(_status);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(_combined);
        var views = new Grid(); views.Children.Add(_table); views.Children.Add(_tiles);
        Grid.SetRow(views, 1); body.Children.Add(views);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(toolbar);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        _summary.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _summaryId = (_summary.SelectedItem as ViewChoice)?.Id;
            var definition = SelectedDefinition;
            _refreshing = true;
            _layout.SelectedItem = definition?.Layout ?? SummaryLayout.Table;
            SetFocusControl(definition?.FocusMinutes ?? 5);
            _refreshing = false;
            RefreshRows();
        };
        _layout.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (SelectedDefinition is { } definition) SetDefinition(definition with { Layout = (SummaryLayout)_layout.SelectedItem! });
            else RefreshRows();
        };
        _group.SelectionChanged += (_, _) => { if (!_refreshing) RefreshRows(); };
        _window.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (SelectedDefinition is { } definition) SetDefinition(definition with { FocusMinutes = FocusMinutes });
            else RefreshRows();
        };
        _problems.IsCheckedChanged += (_, _) => RefreshRows();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshRows());
        Opened += (_, _) => { RefreshRows(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        ReloadViews();
        RefreshRows();
    }

    public IReadOnlyList<SummaryDefinition> Definitions => _definitions.ToArray();
    public SummaryDefinition? SelectedDefinition => _definitions.FirstOrDefault(d => d.Id == _summaryId);
    private int FocusMinutes => _window.SelectedIndex switch { 0 => 1, 2 => 15, 3 => 60, 4 => 360, 5 => 1440, 6 => SelectedDefinition?.FocusMinutes ?? 5, _ => 5 };
    private static int FocusIndex(int minutes) => minutes switch { 1 => 0, 5 => 1, 15 => 2, 60 => 3, 360 => 4, 1440 => 5, _ => 6 };
    private void SetFocusControl(int minutes)
    {
        string[] choices = ["1 minute", "5 minutes", "15 minutes", "1 hour", "6 hours", "24 hours"];
        _window.ItemsSource = FocusIndex(minutes) == 6 ? choices.Append($"{minutes} minutes").ToArray() : choices;
        _window.SelectedIndex = FocusIndex(minutes);
    }

    public void SelectSummary(string? id)
    {
        if (id is not null && _definitions.All(d => d.Id != id)) throw new ArgumentException("Unknown summary.", nameof(id));
        _summary.SelectedItem = _summary.ItemsSource!.Cast<ViewChoice>().First(c => c.Id == id);
    }

    public void SetDefinition(SummaryDefinition definition)
    {
        definition.Validate();
        var index = _definitions.FindIndex(d => d.Id == definition.Id);
        if (index >= 0) _definitions[index] = definition;
        else
        {
            if (_definitions.Count >= 100) throw new InvalidDataException("At most 100 summaries are supported.");
            _definitions.Add(definition);
        }
        _summaryId = definition.Id;
        ReloadViews();
        _definitionsChanged?.Invoke(Definitions);
        RefreshRows();
    }

    public void RemoveDefinition(string id)
    {
        if (_definitions.RemoveAll(d => d.Id == id) == 0) return;
        if (_summaryId == id) _summaryId = null;
        ReloadViews();
        _definitionsChanged?.Invoke(Definitions);
        RefreshRows();
    }

    private void ReloadViews()
    {
        _refreshing = true;
        _summary.ItemsSource = new[] { new ViewChoice(null, "All targets") }.Concat(_definitions.Select(d => new ViewChoice(d.Id, d.Name))).ToArray();
        _summary.SelectedItem = _summary.ItemsSource.Cast<ViewChoice>().First(c => c.Id == _summaryId);
        _layout.SelectedItem = SelectedDefinition?.Layout ?? SummaryLayout.Table;
        SetFocusControl(SelectedDefinition?.FocusMinutes ?? 5);
        _refreshing = false;
    }

    private async Task EditSummaryAsync(bool create)
    {
        var existing = create ? null : SelectedDefinition;
        if (!create && existing is null) { _status.Text = "All targets is automatically maintained. Use New to create a configurable summary."; return; }
        var name = new TextBox { Name = "SummaryEditName", Text = existing?.Name ?? "New summary", MaxLength = 80 };
        var checks = _targets().Select(t => t.Target).Concat(existing?.Targets ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(target => new CheckBox { Content = target, Tag = target, IsChecked = existing?.Includes(target) ?? true, Margin = new Thickness(0, 2) }).ToArray();
        var targetPanel = new StackPanel();
        foreach (var check in checks) targetPanel.Children.Add(check);
        var dialog = new Window { Title = create ? "Create summary" : "Edit summary targets", Width = 460, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var panel = new Grid { Margin = new Thickness(15), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        panel.Children.Add(new TextBlock { Text = "Summary name", Margin = new Thickness(0, 0, 0, 5) });
        Grid.SetRow(name, 1); panel.Children.Add(name);
        var scroller = new ScrollViewer { Content = targetPanel, Margin = new Thickness(0, 10) }; Grid.SetRow(scroller, 2); panel.Children.Add(scroller);
        var warning = new TextBlock { Foreground = Brush.Parse("#E6AA77") }; Grid.SetRow(warning, 3); panel.Children.Add(warning);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var save = new Button { Content = "Save summary", Name = "SummarySave" };
        save.Click += (_, _) =>
        {
            try
            {
                SetDefinition((existing ?? new SummaryDefinition()) with { Name = name.Text?.Trim() ?? "", Targets = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToArray() });
                dialog.Close();
            }
            catch (InvalidDataException ex) { warning.Text = ex.Message; }
        };
        actions.Children.Add(save);
        actions.Children.Add(MakeButton("Cancel", () => dialog.Close()));
        Grid.SetRow(actions, 4); panel.Children.Add(actions); dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private static void AddToolbar(Panel toolbar, Control item) { item.Margin = new Thickness(0, 2, 8, 2); toolbar.Children.Add(item); }
    private static Button MakeButton(string text, Action action, string? name = null)
    { var button = new Button { Content = text, Name = name }; button.Click += (_, _) => action(); return button; }
    private void AddColumn(string title, string property, int width) => _table.Columns.Add(new DataGridTextColumn
    { Header = title, Binding = new Binding(property), Width = new DataGridLength(width) });
    private void ActivateSelected() { if (_table.SelectedItem is SummaryRow row) _activate(row.Item); }

    public void RefreshRows()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var items = _targets();
            var groups = new[] { "All groups" }.Concat(items.Select(i => i.Group).Distinct().Order()).ToArray();
            var previousGroup = _group.SelectedItem as string ?? "All groups";
            if (!(_group.ItemsSource?.Cast<string>().SequenceEqual(groups) ?? false))
            { _group.ItemsSource = groups; _group.SelectedItem = groups.Contains(previousGroup) ? previousGroup : "All groups"; }
            var selected = (_table.SelectedItem as SummaryRow)?.Item.Id;
            var focus = TimeSpan.FromMinutes(FocusMinutes);
            var now = DateTimeOffset.UtcNow;
            var definition = SelectedDefinition;
            var rows = items.Where(i => definition is null || definition.Includes(i.Target))
                .Where(i => _group.SelectedItem as string == "All groups" || i.Group == _group.SelectedItem as string)
                .Select(i => new SummaryRow(i, focus, now)).Where(r => _problems.IsChecked != true || r.IsProblem).ToArray();
            _table.ItemsSource = rows;
            if (selected is { } id) _table.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == id);
            var mode = _layout.SelectedItem is SummaryLayout value ? value : SummaryLayout.Table;
            _table.IsVisible = mode != SummaryLayout.Tiles;
            _tiles.IsVisible = mode == SummaryLayout.Tiles;
            _combined.IsVisible = mode == SummaryLayout.Graphs;
            if (_tiles.IsVisible) BuildTiles(rows);
            else _tiles.Content = null;
            if (_combined.IsVisible) _combined.Child = new SummaryTrend { Name = "SummaryOverlay", ShowAxes = true, End = now, Horizon = focus,
                Series = rows.Select(r => new SummaryGraphSeries(r.Target, r.Graph, r.Item.Session?.Options.Interval.TotalSeconds ?? 2.5)).ToArray() };
            else _combined.Child = null;
            var unavailable = definition?.Targets.Count(t => items.All(i => !i.Target.Equals(t, StringComparison.OrdinalIgnoreCase))) ?? 0;
            _status.Text = $"{rows.Length} shown · {rows.Count(r => r.IsProblem)} with problems · {now.ToLocalTime():HH:mm:ss} · Same time range for every target. Lines: latency (ms); red bars: loss (%). MOS is an estimate."
                + (unavailable > 0 ? $" {unavailable} saved targets are currently closed." : "");
        }
        finally { _refreshing = false; }
    }

    private static SummaryTrend CreateTrend(SummaryRow row, bool axes) => new()
    { ShowAxes = axes, End = row.End, Horizon = row.Focus, Series = [new(row.Target, row.Graph, row.Item.Session?.Options.Interval.TotalSeconds ?? 2.5)] };

    private void BuildTiles(SummaryRow[] rows)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var row in rows)
        {
            var content = new StackPanel { Spacing = 4 };
            var open = new Button { Content = row.Target, HorizontalAlignment = HorizontalAlignment.Stretch };
            open.Click += (_, _) => _activate(row.Item);
            content.Children.Add(open);
            content.Children.Add(new TextBlock { Text = $"{row.Health} · {row.Group}", Foreground = row.HealthBrush });
            content.Children.Add(new TextBlock { Text = $"Avg {row.FocusAverage} ms · Loss {row.Loss} · Jitter {row.Jitter} ms", FontSize = 11 });
            var graph = CreateTrend(row, true); graph.Height = 115; content.Children.Add(graph);
            panel.Children.Add(new Border { Width = 310, Padding = new Thickness(8), Margin = new Thickness(4), BorderBrush = row.HealthBrush, BorderThickness = new Thickness(1), Child = content });
        }
        _tiles.Content = panel;
    }

    private sealed record ViewChoice(string? Id, string Name) { public override string ToString() => Name; }
}

public sealed class SummaryRow
{
    private readonly IReadOnlyList<LatencySample> _history;
    private IReadOnlyList<SummaryBucket>? _graph;
    public SummaryRow(TargetItem item, TimeSpan focus, DateTimeOffset now)
    {
        Item = item;
        var snapshot = item.Session?.LatestSnapshot ?? item.Snapshot;
        var destination = snapshot?.Destination;
        var history = destination?.History ?? [];
        End = now;
        Focus = focus;
        _history = history;
        var thirtySeconds = MonitoringStatistics.Calculate(history, TimeSpan.FromSeconds(30), now);
        var fiveMinutes = MonitoringStatistics.Calculate(history, TimeSpan.FromMinutes(5), now);
        var focused = MonitoringStatistics.Calculate(history, focus, now);
        Current = Number(destination?.LastMs);
        Average30s = Number(thirtySeconds.AverageMs);
        Average5m = Number(fiveMinutes.AverageMs);
        FocusAverage = Number(focused.AverageMs);
        Loss = focused.Sent > 0 ? focused.LossPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        Jitter = Number(focused.JitterMs);
        Mos = focused.Mos?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—";
        Samples = focused.Sent;
        Route = snapshot?.RouteChanged == true ? "Changed" : "—";
        var unreachable = destination?.Status is ProbeStatus.Timeout or ProbeStatus.Unreachable;
        var error = item.Session?.State == SessionState.Error || destination?.Status == ProbeStatus.Error;
        IsProblem = error || unreachable || focused.LossPercent >= 5 || focused.AverageMs >= 150 || focused.JitterMs >= 30;
        Health = error ? "● Error" : unreachable ? "● Unreachable" : snapshot is null ? "○ Waiting"
            : IsProblem ? "● Degraded" : "● Healthy";
        HealthBrush = Brush.Parse(error || unreachable ? "#E47777" : IsProblem ? "#DAB66A" : snapshot is null ? "#A8ADB7" : "#59C875");
    }
    public TargetItem Item { get; }
    public DateTimeOffset End { get; }
    public TimeSpan Focus { get; }
    public IReadOnlyList<SummaryBucket> Graph => _graph ??= SummaryMeasurements.Aggregate(_history, End, Focus);
    public string Target => Item.Target;
    public string Group => Item.Group;
    public string Protocol => Item.Session?.Options.Protocol.ToString().ToUpperInvariant() ?? "History";
    public string State => Item.StateLabel;
    public string Health { get; }
    public IBrush HealthBrush { get; }
    public bool IsProblem { get; }
    public string Current { get; }
    public string Average30s { get; }
    public string Average5m { get; }
    public string FocusAverage { get; }
    public string Loss { get; }
    public string Jitter { get; }
    public string Mos { get; }
    public long Samples { get; }
    public string Route { get; }
    private static string Number(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
}
