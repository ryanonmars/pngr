using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.App.Controls;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class TimelineInteractionTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static TimelineViewport Viewport() => new() { First = End.AddDays(-2), Last = End, Horizon = TimeSpan.FromMinutes(5) };

    [Fact]
    public void PanIsContinuousHeldAndClampedToCapture()
    {
        var viewport = Viewport();
        viewport.Pan(TimeSpan.FromMilliseconds(-1234));
        Assert.Equal(End.AddMilliseconds(-1234), viewport.EndTime);
        viewport.Pan(TimeSpan.FromDays(-10));
        Assert.Equal(viewport.First, viewport.EndTime);
        viewport.Pan(TimeSpan.FromDays(10));
        Assert.Equal(End, viewport.EndTime);
        viewport.GoLive();
        Assert.Null(viewport.EndTime);
    }

    [Fact]
    public void HeldTimestampDoesNotMoveWhenShortCaptureGrowsOrHorizonChanges()
    {
        var viewport = new TimelineViewport { First = End.AddSeconds(-10), Last = End, EndTime = End.AddSeconds(-5) };
        viewport.Last = End.AddSeconds(2);
        viewport.Horizon = TimeSpan.FromMinutes(20);
        Assert.Equal(End.AddSeconds(-5), viewport.EndTime);
    }

    [Fact]
    public void ZoomPreservesPointerTimestampAndEnforcesLimits()
    {
        var viewport = Viewport();
        viewport.EndTime = End.AddHours(-1);
        var anchor = viewport.EffectiveEnd - viewport.Horizon * .7;
        viewport.Zoom(.5, .3);
        Assert.Equal(anchor, viewport.EffectiveEnd - viewport.Horizon * .7);
        viewport.Zoom(.00000001, .5);
        Assert.Equal(TimeSpan.FromSeconds(5), viewport.Horizon);
        viewport.Zoom(1e20, .5);
        Assert.Equal(TimeSpan.FromDays(7), viewport.Horizon);
        viewport.Zoom(double.NaN, .5);
        Assert.Equal(TimeSpan.FromDays(7), viewport.Horizon);
    }

    [Fact]
    public void SelectionSortsClampsAndClearsWithoutChangingZoom()
    {
        var viewport = Viewport();
        viewport.SelectRange(End.AddSeconds(-1.25), End.AddSeconds(-4.75));
        Assert.Equal(End.AddSeconds(-4.75), viewport.SelectionStart);
        Assert.Equal(End.AddSeconds(-1.25), viewport.SelectionEnd);
        Assert.Equal(End, viewport.EndTime);
        viewport.ClearSelection();
        Assert.Null(viewport.SelectionStart);
        Assert.Equal(TimeSpan.FromMinutes(5), viewport.Horizon);
        viewport.SelectRange(End.AddDays(-3), End.AddDays(1));
        Assert.Equal(viewport.First, viewport.SelectionStart);
        Assert.Equal(viewport.Last, viewport.SelectionEnd);
        viewport.GoLive();
        Assert.Null(viewport.SelectionEnd);
        Assert.Null(viewport.EndTime);
    }

    [AvaloniaFact]
    public void ActualControlsSynchronizeWheelZoomAndKeyboardLive()
    {
        var viewport = Viewport();
        var first = new LatencyTimeline { Viewport = viewport, Height = 220 };
        var second = new LatencyTimeline { Viewport = viewport, Height = 220 };
        var window = new Window { Width = 650, Height = 460, Content = new StackPanel { Children = { first, second } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(new Point(300, 120), new Vector(1.125, 0));
            Assert.Equal(End.AddSeconds(-13.5), viewport.EndTime);
            Assert.Equal(first.EndTime, second.EndTime);
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.MouseWheel(new Point(300, 120), new Vector(0, 1), modifier);
            Assert.True(first.Horizon < TimeSpan.FromMinutes(5));
            Assert.Equal(first.Horizon, second.Horizon);
            first.Focus();
            viewport.SelectRange(End.AddMinutes(-2), End.AddMinutes(-1));
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Null(viewport.SelectionStart);
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Assert.Null(viewport.EndTime);
            Assert.Equal(End, first.EndTime);
            Assert.Equal(first.EndTime, second.EndTime);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void VerticalWheelNoiseBubblesAndHeaderDragNavigates()
    {
        var viewport = Viewport();
        var graph = new LatencyTimeline { Viewport = viewport };
        var parent = new Border { Child = graph };
        var wheelBubbled = false;
        parent.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => wheelBubbled = !e.Handled);
        var window = new Window { Width = 600, Height = 300, Content = parent };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(new Point(250, 150), new Vector(.01, 1));
            Assert.True(wheelBubbled);
            Assert.Null(viewport.EndTime);
            window.MouseDown(new Point(250, 15), MouseButton.Left);
            window.MouseMove(new Point(350, 15), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(350, 15), MouseButton.Left);
            Assert.True(viewport.EndTime < End);
            var held = viewport.EndTime;
            window.MouseWheel(new Point(250, 15), new Vector(1, 0));
            Assert.True(viewport.EndTime < held);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ActualPointerDragHoldsTimelineAndDoubleClickDragSelectsTimestamps()
    {
        var viewport = Viewport();
        var graph = new LatencyTimeline { Viewport = viewport };
        var window = new Window { Width = 600, Height = 300, Content = graph };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(new Point(250, 150), MouseButton.Left);
            window.MouseMove(new Point(350, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(350, 150), MouseButton.Left);
            Assert.NotNull(viewport.EndTime);
            Assert.True(viewport.EndTime < End);
            viewport.GoLive();
            window.MouseDown(new Point(300, 150), MouseButton.Left);
            window.MouseUp(new Point(300, 150), MouseButton.Left);
            window.MouseDown(new Point(300, 150), MouseButton.Left);
            window.MouseMove(new Point(400, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(400, 150), MouseButton.Left);
            Assert.NotNull(viewport.SelectionStart);
            Assert.NotNull(viewport.SelectionEnd);
            Assert.True(viewport.SelectionEnd > viewport.SelectionStart);
            Assert.Equal(graph.TimestampAt(300), viewport.SelectionStart);
            Assert.Equal(graph.TimestampAt(400), viewport.SelectionEnd);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectionOverlayUsesTimestampsAcrossResizeAndPreservesMeasuredLoss()
    {
        var viewport = Viewport();
        viewport.SelectRange(End.AddSeconds(-60), End.AddSeconds(-30));
        var graph = new LatencyTimeline
        {
            Viewport = viewport,
            Samples = [new GraphPoint(End.AddSeconds(-45), 15, false, 25, 2, IsAggregated: true, SampleCount: 4)]
        };
        DrawingGroup Render(double width)
        {
            graph.Measure(new Size(width, 300));
            graph.Arrange(new Rect(0, 0, width, 300));
            var drawing = new DrawingGroup();
            using var context = drawing.Open();
            graph.Render(context);
            return drawing;
        }
        IEnumerable<GeometryDrawing> Geometry(Drawing drawing)
        {
            if (drawing is GeometryDrawing geometry) yield return geometry;
            else if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var geometryChild in Geometry(child)) yield return geometryChild;
        }
        GeometryDrawing Fill(Drawing drawing, string color) => Assert.Single(Geometry(drawing), g => g.Pen is null && g.Brush is ISolidColorBrush brush && brush.Color == Color.Parse(color));
        var first = Render(600);
        Assert.Equal(52.8, Fill(first, "#354DA4D8").Geometry!.Bounds.Width, 5);
        Assert.Equal((300 - 43 - 30) * .25, Fill(first, "#AED44843").Geometry!.Bounds.Height, 5);
        var resized = Render(1000);
        Assert.Equal(92.8, Fill(resized, "#354DA4D8").Geometry!.Bounds.Width, 5);
        Assert.Equal(End.AddSeconds(-60), viewport.SelectionStart);
        Assert.Equal(End.AddSeconds(-30), viewport.SelectionEnd);
    }

    [AvaloniaFact]
    public void LatencyAndJitterLinesDoNotJoinDifferentHopAddresses()
    {
        var graph = new LatencyTimeline
        {
            EndTime = End,
            Horizon = TimeSpan.FromSeconds(10),
            Samples = [
                new GraphPoint(End.AddSeconds(-4), 10, false, JitterMs: 2, Address: "10.0.0.1"),
                new GraphPoint(End.AddSeconds(-3), 20, false, JitterMs: 3, Address: "10.0.0.2")]
        };
        graph.Measure(new Size(600, 300));
        graph.Arrange(new Rect(0, 0, 600, 300));
        IEnumerable<GeometryDrawing> Geometry(Drawing drawing)
        {
            if (drawing is GeometryDrawing geometry) yield return geometry;
            else if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var item in Geometry(child)) yield return item;
        }
        DrawingGroup Render()
        {
            var drawing = new DrawingGroup();
            using var context = drawing.Open();
            graph.Render(context);
            return drawing;
        }
        Assert.DoesNotContain(Geometry(Render()), g => g.Pen?.Brush is ISolidColorBrush brush
            && (brush.Color == Color.Parse("#E1E6D9") || brush.Color == Color.Parse("#CD9476")));
        graph.Samples = graph.Samples.Select(p => p with { Address = "10.0.0.1" }).ToArray();
        Assert.Equal(4, Geometry(Render()).Count(g => g.Pen?.Brush is ISolidColorBrush brush
            && (brush.Color == Color.Parse("#E1E6D9") || brush.Color == Color.Parse("#CD9476"))));
    }

    [AvaloniaFact]
    public void CoordinateConversionRetainsSubsecondPrecisionAndViewportAcrossSamples()
    {
        var viewport = Viewport();
        var graph = new LatencyTimeline { Viewport = viewport };
        graph.Measure(new Size(600, 300));
        graph.Arrange(new Rect(0, 0, 600, 300));
        Assert.Equal(End.AddSeconds(-150), graph.TimestampAt(310));
        viewport.SelectRange(End.AddSeconds(-10.123), End.AddSeconds(-5.456));
        graph.Samples = [new GraphPoint(End, 12, false)];
        Assert.Equal(End.AddSeconds(-10.123), viewport.SelectionStart);
        Assert.Equal(End.AddSeconds(-5.456), viewport.SelectionEnd);
        Assert.Equal(End.ToLocalTime().ToString("HH:mm:ss"), LatencyTimeline.FormatTimestamp(End, TimeSpan.FromSeconds(10)));
    }
}
