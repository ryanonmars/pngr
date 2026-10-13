using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TraceScope.App.Controls;

/// <summary>Compact latency range plot aligned with the hop table.</summary>
public sealed class HopLatencyPlot : Control
{
    private static readonly Typeface Font = new("Inter, Segoe UI, Helvetica Neue, Arial");
    private static readonly IBrush Background = Brush("#202226");
    private static readonly IBrush Header = Brush("#303236");
    private static readonly IBrush Ink = Brush("#BBC1C8");
    private static readonly Pen Range = new(Brush("#A8B8A0"), 1);
    private static readonly Pen Route = new(Brush("#BDC5B1"), 1);
    private static readonly Pen Marker = new(Brush("#E27768"), 1.2);
    private IReadOnlyList<HopPlotRow> _rows = Array.Empty<HopPlotRow>();
    private int? _selectedHop;
    private double _rowHeight = 22;
    private double _headerHeight = 24;
    private double _verticalOffset;

    public HopLatencyPlot() { ClipToBounds = true; MinWidth = 180; }
    public IReadOnlyList<HopPlotRow> Rows
    {
        get => _rows;
        set { _rows = value ?? Array.Empty<HopPlotRow>(); InvalidateMeasure(); InvalidateVisual(); }
    }
    public int? SelectedHop { get => _selectedHop; set { _selectedHop = value; InvalidateVisual(); } }
    public double RowHeight { get => _rowHeight; set { _rowHeight = Math.Max(12, value); InvalidateMeasure(); InvalidateVisual(); } }
    public double HeaderHeight { get => _headerHeight; set { _headerHeight = Math.Max(0, value); InvalidateMeasure(); InvalidateVisual(); } }
    /// <summary>Table scroll offset; the axis remains fixed above the rows.</summary>
    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            var offset = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (Math.Abs(_verticalOffset - offset) < 0.1) return;
            _verticalOffset = offset;
            InvalidateVisual();
        }
    }
    public Action<int>? HopSelected { get; set; }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width,
        HeaderHeight + Rows.Count * RowHeight);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 30) return;
        context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        const double left = 6;
        var right = Math.Max(left + 1, Bounds.Width - 8);
        var highest = Rows.SelectMany(r => new[] { r.Maximum, r.Average, r.Current }).Where(v => v is >= 0 && double.IsFinite(v.Value)).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var ceiling = Math.Max(50, Math.Ceiling(highest * 1.08 / 10) * 10);
        double X(double value) => left + Math.Clamp(value / ceiling, 0, 1) * (right - left);
        var rowsBounds = new Rect(0, HeaderHeight, Bounds.Width, Math.Max(0, Bounds.Height - HeaderHeight));
        using (context.PushClip(rowsBounds))
        {
            Band(0, 100, "#3B5E36");
            Band(100, 200, "#685C32");
            Band(200, ceiling, "#653B37");
            for (var i = 1; i < 4; i++)
            {
                var x = left + (right - left) * i / 4;
                context.DrawLine(new Pen(Brush("#0CFFFFFF"), 1), new Point(x, HeaderHeight), new Point(x, Bounds.Height));
            }
            Point? previous = null;
            for (var index = 0; index < Rows.Count; index++)
            {
                var row = Rows[index];
                var top = HeaderHeight + index * RowHeight - VerticalOffset;
                var y = top + RowHeight / 2;
                var current = row.Average ?? row.Current;
                var point = current is >= 0 && double.IsFinite(current.Value) ? new Point(X(current.Value), y) : (Point?)null;
                if (top + RowHeight < HeaderHeight) { previous = point; continue; }
                if (top >= Bounds.Height) break;
                if (row.Hop == SelectedHop)
                    context.DrawRectangle(Brush("#22FFFFFF"), null, new Rect(0, top, Bounds.Width, RowHeight));
                if (row.Minimum is double minimum && row.Maximum is double maximum && double.IsFinite(minimum) && double.IsFinite(maximum))
                {
                    context.DrawLine(Range, new Point(X(minimum), y), new Point(X(maximum), y));
                    context.DrawLine(Range, new Point(X(minimum), y - 3), new Point(X(minimum), y + 3));
                    context.DrawLine(Range, new Point(X(maximum), y - 3), new Point(X(maximum), y + 3));
                }
                if (point is { } p)
                {
                    if (previous is { } before) context.DrawLine(Route, before, p);
                    context.DrawEllipse(Brush("#3B5E36"), Marker, p, 2.4, 2.4);
                }
                else if (row.LossPercent > 0)
                {
                    context.DrawLine(Marker, new Point(right - 5, y - 3), new Point(right, y + 3));
                    context.DrawLine(Marker, new Point(right - 5, y + 3), new Point(right, y - 3));
                }
                previous = point;
            }
        }
        context.DrawRectangle(Header, null, new Rect(0, 0, Bounds.Width, HeaderHeight));
        if (HeaderHeight > 0)
        {
            Label("0 ms", left, false);
            var center = new FormattedText("Latency", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, Ink);
            context.DrawText(center, new Point((Bounds.Width - center.Width) / 2, Math.Max(0, (HeaderHeight - center.Height) / 2)));
            Label($"{ceiling:0} ms", right, true);
        }
        void Band(double from, double to, string color)
        {
            if (from >= ceiling || to <= from) return;
            var x1 = from == 0 ? 0 : X(from);
            var x2 = to >= ceiling ? Bounds.Width : X(to);
            context.DrawRectangle(Brush(color), null, new Rect(x1, HeaderHeight, Math.Max(0, x2 - x1), rowsBounds.Height));
        }
        void Label(string text, double x, bool alignRight)
        {
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, Ink);
            context.DrawText(label, new Point(alignRight ? x - label.Width : x, Math.Max(0, (HeaderHeight - label.Height) / 2)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        if (position.Y < HeaderHeight) return;
        var index = (int)Math.Floor((position.Y - HeaderHeight + VerticalOffset) / RowHeight);
        if (index < 0 || index >= Rows.Count) return;
        SelectedHop = Rows[index].Hop;
        HopSelected?.Invoke(SelectedHop.Value);
        e.Handled = true;
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
