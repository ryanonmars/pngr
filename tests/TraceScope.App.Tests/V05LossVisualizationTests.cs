using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class V05LossVisualizationTests
{
    [AvaloniaFact]
    public void MeasuredLossProducesProportionalStripsAlongsideLatencyMarkers()
    {
        var rows = new[]
        {
            new HopPlotRow(1, 10, 20, 30, 25, 0),
            new HopPlotRow(2, 10, 20, 30, 25, 25),
            new HopPlotRow(3, null, null, null, null, 100)
        };
        var plot = new HopLatencyPlot { Rows = rows };
        var drawing = Record(plot);
        var strips = Filled(drawing, "#F57161").ToArray();
        Assert.Equal(2, strips.Length);
        Assert.Equal(strips[1].Geometry!.Bounds.Width / 4, strips[0].Geometry!.Bounds.Width, 5);
        Assert.All(strips, strip => Assert.Equal(3, strip.Geometry!.Bounds.Height));
        Assert.Equal(2, Geometry(drawing).Count(g => g.Pen?.Brush is ISolidColorBrush brush
            && brush.Color == Color.Parse("#E27768")));
        plot.ShowPacketLoss = false;
        Assert.Empty(Filled(Record(plot), "#F57161"));
        Assert.Same(rows, plot.Rows);
    }

    [AvaloniaFact]
    public void InvalidLossCannotDrawAnIndicatorAndHighLossIsClamped()
    {
        var plot = new HopLatencyPlot
        {
            Rows = [new(1, null, null, null, null, double.NaN), new(2, null, null, null, null, -10), new(3, null, null, null, null, 200)]
        };
        var strip = Assert.Single(Filled(Record(plot), "#F57161"));
        Assert.Equal(586, strip.Geometry!.Bounds.Width);
    }

    [AvaloniaFact]
    public void OverviewRetainsTargetCountFooterAndAppliesLossVisibility()
    {
        var view = new AllTargetsView();
        var end = DateTimeOffset.UtcNow;
        var samples = new[] { new LatencySample(end.AddSeconds(-1), 10, ProbeStatus.Success), new LatencySample(end, null, ProbeStatus.Timeout) };
        var destination = new HopSnapshot(0, "127.0.0.1", null, 10, 10, 10, 10, 50, 2, 1, 0, true, samples, ProbeStatus.Timeout);
        var item = new TargetItem(new StoredSession(Guid.NewGuid(), "127.0.0.1", "127.0.0.1", end, null, 2))
        { Snapshot = new RoundSnapshot(Guid.NewGuid(), "127.0.0.1", "127.0.0.1", end, 1, true, [], destination, TimeSpan.Zero) };
        view.Refresh([item], item.Id, end, TimeSpan.FromMinutes(10), [], new DisplayConfiguration { ShowPacketLoss = false });
        var row = Assert.Single(view.Rows);
        Assert.Equal(50, row.Loss);
        Assert.False(row.ShowPacketLoss);
        view.Refresh([item], item.Id, end, TimeSpan.FromMinutes(10), [], new DisplayConfiguration { ShowPacketLoss = true });
        Assert.True(row.ShowPacketLoss);
        var layout = Assert.IsType<Grid>(view.Content);
        var footer = Assert.IsType<Grid>(layout.Children[1]);
        Assert.Equal("1 target", Assert.IsType<TextBlock>(footer.Children[0]).Text);
    }

    private static DrawingGroup Record(Control control)
    {
        control.Measure(new Size(600, 300)); control.Arrange(new Rect(0, 0, 600, 300));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) control.Render(context);
        return drawing;
    }
    private static IEnumerable<GeometryDrawing> Geometry(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Geometry(child)) yield return descendant;
    }
    private static IEnumerable<GeometryDrawing> Filled(Drawing drawing, string color)
        => Geometry(drawing).Where(g => g.Pen is null && g.Brush is ISolidColorBrush brush && brush.Color == Color.Parse(color));
}
