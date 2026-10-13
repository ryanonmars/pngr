using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TraceScope.App.Controls;

/// <summary>Measured latency, consecutive-response jitter, and packet loss on a shared time axis.</summary>
public sealed class LatencyTimeline : Control
{
    private static readonly Typeface Font = new("Inter, Segoe UI, Helvetica Neue, Arial");
    private static readonly IBrush Ink = Brush("#D8DADC");
    private static readonly IBrush Muted = Brush("#9CA3AA");
    private static readonly Pen Grid = new(Brush("#22FFFFFF"), 1);
    private static readonly Pen Line = new(Brush("#E1E6D9"), 1.3);
    private IReadOnlyList<GraphPoint> _samples = Array.Empty<GraphPoint>();
    private TimeSpan _horizon = TimeSpan.FromMinutes(10);
    private DateTimeOffset? _endTime;
    private DateTimeOffset? _selectedTime;
    private string _title = "Latency history";
    private Point? _pointer;
    private IReadOnlyList<GraphEvent> _markers = [];

    public LatencyTimeline() { ClipToBounds = true; MinHeight = 180; }
    public IReadOnlyList<GraphPoint> Samples { get => _samples; set { _samples = value ?? Array.Empty<GraphPoint>(); InvalidateVisual(); } }
    public TimeSpan Horizon { get => _horizon; set { _horizon = value < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : value; InvalidateVisual(); } }
    /// <summary>Null follows the latest sample; a value lets the caller browse earlier history.</summary>
    public DateTimeOffset? EndTime { get => _endTime; set { _endTime = value; InvalidateVisual(); } }
    public DateTimeOffset? SelectedTime { get => _selectedTime; set { _selectedTime = value; InvalidateVisual(); } }
    public string Title { get => _title; set { _title = value ?? "Latency history"; InvalidateVisual(); } }
    public Action<DateTimeOffset>? TimeSelected { get; set; }
    public IReadOnlyList<GraphEvent> Markers { get => _markers; set { _markers = value ?? []; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 100 || Bounds.Height < 100) return;
        context.DrawRectangle(Brush("#202226"), null, new Rect(Bounds.Size));
        var plot = PlotBounds();
        var jitterPlot = new Rect(plot.Left, 27, plot.Width, 25);
        var end = EffectiveEnd();
        var start = end - Horizon;
        var visible = Samples.Where(s => s.Time >= start && s.Time <= end).OrderBy(s => s.Time).ToArray();
        var maxValue = visible.Where(IsResponse).Select(s => s.Latency!.Value).DefaultIfEmpty(0).Max();
        var ceiling = AxisMaximum(maxValue);
        var latest = visible.LastOrDefault();
        Text(context, Title, new Point(10, 7), 11, Ink);
        var latestLabel = latest is null ? "Waiting for measurements" : !IsResponse(latest) ? "Latest: no reply" : $"Latest {latest.Latency:0.0} ms";
        var latestText = Label(latestLabel, 10, Muted);
        context.DrawText(latestText, new Point(Math.Max(170, Bounds.Width - latestText.Width - 12), 8));
        double X(DateTimeOffset time) => plot.Left + (time - start).TotalSeconds / Horizon.TotalSeconds * plot.Width;
        double Y(double value) => plot.Bottom - Math.Clamp(value / ceiling, 0, 1) * plot.Height;

        Band(0, Math.Min(100, ceiling), "#3B5E36");
        Band(100, Math.Min(200, ceiling), "#695C32");
        Band(200, ceiling, "#663B37");
        context.DrawRectangle(Brush("#3D3F40"), null, jitterPlot);
        var intervalSeconds = visible.Length > 1 ? MedianInterval(visible) : 1;
        // An empty part of a capture stays visibly dim, rather than implying recorded zero latency.
        if (Samples.Count > 0)
        {
            var first = Samples.Min(s => s.Time);
            var beforeWidth = Math.Clamp(X(first) - plot.Left, 0, plot.Width);
            context.DrawRectangle(Brush("#66000000"), null, new Rect(plot.Left, plot.Top, beforeWidth, plot.Height));
        }
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            context.DrawLine(Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label($"{ceiling * i / 4:0}", 10, Muted);
            context.DrawText(label, new Point(plot.Left - label.Width - 7, y - label.Height / 2));
            var lossLabel = Label($"{i * 25}", 9, Brush("#DE7772"));
            context.DrawText(lossLabel, new Point(plot.Right + 5, y - lossLabel.Height / 2));
        }
        var latencyAxis = Label("Latency (ms)", 9, Muted);
        var lossAxis = Label("Loss %", 9, Brush("#DE7772"));
        Text(context, "ms", new Point(10, plot.Top - 15), 9, Muted);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(10, plot.Top + (plot.Height + latencyAxis.Width) / 2)))
            context.DrawText(latencyAxis, new Point(0, 0));
        // Keep the right axis readable even on short graphs.
        Text(context, "Loss %", new Point(Math.Max(plot.Left, plot.Right - lossAxis.Width), plot.Top - 15), 9, Brush("#DE7772"));
        for (var i = 0; i <= 5; i++)
        {
            var x = plot.Left + plot.Width * i / 5;
            context.DrawLine(new Pen(Brush("#13FFFFFF"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var time = start + TimeSpan.FromSeconds(Horizon.TotalSeconds * i / 5);
            var label = Label(time.ToLocalTime().ToString(Horizon.TotalMinutes >= 2 ? "h:mm tt" : "HH:mm:ss", CultureInfo.InvariantCulture), 10, Ink);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left - 8, Bounds.Width - label.Width - 5), plot.Bottom + 7));
        }

        using (context.PushClip(plot))
        {
            if (EndTime.HasValue)
                context.DrawRectangle(Brush("#0FFFFFFF"), null, new Rect(plot.Left, plot.Top, plot.Width, plot.Height));
            var lossWidth = Math.Clamp(intervalSeconds / Horizon.TotalSeconds * plot.Width, 1, Math.Max(1, plot.Width / 6));
            foreach (var sample in visible.Where(s => s.IsError))
                context.DrawRectangle(Brush("#99777777"), null, new Rect(X(sample.Time) - lossWidth / 2, plot.Top, lossWidth, plot.Height));
            foreach (var sample in visible.Where(s => !s.IsError && (s.LossPercent > 0 || s.Lost)))
            {
                var height = plot.Height * Math.Clamp((sample.LossPercent ?? 100) / 100, 0, 1);
                context.DrawRectangle(Brush("#AED44843"), null, new Rect(X(sample.Time) - lossWidth / 2, plot.Bottom - height, lossWidth, height));
            }
            foreach (var marker in Markers.Where(m => m.Time >= start && m.Time <= end))
            {
                var color = marker.Kind == "Recovery" ? "#75D88A" : marker.Kind == "RouteChange" ? "#E7B95D" : "#E5817B";
                context.DrawLine(new Pen(Brush(color), 1), new Point(X(marker.Time), plot.Top), new Point(X(marker.Time), plot.Bottom));
                context.DrawEllipse(Brush(color), null, new Point(X(marker.Time), plot.Top + 4), 3, 3);
            }
            GraphPoint? previous = null;
            foreach (var sample in visible)
            {
                if (!IsResponse(sample)) { previous = null; continue; }
                var point = new Point(X(sample.Time), Y(sample.Latency!.Value));
                if (previous is not null && (sample.Time - previous.Time).TotalSeconds <= intervalSeconds * 3.5)
                {
                    var before = new Point(X(previous.Time), Y(previous.Latency!.Value));
                    var corner = new Point(point.X, before.Y);
                    context.DrawLine(Line, before, corner);
                    context.DrawLine(Line, corner, point);
                }
                else context.DrawEllipse(Line.Brush, null, point, 1.5, 1.5);
                previous = sample;
            }
        }
        DrawJitter(context, jitterPlot, visible, intervalSeconds, X);
        if (visible.Length == 0)
        {
            var message = Label(Samples.Count == 0 ? "Start a target to collect latency and packet loss" : "No measurements in this time range", 11, Muted);
            context.DrawText(message, new Point(plot.Left + (plot.Width - message.Width) / 2, plot.Top + plot.Height / 2 - message.Height / 2));
        }
        var activeTime = _pointer is { } pointer && plot.Contains(pointer)
            ? start + TimeSpan.FromSeconds((pointer.X - plot.Left) / plot.Width * Horizon.TotalSeconds)
            : SelectedTime;
        if (activeTime is { } cursor && cursor >= start && cursor <= end)
        {
            var x = X(cursor);
            context.DrawRectangle(Brush("#15FFFFFF"), null, new Rect(x - 3, plot.Top, 6, plot.Height));
            context.DrawLine(new Pen(Brush("#ABB5A9"), 1), new Point(x, jitterPlot.Top), new Point(x, plot.Bottom));
            var nearest = visible.MinBy(s => Math.Abs((s.Time - cursor).TotalMilliseconds));
            if (nearest is not null)
            {
                var nearEvent = Markers.FirstOrDefault(m => Math.Abs((m.Time - cursor).TotalSeconds) < Math.Max(1, Horizon.TotalSeconds * 5 / plot.Width));
                var text = nearEvent is not null ? $"{nearEvent.Time.ToLocalTime():HH:mm:ss} · {nearEvent.Message}"
                    : $"{nearest.Time.ToLocalTime():HH:mm:ss}  ·  " + (nearest.IsError ? "Probe unavailable" : !IsResponse(nearest) ? "No reply" : $"{nearest.Latency:0.0} ms")
                      + (nearest.LossPercent is { } loss ? $" · {loss:0.#}% loss" : "");
                if (text.Length > 100) text = text[..97] + "…";
                var label = Label(text, 10, Ink);
                var boxWidth = label.Width + 16;
                var box = new Rect(Math.Clamp(x + 8, plot.Left, Math.Max(plot.Left, plot.Right - boxWidth)), plot.Top + 6, boxWidth, 23);
                context.DrawRectangle(Brush("#E82B2D31"), new Pen(Brush("#777D76"), 1), box);
                context.DrawText(label, new Point(box.Left + 8, box.Top + 5));
                if (IsResponse(nearest)) context.DrawEllipse(Brush("#202226"), Line, new Point(X(nearest.Time), Y(nearest.Latency!.Value)), 3, 3);
            }
        }
        context.DrawRectangle(null, new Pen(Brush("#747A70"), 1), plot);
        context.DrawRectangle(null, new Pen(Brush("#626567"), 1), jitterPlot);
        void Band(double from, double to, string color)
        {
            if (to <= from || from >= ceiling) return;
            context.DrawRectangle(Brush(color), null, new Rect(plot.Left, Y(to), plot.Width, Y(from) - Y(to)));
        }
    }

    private static void DrawJitter(DrawingContext context, Rect plot, GraphPoint[] points, double interval, Func<DateTimeOffset, double> x)
    {
        var differences = new List<(DateTimeOffset Time, double Value)>();
        GraphPoint? previous = null;
        foreach (var point in points)
        {
            if (!IsResponse(point)) { previous = null; continue; }
            if (point.JitterMs is { } aggregatedJitter)
            {
                differences.Add((point.Time, aggregatedJitter)); previous = point; continue;
            }
            if (previous is not null && (point.Time - previous.Time).TotalSeconds <= interval * 3.5)
                differences.Add((point.Time, Math.Abs(point.Latency!.Value - previous.Latency!.Value)));
            previous = point;
        }
        var ceiling = Math.Max(5, differences.Select(p => p.Value).DefaultIfEmpty(0).Max() * 1.15);
        var mean = differences.Count == 0 ? 0 : differences.Average(p => p.Value);
        var label = Label($"Jitter (ms) · mean {mean:0.0}", 9, Brush("#DF9576"));
        context.DrawText(label, new Point(plot.Left + 6, plot.Top + 3));
        using (context.PushClip(plot))
        {
            Point? before = null;
            DateTimeOffset? beforeTime = null;
            var pen = new Pen(Brush("#CD9476"), 1);
            foreach (var point in differences)
            {
                var p = new Point(x(point.Time), plot.Bottom - 2 - point.Value / ceiling * (plot.Height - 5));
                if (before is { } b && beforeTime is { } t && (point.Time - t).TotalSeconds <= interval * 3.5)
                {
                    context.DrawLine(pen, b, new Point(p.X, b.Y));
                    context.DrawLine(pen, new Point(p.X, b.Y), p);
                }
                before = p;
                beforeTime = point.Time;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); _pointer = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _pointer = null; InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var position = e.GetPosition(this);
        var plot = PlotBounds();
        if (!plot.Contains(position) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SelectedTime = EffectiveEnd() - Horizon + TimeSpan.FromSeconds((position.X - plot.Left) / plot.Width * Horizon.TotalSeconds);
        TimeSelected?.Invoke(SelectedTime.Value);
        e.Handled = true;
    }
    private Rect PlotBounds() => new(46, 64, Math.Max(1, Bounds.Width - 82), Math.Max(1, Bounds.Height - 94));
    private DateTimeOffset EffectiveEnd() => EndTime ?? (Samples.Count > 0 ? Samples.Max(s => s.Time) : DateTimeOffset.UtcNow);
    private static bool IsResponse(GraphPoint sample) => !sample.IsError && !sample.Lost && sample.Latency is >= 0 && double.IsFinite(sample.Latency.Value);
    private static double AxisMaximum(double maximum)
    {
        var value = Math.Max(40, maximum * 1.18);
        var step = value <= 100 ? 10 : value <= 200 ? 25 : value <= 1000 ? 100 : Math.Pow(10, Math.Floor(Math.Log10(value))) / 2;
        return Math.Ceiling(value / step) * step;
    }
    private static double MedianInterval(GraphPoint[] points)
    {
        var intervals = points.Zip(points.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).Where(s => s > 0).OrderBy(s => s).ToArray();
        return intervals.Length == 0 ? 1 : intervals[intervals.Length / 2];
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private static FormattedText Label(string text, double size, IBrush color) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, color);
    private static void Text(DrawingContext context, string text, Point point, double size, IBrush color) => context.DrawText(Label(text, size, color), point);
}
