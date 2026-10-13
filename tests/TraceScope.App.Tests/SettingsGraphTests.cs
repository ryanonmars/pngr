using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TraceScope.App.Controls;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class SettingsGraphTests
{
    [AvaloniaFact]
    public void DefaultGraphPreferencesRetainExistingThresholdsAndVisibleLayers()
    {
        var route = new HopLatencyPlot();
        var timeline = new LatencyTimeline();
        Assert.Equal(100, route.WarningLatencyMs);
        Assert.Equal(200, route.CriticalLatencyMs);
        Assert.Equal(0, route.FixedMaximumMs);
        Assert.Equal(100, timeline.WarningLatencyMs);
        Assert.Equal(200, timeline.CriticalLatencyMs);
        Assert.Equal(0, timeline.FixedMaximumMs);
        Assert.True(timeline.ShowJitter);
        Assert.True(timeline.ShowPacketLoss);
        Assert.True(timeline.ShowEventMarkers);
    }

    [AvaloniaFact]
    public void HiddenTimelineLayersDisappearAndReclaimSpaceWithoutChangingHistoricalPosition()
    {
        var end = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new GraphPoint[]
        {
            new(end.AddSeconds(-3), 15, false, 0, 2),
            new(end.AddSeconds(-2), null, true, 100),
            new(end.AddSeconds(-1), 20, false, 50, 3)
        };
        var markers = new[] { new GraphEvent(end.AddSeconds(-1), "RouteChange", "Route changed") };
        var timeline = new LatencyTimeline
        {
            Samples = samples,
            Markers = markers,
            EndTime = end,
            SelectedTime = end.AddSeconds(-1),
            Horizon = TimeSpan.FromMinutes(5)
        };
        var before = Record(timeline);
        var beforeBand = Assert.Single(Filled(before, "#3B5E36"));
        Assert.Single(Filled(before, "#3D3F40"));
        Assert.Equal(2, Filled(before, "#AED44843").Count());
        Assert.NotEmpty(Stroked(before, "#E7B95D"));

        timeline.ShowJitter = false;
        timeline.ShowPacketLoss = false;
        timeline.ShowEventMarkers = false;
        var after = Record(timeline);
        var afterBand = Assert.Single(Filled(after, "#3B5E36"));
        Assert.Empty(Filled(after, "#3D3F40"));
        Assert.Empty(Filled(after, "#AED44843"));
        Assert.Empty(Stroked(after, "#E7B95D"));
        Assert.Equal(beforeBand.Geometry!.Bounds.Top - 27, afterBand.Geometry!.Bounds.Top);
        Assert.Equal(beforeBand.Geometry.Bounds.Height + 27, afterBand.Geometry.Bounds.Height);
        Assert.Equal(beforeBand.Geometry.Bounds.Width + 24, afterBand.Geometry.Bounds.Width);
        Assert.Same(samples, timeline.Samples);
        Assert.Same(markers, timeline.Markers);
        Assert.Equal(end, timeline.EndTime);
        Assert.Equal(end.AddSeconds(-1), timeline.SelectedTime);
        Assert.Equal(TimeSpan.FromMinutes(5), timeline.Horizon);
    }

    [AvaloniaFact]
    public void ConfiguredThresholdsAndFixedScaleControlBothGraphColorBands()
    {
        var end = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var routeRows = new[] { new HopPlotRow(1, 10, 30, 450, 35, 0) };
        var route = new HopLatencyPlot
        {
            Rows = routeRows,
            SelectedHop = 1,
            WarningLatencyMs = 25,
            CriticalLatencyMs = 75,
            FixedMaximumMs = 100
        };
        var timeline = new LatencyTimeline
        {
            Samples = [new GraphPoint(end, 450, false)],
            EndTime = end,
            WarningLatencyMs = 25,
            CriticalLatencyMs = 75,
            FixedMaximumMs = 100
        };
        var routeAt100 = Assert.Single(Filled(Record(route), "#3B5E36")).Geometry!.Bounds.Width;
        var timelineAt100 = Assert.Single(Filled(Record(timeline), "#3B5E36")).Geometry!.Bounds.Height;
        Assert.NotEmpty(Filled(Record(route), "#653B37"));
        Assert.NotEmpty(Filled(Record(timeline), "#663B37"));

        route.FixedMaximumMs = 200;
        timeline.FixedMaximumMs = 200;
        var routeAt200 = Assert.Single(Filled(Record(route), "#3B5E36")).Geometry!.Bounds.Width;
        var timelineAt200 = Assert.Single(Filled(Record(timeline), "#3B5E36")).Geometry!.Bounds.Height;
        // The route plot includes its six-pixel left margin in the green band.
        Assert.Equal((routeAt100 - 6) / 2, routeAt200 - 6, 5);
        Assert.Equal(timelineAt100 / 2, timelineAt200, 5);
        route.WarningLatencyMs = 50;
        timeline.WarningLatencyMs = 50;
        Assert.Equal(routeAt100, Assert.Single(Filled(Record(route), "#3B5E36")).Geometry!.Bounds.Width, 5);
        Assert.Equal(timelineAt100, Assert.Single(Filled(Record(timeline), "#3B5E36")).Geometry!.Bounds.Height, 5);
        Assert.Same(routeRows, route.Rows);
        Assert.Equal(1, route.SelectedHop);
        Assert.Equal(end, timeline.EndTime);
        Assert.Equal(450, Assert.Single(timeline.Samples).Latency);
    }

    private static DrawingGroup Record(Control control)
    {
        control.Measure(new Size(600, 300));
        control.Arrange(new Rect(0, 0, 600, 300));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) control.Render(context);
        return drawing;
    }

    private static IEnumerable<GeometryDrawing> Geometries(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Geometries(child)) yield return descendant;
    }

    private static IEnumerable<GeometryDrawing> Filled(Drawing drawing, string color)
        => Geometries(drawing).Where(g => g.Pen is null && g.Brush is ISolidColorBrush brush && brush.Color == Color.Parse(color));

    private static IEnumerable<GeometryDrawing> Stroked(Drawing drawing, string color)
        => Geometries(drawing).Where(g => g.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.Parse(color));
}
