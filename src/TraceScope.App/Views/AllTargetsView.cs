using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App.Views;

/// <summary>The built-in destination overview; membership follows the open target tabs.</summary>
public sealed class AllTargetsView : UserControl
{
    private readonly ObservableCollection<AllTargetRow> _rows = [];
    private readonly DataGrid _table;
    private readonly TextBlock _count;
    private readonly TextBlock _focus;
    private readonly TextBlock _maximum;
    private readonly TextBlock _empty;
    private bool _updating;

    public Action<TargetItem>? TargetSelected { get; set; }
    public Action<TargetItem>? TargetOpened { get; set; }
    public DataGrid Table => _table;
    public IReadOnlyList<AllTargetRow> Rows => _rows;

    public AllTargetsView()
    {
        _table = new DataGrid { Name = "AllTargetsTable", ItemsSource = _rows,
            IsReadOnly = true, AutoGenerateColumns = false, CanUserSortColumns = true,
            CanUserResizeColumns = true, SelectionMode = DataGridSelectionMode.Single,
            ColumnHeaderHeight = 26, RowHeight = 23, Background = Brush.Parse("#232326"),
            GridLinesVisibility = DataGridGridLinesVisibility.None };
        foreach (var state in new[] { "Selected", "SelectedHovered", "SelectedUnfocused", "SelectedHoveredUnfocused" })
        {
            _table.Resources[$"DataGridRow{state}BackgroundBrush"] = Brush.Parse("#2949A5");
            _table.Resources[$"DataGridRow{state}BackgroundOpacity"] = 1d;
        }
        _table.Columns.Add(new DataGridTemplateColumn { Header = "Status", CanUserSort = false, Width = new DataGridLength(60),
            CellTemplate = new FuncDataTemplate<AllTargetRow>((row, _) =>
            {
                var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0) };
                dot.Bind(Border.BackgroundProperty, new Binding(nameof(AllTargetRow.StatusBrush)));
                dot.DataContext = row;
                ToolTip.SetTip(dot, row?.Item.StateLabel);
                return dot;
            }) });
        AddColumn("Count", nameof(AllTargetRow.Count), 52);
        _table.Columns.Add(new DataGridTemplateColumn { Header = "IP", SortMemberPath = nameof(AllTargetRow.Address), Width = new DataGridLength(170),
            CellTemplate = new FuncDataTemplate<AllTargetRow>((row, _) =>
            {
                var ip = new TextBlock { Foreground = Brush.Parse("#52A0FF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
                ip.Bind(TextBlock.TextProperty, new Binding(nameof(AllTargetRow.Address)));
                ip.DataContext = row;
                return ip;
            }) });
        AddColumn("Name", nameof(AllTargetRow.Name), 0);
        AddColumn("Setting", nameof(AllTargetRow.Setting), 150);
        AddColumn("Avg", nameof(AllTargetRow.AverageLabel), 48);
        AddColumn("Min", nameof(AllTargetRow.MinimumLabel), 48);
        AddColumn("Cur", nameof(AllTargetRow.CurrentLabel), 48);
        AddColumn("PL%", nameof(AllTargetRow.LossLabel), 46);
        var axis = new Grid { Width = 210, ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        axis.Children.Add(new TextBlock { Text = "0 ms", Foreground = Brush.Parse("#A6BBA0"), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = "Latency", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); axis.Children.Add(label);
        _maximum = new TextBlock { Text = "— ms", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#A6BBA0") };
        Grid.SetColumn(_maximum, 2); axis.Children.Add(_maximum);
        _table.Columns.Add(new DataGridTemplateColumn { Header = axis, CanUserSort = false, Width = new DataGridLength(220),
            CellTemplate = new FuncDataTemplate<AllTargetRow>((row, _) => new TargetLatencyRange { DataContext = row }) });
        _count = new TextBlock { Text = "0 targets", FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#A4A9B3") };
        _focus = new TextBlock { Foreground = Brush.Parse("#A4A9B3"), HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(112, 2, 7, 2) };
        footer.Children.Add(_count); Grid.SetColumn(_focus, 1); footer.Children.Add(_focus);
        _empty = new TextBlock { Text = "Enter a hostname or IP address above to start monitoring targets.",
            Foreground = Brush.Parse("#969CA8"), Margin = new Thickness(15, 45, 15, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var body = new Grid(); body.Children.Add(_table); body.Children.Add(_empty);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,22"), Background = Brush.Parse("#191A1D") };
        layout.Children.Add(body); Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        _table.SelectionChanged += (_, _) => { if (!_updating && _table.SelectedItem is AllTargetRow row) TargetSelected?.Invoke(row.Item); };
        _table.DoubleTapped += (_, _) => { if (_table.SelectedItem is AllTargetRow row) TargetOpened?.Invoke(row.Item); };
        _table.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && _table.SelectedItem is AllTargetRow row)
            { TargetOpened?.Invoke(row.Item); e.Handled = true; }
        };
    }

    private void AddColumn(string header, string property, int width) => _table.Columns.Add(new DataGridTextColumn
    {
        Header = header, Binding = new Binding(property),
        SortMemberPath = property switch
        {
            nameof(AllTargetRow.AverageLabel) => nameof(AllTargetRow.Average),
            nameof(AllTargetRow.MinimumLabel) => nameof(AllTargetRow.Minimum),
            nameof(AllTargetRow.CurrentLabel) => nameof(AllTargetRow.Current),
            nameof(AllTargetRow.LossLabel) => nameof(AllTargetRow.Loss),
            _ => property
        },
        Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
        MinWidth = width == 0 ? 110 : 25
    });

    public void Refresh(IEnumerable<TargetItem> targets, Guid? selected, DateTimeOffset end, TimeSpan horizon,
        IReadOnlyList<MonitoringProfile> profiles, DisplayConfiguration display)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var items = targets.ToArray();
            var ids = items.Select(i => i.Id).ToHashSet();
            for (var i = _rows.Count - 1; i >= 0; i--) if (!ids.Contains(_rows[i].Item.Id)) _rows.RemoveAt(i);
            foreach (var item in items)
            {
                var row = _rows.FirstOrDefault(r => r.Item.Id == item.Id);
                if (row is null) { row = new AllTargetRow(item); _rows.Add(row); }
                row.Update(end, horizon, profiles);
            }
            var high = _rows.Select(r => r.Maximum ?? 0).DefaultIfEmpty(0).Max();
            var ceiling = display.FixedMaximumMs > 0 ? display.FixedMaximumMs : Math.Max(1, Math.Ceiling(high * 1.08));
            foreach (var row in _rows) row.SetScale(ceiling, display.WarningLatencyMs, display.CriticalLatencyMs);
            _maximum.Text = $"{ceiling:0.#} ms";
            _table.SelectedItem = _rows.FirstOrDefault(r => r.Item.Id == selected);
            _count.Text = $"{_rows.Count} {(_rows.Count == 1 ? "target" : "targets")}";
            var startLabel = (end - horizon).ToLocalTime().ToString("h:mm:sst", CultureInfo.InvariantCulture).ToLowerInvariant();
            var endLabel = end.ToLocalTime().ToString("h:mm:sst", CultureInfo.InvariantCulture).ToLowerInvariant();
            _focus.Text = $"Focus: {startLabel}–{endLabel}";
            _empty.IsVisible = _rows.Count == 0;
        }
        finally { _updating = false; }
    }
}

public sealed class AllTargetRow(TargetItem item) : INotifyPropertyChanged
{
    public TargetItem Item { get; } = item;
    public string Address => Item.Snapshot?.DestinationAddress ?? Item.Address;
    public string Name => Item.Snapshot?.Destination.Hostname ?? Item.Target;
    public string Setting { get; private set; } = "Default Settings";
    public IBrush StatusBrush => Item.StatusBrush;
    public long Count { get; private set; }
    public double? Average { get; private set; }
    public double? Minimum { get; private set; }
    public double? Maximum { get; private set; }
    public double? Current { get; private set; }
    public double? Loss { get; private set; }
    public double Ceiling { get; private set; }
    public double Warning { get; private set; }
    public double Critical { get; private set; }
    public string AverageLabel => MainWindow.Number(Average);
    public string MinimumLabel => MainWindow.Number(Minimum);
    public string CurrentLabel => MainWindow.Number(Current);
    public string LossLabel => Loss is { } loss ? loss.ToString("0.#", CultureInfo.InvariantCulture) : "—";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DateTimeOffset end, TimeSpan horizon, IReadOnlyList<MonitoringProfile> profiles)
    {
        var samples = (Item.Snapshot?.Destination.History ?? []).Where(s => s.Timestamp >= end - horizon && s.Timestamp <= end)
            .OrderBy(s => s.Timestamp).ToArray();
        var sent = samples.Where(s => s.Status != ProbeStatus.Error).ToArray();
        var replies = sent.Where(s => s.Status is ProbeStatus.Success or ProbeStatus.TimeExceeded && s.LatencyMs is >= 0 && double.IsFinite(s.LatencyMs.Value))
            .Select(s => s.LatencyMs!.Value).ToArray();
        Count = sent.LongLength;
        Average = replies.Length > 0 ? replies.Average() : null;
        Minimum = replies.Length > 0 ? replies.Min() : null;
        Maximum = replies.Length > 0 ? replies.Max() : null;
        Current = samples.LastOrDefault() is { Status: ProbeStatus.Success or ProbeStatus.TimeExceeded, LatencyMs: >= 0 } latest && double.IsFinite(latest.LatencyMs!.Value) ? latest.LatencyMs : null;
        Loss = Count > 0 ? 100d * (Count - replies.LongLength) / Count : null;
        var options = Item.Session?.Options;
        Setting = Item.IsHistorical ? "Saved session" : profiles.FirstOrDefault(p => p.Options == options)?.Name ?? "Default Settings";
    }

    public void SetScale(double ceiling, double warning, double critical)
    {
        Ceiling = ceiling; Warning = warning; Critical = critical;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

internal sealed class TargetLatencyRange : Control
{
    public TargetLatencyRange()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (_row is not null) _row.PropertyChanged -= Refresh;
            _row = DataContext as AllTargetRow;
            if (_row is not null) _row.PropertyChanged += Refresh;
            InvalidateVisual();
        };
        DetachedFromVisualTree += (_, _) => { if (_row is not null) _row.PropertyChanged -= Refresh; };
        AttachedToVisualTree += (_, _) => { if (_row is not null) { _row.PropertyChanged -= Refresh; _row.PropertyChanged += Refresh; } };
    }
    private AllTargetRow? _row;
    private void Refresh(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_row is not { } row || Bounds.Width <= 12 || Bounds.Height <= 0) return;
        double X(double value) => 6 + Math.Clamp(value / row.Ceiling, 0, 1) * (Bounds.Width - 12);
        context.DrawRectangle(Brush.Parse("#3C6035"), null, new Rect(Bounds.Size));
        foreach (var (from, color) in new[] { (row.Warning, "#95803F"), (row.Critical, "#985353") })
            if (from < row.Ceiling) context.DrawRectangle(Brush.Parse(color), null, new Rect(X(from), 0, Bounds.Width - X(from), Bounds.Height));
        var y = Bounds.Height / 2;
        var whisker = new Pen(Brush.Parse("#A6B994"), 1);
        if (row.Minimum is { } min && row.Maximum is { } max)
        {
            context.DrawLine(whisker, new Point(X(min), y), new Point(X(max), y));
            context.DrawLine(whisker, new Point(X(min), y - 3), new Point(X(min), y + 3));
            context.DrawLine(whisker, new Point(X(max), y - 3), new Point(X(max), y + 3));
        }
        var red = new Pen(Brush.Parse("#F57161"), 1);
        if (row.Average is { } average)
        {
            var x = X(average);
            context.DrawLine(red, new Point(x, y - 8), new Point(x, y + 8));
            context.DrawEllipse(Brush.Parse("#3C6035"), red, new Point(x, y), 3, 3);
        }
        if (row.Current is { } current)
        {
            var cyan = new Pen(Brush.Parse("#46B9DD"), 1);
            var x = X(current);
            context.DrawLine(cyan, new Point(x - 3, y - 3), new Point(x + 3, y + 3));
            context.DrawLine(cyan, new Point(x - 3, y + 3), new Point(x + 3, y - 3));
        }
    }
}
