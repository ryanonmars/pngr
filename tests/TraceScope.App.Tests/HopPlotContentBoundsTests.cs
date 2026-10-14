using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TraceScope.App.Controls;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class HopPlotContentBoundsTests
{
    [AvaloniaFact]
    public void ShadingStopsAfterPopulatedRowsDespiteTallPanel()
    {
        var plot = Create(8);
        AssertBands(plot, 600, 176);
        AssertBands(plot, 900, 176);
        Assert.Equal(8, plot.Rows.Count);
    }

    [AvaloniaFact]
    public void ScrollAndViewportClipShadingToVisibleRowContent()
    {
        var plot = Create(30);
        AssertBands(plot, 300, 276);
        plot.VerticalOffset = 22 * 25 + 11;
        AssertBands(plot, 300, 99);
        plot.VerticalOffset = 22 * 30;
        AssertBands(plot, 300, 0);
    }

    [AvaloniaFact]
    public void RouteGrowthShrinkAndEmptyContentUpdateShading()
    {
        var plot = Create(8);
        AssertBands(plot, 400, 176);
        plot.Rows = Create(12).Rows;
        AssertBands(plot, 400, 264);
        plot.Rows = Create(3).Rows;
        AssertBands(plot, 400, 66);
        plot.Rows = [];
        AssertBands(plot, 400, 0);
    }

    [AvaloniaFact]
    public void SelectionAndMeasuredLossRemainAlignedAfterScrolling()
    {
        var plot = Create(8);
        plot.SelectedHop = 8;
        plot.VerticalOffset = 33;
        var drawing = Record(plot, 400);
        var selection = Assert.Single(Filled(drawing, "#22FFFFFF"));
        Assert.Equal(24 + 7 * 22 - 33, selection.Geometry!.Bounds.Y);
        Assert.Equal(22, selection.Geometry.Bounds.Height);
        var loss = Assert.Single(Filled(drawing, "#AAD44843"));
        Assert.Equal(selection.Geometry.Bounds.Y + 4, loss.Geometry!.Bounds.Y);
        Assert.Equal(14, loss.Geometry.Bounds.Height);
    }

    private static HopLatencyPlot Create(int count) => new()
    {
        FixedMaximumMs = 300,
        Rows = Enumerable.Range(1, count)
            .Select(hop => new HopPlotRow(hop, 10, 20, 30, 25, hop == 8 ? 50 : 0)).ToArray()
    };

    private static void AssertBands(HopLatencyPlot plot, double height, double expectedHeight)
    {
        var drawing = Record(plot, height);
        Assert.Equal(height, Assert.Single(Filled(drawing, "#191A1D")).Geometry!.Bounds.Height);
        foreach (var color in new[] { "#3B5E36", "#685C32", "#653B37" })
        {
            var band = Assert.Single(Filled(drawing, color));
            Assert.Equal(24, band.Geometry!.Bounds.Y);
            Assert.Equal(expectedHeight, band.Geometry.Bounds.Height);
        }
    }

    private static DrawingGroup Record(HopLatencyPlot plot, double height)
    {
        plot.Measure(new Size(600, height));
        plot.Arrange(new Rect(0, 0, 600, height));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) plot.Render(context);
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
        => Geometry(drawing).Where(g => g.Pen is null && g.Brush is ISolidColorBrush brush
            && brush.Color == Color.Parse(color));
}
