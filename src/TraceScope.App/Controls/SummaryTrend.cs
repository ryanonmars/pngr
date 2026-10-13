using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App.Controls;

/// <summary>Independent compact graph: measured latency lines and packet-loss bars on shared time axes.</summary>
public sealed class SummaryTrend : Control
{
    private static readonly IBrush Ink = Brush.Parse("#B6BEC8");
    private static readonly string[] Palette = ["#D6E8CE", "#79C6E4", "#D5AEF4", "#F1C470", "#78D8C3", "#EAB8CF"];
    public IReadOnlyList<SummaryGraphSeries> Series { get; init; } = [];
    public DateTimeOffset End { get; init; }
    public TimeSpan Horizon { get; init; } = TimeSpan.FromMinutes(5);
    public bool ShowAxes { get; init; }

    public SummaryTrend() { ClipToBounds = true; MinHeight = 19; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var top = ShowAxes && Series.Count > 1 ? 18 : 2;
        var plot = new Rect(ShowAxes ? 32 : 2, top, Math.Max(1, Bounds.Width - (ShowAxes ? 62 : 4)), Math.Max(1, Bounds.Height - top - (ShowAxes ? 22 : 2)));
        if (top == 18)
        {
            var left = 32d;
            foreach (var (series, index) in Series.Select((s, i) => (s, i)))
            {
                var title = series.Target.Length <= 28 ? series.Target : series.Target[..25] + "…";
                if (left + title.Length * 6 > Bounds.Width - 60)
                { Label(context, $"+{Series.Count - index} more", new Point(left, 2), Ink); break; }
                left += Label(context, "● " + title, new Point(left, 2), Brush.Parse(Palette[index % Palette.Length])) + 15;
            }
        }
        context.DrawRectangle(Brush.Parse("#2B362B"), null, plot);
        var maximum = Math.Max(40, Series.SelectMany(s => s.Points).Select(p => p.AverageMs ?? 0).DefaultIfEmpty(0).Max() * 1.12);
        var begin = End - Horizon;
        double X(DateTimeOffset time) => plot.Left + (time - begin).TotalSeconds / Horizon.TotalSeconds * plot.Width;
        double Y(double latency) => plot.Bottom - Math.Clamp(latency / maximum, 0, 1) * plot.Height;
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - i / 4d * plot.Height;
            context.DrawLine(new Pen(Brush.Parse("#1CFFFFFF"), 1), new Point(plot.Left, y), new Point(plot.Right, y));
            if (ShowAxes)
            {
                Label(context, (maximum * i / 4).ToString("0"), new Point(0, y - 6), Ink);
                Label(context, (i * 25).ToString(), new Point(plot.Right + 4, y - 6), Brush.Parse("#DD8179"));
            }
        }
        using (context.PushClip(plot))
        {
            foreach (var (series, index) in Series.Select((s, i) => (s, i)))
            {
                var pen = new Pen(Brush.Parse(Palette[index % Palette.Length]), 1.25);
                SummaryBucket? previous = null;
                foreach (var point in series.Points)
                {
                    var x = X(point.Timestamp);
                    if (point.Sent > 0 && point.LossPercent > 0)
                    {
                        var height = point.LossPercent / 100 * plot.Height;
                        context.DrawRectangle(Brush.Parse("#89D85B54"), null, new Rect(x - 1, plot.Bottom - height, 2, height));
                    }
                    if (point.Sent == 0 && point.Errors > 0)
                        context.DrawLine(new Pen(Brush.Parse("#707D848C"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
                    if (point.AverageMs is not { } latency) { previous = null; continue; }
                    var position = new Point(x, Y(latency));
                    // Leave long unobserved periods blank rather than connecting across an outage or pause.
                    var maxGap = Math.Max(series.ExpectedIntervalSeconds * 3.5, Horizon.TotalSeconds / 300 * 3.5);
                    if (previous?.AverageMs is { } before && (point.Timestamp - previous.Timestamp).TotalSeconds <= maxGap)
                        context.DrawLine(pen, new Point(X(previous.Timestamp), Y(before)), position);
                    else context.DrawEllipse(pen.Brush, null, position, 1.2, 1.2);
                    previous = point;
                }
            }
        }
        if (ShowAxes)
        {
            Label(context, begin.ToLocalTime().ToString("HH:mm"), new Point(plot.Left, plot.Bottom + 6), Ink);
            Label(context, "ms / loss %", new Point(Math.Max(plot.Left, plot.Right - 80), plot.Bottom + 6), Ink);
        }
        if (Series.All(s => s.Points.Count == 0))
            Label(context, "No measurements in this range", new Point(plot.Left + 6, plot.Top + 5), Ink);
    }

    private static double Label(DrawingContext context, string value, Point position, IBrush brush)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI, Helvetica Neue, Arial"), 9, brush);
        context.DrawText(text, position);
        return text.Width;
    }
}

public sealed record SummaryGraphSeries(string Target, IReadOnlyList<SummaryBucket> Points, double ExpectedIntervalSeconds = 2.5);
