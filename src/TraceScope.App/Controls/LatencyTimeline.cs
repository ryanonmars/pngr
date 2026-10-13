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
    private double _warningLatencyMs = 100;
    private double _criticalLatencyMs = 200;
    private double _fixedMaximumMs;
    private bool _showJitter = true;
    private bool _showPacketLoss = true;
    private bool _showEventMarkers = true;
    private TimelineViewport? _viewport;
    private Point? _dragPoint;
    private bool _selecting;
    private bool _dragged;
    private DateTimeOffset _selectionAnchor;
    private double _pinchScale = 1;

    public TimelineViewport? Viewport
    {
        get => _viewport;
        set
        {
            if (_viewport == value) return;
            if (_viewport is not null) _viewport.Changed -= OnViewportChanged;
            _viewport = value;
            if (_viewport is not null) _viewport.Changed += OnViewportChanged;
            InvalidateVisual();
        }
    }
    private void OnViewportChanged() => InvalidateVisual();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewport is not null) { _viewport.Changed -= OnViewportChanged; _viewport.Changed += OnViewportChanged; }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewport is not null) _viewport.Changed -= OnViewportChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public LatencyTimeline()
    {
        ClipToBounds = true; MinHeight = 180; Focusable = true;
        GestureRecognizers.Add(new Avalonia.Input.PinchGestureRecognizer());
        AddHandler(Gestures.PinchEvent, (_, e) =>
        {
            var factor = _pinchScale / e.Scale;
            Navigation().Zoom(factor, Math.Clamp((e.ScaleOrigin.X - PlotBounds().Left) / PlotBounds().Width, 0, 1));
            _pinchScale = e.Scale;
            e.Handled = true;
        });
        AddHandler(Gestures.PinchEndedEvent, (_, _) => _pinchScale = 1);
    }
    public IReadOnlyList<GraphPoint> Samples { get => _samples; set { _samples = value ?? Array.Empty<GraphPoint>(); InvalidateVisual(); } }
    public TimeSpan Horizon { get => Viewport?.Horizon ?? _horizon; set { if (Viewport is { } v) v.Horizon = value; else _horizon = value < TimelineViewport.MinimumHorizon ? TimelineViewport.MinimumHorizon : value > TimelineViewport.MaximumHorizon ? TimelineViewport.MaximumHorizon : value; InvalidateVisual(); } }
    /// <summary>Null follows the latest sample; a value lets the caller browse earlier history.</summary>
    public DateTimeOffset? EndTime { get => Viewport?.EffectiveEnd ?? _endTime; set { if (Viewport is { } v) v.EndTime = value; else _endTime = value; InvalidateVisual(); } }
    public DateTimeOffset? SelectedTime { get => _selectedTime; set { _selectedTime = value; InvalidateVisual(); } }
    public string Title { get => _title; set { _title = value ?? "Latency history"; InvalidateVisual(); } }
    public Action<DateTimeOffset>? TimeSelected { get; set; }
    public IReadOnlyList<GraphEvent> Markers { get => _markers; set { _markers = value ?? []; InvalidateVisual(); } }
    public double WarningLatencyMs { get => _warningLatencyMs; set { _warningLatencyMs = Nonnegative(value, _warningLatencyMs); InvalidateVisual(); } }
    public double CriticalLatencyMs { get => _criticalLatencyMs; set { _criticalLatencyMs = Nonnegative(value, _criticalLatencyMs); InvalidateVisual(); } }
    /// <summary>Zero scales automatically to the measured latency range.</summary>
    public double FixedMaximumMs { get => _fixedMaximumMs; set { _fixedMaximumMs = Nonnegative(value, _fixedMaximumMs); InvalidateVisual(); } }
    public bool ShowJitter { get => _showJitter; set { _showJitter = value; InvalidateVisual(); } }
    public bool ShowPacketLoss { get => _showPacketLoss; set { _showPacketLoss = value; InvalidateVisual(); } }
    public bool ShowEventMarkers { get => _showEventMarkers; set { _showEventMarkers = value; InvalidateVisual(); } }

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
        var ceiling = FixedMaximumMs > 0 ? FixedMaximumMs : AxisMaximum(maxValue);
        var critical = Math.Max(WarningLatencyMs, CriticalLatencyMs);
        var latest = visible.LastOrDefault();
        Text(context, Title, new Point(10, 7), 11, Ink);
        var latestLabel = latest is null ? "Waiting for measurements" : !IsResponse(latest) ? "Latest: no reply" : $"{(latest.IsAggregated ? "Latest bin mean" : "Latest")} {latest.Latency:0.0} ms";
        var latestText = Label(latestLabel, 10, Muted);
        context.DrawText(latestText, new Point(Math.Max(170, Bounds.Width - latestText.Width - 12), 8));
        double X(DateTimeOffset time) => plot.Left + (time - start).TotalSeconds / Horizon.TotalSeconds * plot.Width;
        double Y(double value) => plot.Bottom - Math.Clamp(value / ceiling, 0, 1) * plot.Height;

        Band(0, Math.Min(WarningLatencyMs, ceiling), "#3B5E36");
        Band(WarningLatencyMs, Math.Min(critical, ceiling), "#695C32");
        Band(critical, ceiling, "#663B37");
        if (ShowJitter) context.DrawRectangle(Brush("#3D3F40"), null, jitterPlot);
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
            if (ShowPacketLoss)
            {
                var lossLabel = Label($"{i * 25}", 9, Brush("#DE7772"));
                context.DrawText(lossLabel, new Point(plot.Right + 5, y - lossLabel.Height / 2));
            }
        }
        var latencyAxis = Label("Latency (ms)", 9, Muted);
        var lossAxis = Label("Loss %", 9, Brush("#DE7772"));
        Text(context, "ms", new Point(10, plot.Top - 15), 9, Muted);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(10, plot.Top + (plot.Height + latencyAxis.Width) / 2)))
            context.DrawText(latencyAxis, new Point(0, 0));
        // Keep the right axis readable even on short graphs.
        if (ShowPacketLoss) Text(context, "Loss %", new Point(Math.Max(plot.Left, plot.Right - lossAxis.Width), plot.Top - 15), 9, Brush("#DE7772"));
        foreach (var time in AxisTicks(start, end, plot.Width))
        {
            var x = X(time);
            context.DrawLine(new Pen(Brush("#13FFFFFF"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Label(FormatTimestamp(time, Horizon), 10, Ink);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left - 8, Bounds.Width - label.Width - 5), plot.Bottom + 7));
        }

        using (context.PushClip(plot))
        {
            if (Viewport is { EndTime: not null } || Viewport is null && EndTime.HasValue)
                context.DrawRectangle(Brush("#0FFFFFFF"), null, new Rect(plot.Left, plot.Top, plot.Width, plot.Height));
            var lossWidth = Math.Clamp(intervalSeconds / Horizon.TotalSeconds * plot.Width, 1, Math.Max(1, plot.Width / 6));
            foreach (var sample in visible.Where(s => s.IsError))
                context.DrawRectangle(Brush("#99777777"), null, new Rect(X(sample.Time) - lossWidth / 2, plot.Top, lossWidth, plot.Height));
            foreach (var sample in visible.Where(s => ShowPacketLoss && !s.IsError && (s.LossPercent > 0 || s.Lost)))
            {
                var height = plot.Height * Math.Clamp((sample.LossPercent ?? 100) / 100, 0, 1);
                context.DrawRectangle(Brush("#AED44843"), null, new Rect(X(sample.Time) - lossWidth / 2, plot.Bottom - height, lossWidth, height));
            }
            foreach (var marker in Markers.Where(m => ShowEventMarkers && m.Time >= start && m.Time <= end))
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
                if (previous is not null && SameAddress(previous, sample) && (sample.Time - previous.Time).TotalSeconds <= intervalSeconds * 3.5)
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
        if (Viewport?.SelectionStart is { } selectionStart && Viewport.SelectionEnd is { } selectionEnd)
        {
            var left = Math.Clamp(X(selectionStart), plot.Left, plot.Right);
            var right = Math.Clamp(X(selectionEnd), plot.Left, plot.Right);
            if (right >= left)
            {
                context.DrawRectangle(Brush("#354DA4D8"), null, new Rect(left, plot.Top, right - left, plot.Height));
                var selectionPen = new Pen(Brush("#82C8F0"), 1);
                context.DrawLine(selectionPen, new Point(left, plot.Top), new Point(left, plot.Bottom));
                context.DrawLine(selectionPen, new Point(right, plot.Top), new Point(right, plot.Bottom));
            }
        }
        if (ShowJitter) DrawJitter(context, jitterPlot, visible, intervalSeconds, X);
        if (visible.Length == 0)
        {
            var message = Label(Samples.Count == 0 && Viewport?.First is null ? "Start a target to collect latency and packet loss" : "No measurements in this time range", 11, Muted);
            context.DrawText(message, new Point(plot.Left + (plot.Width - message.Width) / 2, plot.Top + plot.Height / 2 - message.Height / 2));
        }
        var activeTime = _pointer is { } pointer && plot.Contains(pointer)
            ? start + TimeSpan.FromSeconds((pointer.X - plot.Left) / plot.Width * Horizon.TotalSeconds)
            : SelectedTime;
        if (activeTime is { } cursor && cursor >= start && cursor <= end)
        {
            var x = X(cursor);
            context.DrawRectangle(Brush("#15FFFFFF"), null, new Rect(x - 3, plot.Top, 6, plot.Height));
            context.DrawLine(new Pen(Brush("#ABB5A9"), 1), new Point(x, ShowJitter ? jitterPlot.Top : plot.Top), new Point(x, plot.Bottom));
            var nearest = visible.MinBy(s => Math.Abs((s.Time - cursor).TotalMilliseconds));
            if (nearest is not null)
            {
                var nearEvent = ShowEventMarkers ? Markers.FirstOrDefault(m => Math.Abs((m.Time - cursor).TotalSeconds) < Math.Max(1, Horizon.TotalSeconds * 5 / plot.Width)) : null;
                var before = visible.LastOrDefault(s => s.Time < nearest.Time);
                var measuredJitter = nearest.JitterMs ?? (before is not null && SameAddress(before, nearest) && IsResponse(before) && IsResponse(nearest)
                    && (nearest.Time - before.Time).TotalSeconds <= intervalSeconds * 3.5 ? Math.Abs(nearest.Latency!.Value - before.Latency!.Value) : (double?)null);
                var text = $"{nearest.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  ·  " + (nearest.IsError ? "Probe unavailable" : !IsResponse(nearest) ? "No reply" : $"{nearest.Latency:0.0} ms")
                      + (ShowPacketLoss ? $" · {nearest.LossPercent ?? (nearest.Lost ? 100 : 0):0.#}% loss" : "")
                      + (measuredJitter is { } jitter ? $" · jitter {jitter:0.0} ms" : "")
                      + (nearest.IsAggregated ? $" · aggregated ({nearest.SampleCount} probes)" : "")
                      + (nearEvent is not null ? $"\n{nearEvent.Time.ToLocalTime():HH:mm:ss.fff} · {nearEvent.Kind}: {nearEvent.Message}" : "");
                var label = Label(text, 10, Ink);
                label.MaxTextWidth = Math.Max(80, plot.Width - 16);
                var boxWidth = label.Width + 16;
                var box = new Rect(Math.Clamp(x + 8, plot.Left, Math.Max(plot.Left, plot.Right - boxWidth)), plot.Top + 6, boxWidth, label.Height + 10);
                context.DrawRectangle(Brush("#E82B2D31"), new Pen(Brush("#777D76"), 1), box);
                context.DrawText(label, new Point(box.Left + 8, box.Top + 5));
                if (IsResponse(nearest)) context.DrawEllipse(Brush("#202226"), Line, new Point(X(nearest.Time), Y(nearest.Latency!.Value)), 3, 3);
            }
        }
        context.DrawRectangle(null, new Pen(Brush("#747A70"), 1), plot);
        if (ShowJitter) context.DrawRectangle(null, new Pen(Brush("#626567"), 1), jitterPlot);
        void Band(double from, double to, string color)
        {
            if (to <= from || from >= ceiling) return;
            context.DrawRectangle(Brush(color), null, new Rect(plot.Left, Y(to), plot.Width, Y(from) - Y(to)));
        }
    }

    private static void DrawJitter(DrawingContext context, Rect plot, GraphPoint[] points, double interval, Func<DateTimeOffset, double> x)
    {
        var differences = new List<(DateTimeOffset Time, double Value, string? Address)>();
        GraphPoint? previous = null;
        foreach (var point in points)
        {
            if (!IsResponse(point)) { previous = null; continue; }
            if (point.JitterMs is { } aggregatedJitter)
            {
                differences.Add((point.Time, aggregatedJitter, point.Address)); previous = point; continue;
            }
            if (previous is not null && SameAddress(previous, point) && (point.Time - previous.Time).TotalSeconds <= interval * 3.5)
                differences.Add((point.Time, Math.Abs(point.Latency!.Value - previous.Latency!.Value), point.Address));
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
            string? beforeAddress = null;
            var pen = new Pen(Brush("#CD9476"), 1);
            foreach (var point in differences)
            {
                var p = new Point(x(point.Time), plot.Bottom - 2 - point.Value / ceiling * (plot.Height - 5));
                if (before is { } b && beforeTime is { } t && (beforeAddress is null || point.Address is null || string.Equals(beforeAddress, point.Address, StringComparison.OrdinalIgnoreCase)) && (point.Time - t).TotalSeconds <= interval * 3.5)
                {
                    context.DrawLine(pen, b, new Point(p.X, b.Y));
                    context.DrawLine(pen, new Point(p.X, b.Y), p);
                }
                before = p;
                beforeTime = point.Time;
                beforeAddress = point.Address;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_dragPoint is { } before)
        {
            if (Math.Abs(position.X - before.X) > .1)
            {
                _dragged = true;
                if (_selecting) Navigation().SelectRange(_selectionAnchor, TimestampAt(position.X));
                else Navigation().Pan(TimeSpan.FromTicks((long)((before.X - position.X) / PlotBounds().Width * Horizon.Ticks)));
                _dragPoint = position;
            }
            e.Handled = true;
        }
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _pointer = null; InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var position = e.GetPosition(this);
        if (!new Rect(Bounds.Size).Contains(position) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragPoint = position;
        _dragged = false;
        _selecting = e.ClickCount >= 2;
        _selectionAnchor = TimestampAt(position.X);
        if (_selecting) Navigation().SelectRange(_selectionAnchor, _selectionAnchor);
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragPoint is null) return;
        if (!_dragged && !_selecting)
        {
            SelectedTime = TimestampAt(e.GetPosition(this).X);
            TimeSelected?.Invoke(SelectedTime.Value);
        }
        _dragPoint = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _dragPoint = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) return;
        var zoomModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.KeyModifiers & zoomModifier) != 0)
        {
            Navigation().Zoom(Math.Exp(-e.Delta.Y * .15), Math.Clamp((e.GetPosition(this).X - PlotBounds().Left) / PlotBounds().Width, 0, 1));
            e.Handled = true;
        }
        else if (Math.Abs(e.Delta.X) > .0001 && Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) * .5 || (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            Navigation().Pan(TimeSpan.FromTicks((long)(-delta * Horizon.Ticks * .04)));
            e.Handled = true;
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var navigation = Navigation();
        switch (e.Key)
        {
            case Key.Left: navigation.Pan(-Horizon / 10); break;
            case Key.Right: navigation.Pan(Horizon / 10); break;
            case Key.OemPlus: case Key.Add: navigation.Zoom(.8, .5); break;
            case Key.OemMinus: case Key.Subtract: navigation.Zoom(1.25, .5); break;
            case Key.Escape: navigation.ClearSelection(); break;
            case Key.Home: navigation.GoLive(); break;
            default: return;
        }
        e.Handled = true;
    }
    private TimelineViewport Navigation()
    {
        if (Viewport is not null) return Viewport;
        var viewport = new TimelineViewport { Horizon = _horizon, EndTime = _endTime };
        if (Samples.Count > 0) { viewport.First = Samples.Min(s => s.Time); viewport.Last = Samples.Max(s => s.Time); }
        Viewport = viewport;
        return viewport;
    }
    public DateTimeOffset TimestampAt(double x) => EffectiveEnd() - Horizon + TimeSpan.FromTicks((long)(Math.Clamp((x - PlotBounds().Left) / PlotBounds().Width, 0, 1) * Horizon.Ticks));
    public static string FormatTimestamp(DateTimeOffset time, TimeSpan horizon)
        => time.ToLocalTime().ToString(horizon.TotalMinutes <= 10 ? "HH:mm:ss" : horizon.TotalHours <= 24 ? "HH:mm" : "MMM d HH:mm", CultureInfo.InvariantCulture);
    private static IEnumerable<DateTimeOffset> AxisTicks(DateTimeOffset start, DateTimeOffset end, double width)
    {
        var count = Math.Max(2, (int)(width / 100));
        var seconds = (end - start).TotalSeconds / count;
        double[] intervals = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 43200, 86400];
        var step = intervals.FirstOrDefault(i => i >= seconds, 86400);
        var ticks = TimeSpan.FromSeconds(step).Ticks;
        // Align local labels, including zones with half-hour UTC offsets.
        var localStart = start.ToLocalTime();
        var firstTicks = (localStart.Ticks / ticks + 1) * ticks;
        for (var time = new DateTimeOffset(firstTicks, localStart.Offset); time <= end; time += TimeSpan.FromTicks(ticks)) yield return time;
    }
    private Rect PlotBounds()
    {
        var top = ShowJitter ? 64 : 37;
        var rightMargin = ShowPacketLoss ? 36 : 12;
        return new Rect(46, top, Math.Max(1, Bounds.Width - 46 - rightMargin), Math.Max(1, Bounds.Height - top - 30));
    }
    private DateTimeOffset EffectiveEnd() => Viewport?.EffectiveEnd ?? EndTime ?? (Samples.Count > 0 ? Samples.Max(s => s.Time) : DateTimeOffset.UtcNow);
    private static bool SameAddress(GraphPoint first, GraphPoint second)
        => first.Address is null || second.Address is null || string.Equals(first.Address, second.Address, StringComparison.OrdinalIgnoreCase);
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
    private static double Nonnegative(double value, double fallback) => double.IsFinite(value) ? Math.Max(0, value) : fallback;
    private static FormattedText Label(string text, double size, IBrush color) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, color);
    private static void Text(DrawingContext context, string text, Point point, double size, IBrush color) => context.DrawText(Label(text, size, color), point);
}
