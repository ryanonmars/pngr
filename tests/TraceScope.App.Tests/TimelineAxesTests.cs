using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TraceScope.App.Controls;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class TimelineAxesTests
{
    [AvaloniaFact]
    public void LossVisibilityDoesNotReserveAnotherAxisOrMoveLatencyGeometry()
    {
        var plot = new LatencyTimeline { ShowJitter = false };
        Arrange(plot, 300);
        var withLoss = Bounds(plot);
        plot.ShowPacketLoss = false;
        Assert.Equal(withLoss, Bounds(plot));
        Assert.Equal(574, withLoss.Right);
        Assert.Equal(18, withLoss.Top);
        plot.ShowJitter = true;
        Assert.Equal(43, Bounds(plot).Top);
        Arrange(plot, 100);
        Assert.Equal(18, Bounds(plot).Top);
    }

    [AvaloniaFact]
    public void RawUnansweredProbesAndBinnedLossRetainTheirActualHeights()
    {
        var end = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new GraphPoint(end.AddSeconds(-20), null, true),
            new GraphPoint(end.AddSeconds(-10), 12, false, LossPercent: 25, IsAggregated: true, SampleCount: 20)
        };
        var plot = new LatencyTimeline
        {
            ShowJitter = false, EndTime = end, Horizon = TimeSpan.FromMinutes(1), Samples = samples
        };
        Arrange(plot, 300);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) plot.Render(context);
        var bars = Geometry(drawing).Where(g => g.Pen is null && g.Brush is ISolidColorBrush b
            && b.Color == Color.Parse("#AED44843")).ToArray();
        Assert.Equal(2, bars.Length);
        Assert.Equal(Bounds(plot).Height, bars[0].Geometry!.Bounds.Height);
        Assert.Equal(bars[0].Geometry!.Bounds.Height / 4, bars[1].Geometry!.Bounds.Height, 5);
        Assert.Same(samples, plot.Samples);
    }

    [AvaloniaFact]
    public void PacketLossLabelIsClippedBesideLatencyPlotAtNormalAndCompactSizes()
    {
        foreach (var height in new[] { 300d, 100d, 62d })
        {
            var plot = new LatencyTimeline();
            Arrange(plot, height);
            var bounds = Bounds(plot);
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) plot.Render(context);
            var axisClip = new Rect(bounds.Right + 4, bounds.Top, 22, bounds.Height);
            Assert.Contains(Groups(drawing), group => group.ClipGeometry?.Bounds == axisClip);
            Assert.True(axisClip.Left > bounds.Right);
            Assert.True(axisClip.Top >= (height >= 140 ? 43 : 18));
            plot.ShowPacketLoss = false;
            var hidden = new DrawingGroup();
            using (var context = hidden.Open()) plot.Render(context);
            Assert.DoesNotContain(Groups(hidden), group => group.ClipGeometry?.Bounds == axisClip);
            Assert.Equal(bounds, Bounds(plot));
        }
    }

    private static IEnumerable<DrawingGroup> Groups(Drawing drawing)
    {
        if (drawing is not DrawingGroup group) yield break;
        yield return group;
        foreach (var child in group.Children)
            foreach (var descendant in Groups(child)) yield return descendant;
    }

    private static void Arrange(LatencyTimeline plot, double height)
    {
        plot.Measure(new Size(600, height));
        plot.Arrange(new Rect(0, 0, 600, height));
    }
    private static Rect Bounds(LatencyTimeline plot) => (Rect)typeof(LatencyTimeline)
        .GetMethod("PlotBounds", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(plot, null)!;
    private static IEnumerable<GeometryDrawing> Geometry(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Geometry(child)) yield return descendant;
    }
}
