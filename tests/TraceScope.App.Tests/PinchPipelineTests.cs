using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using TraceScope.App.Controls;
using Xunit;

namespace TraceScope.App.Tests;

/// <summary>Routed native magnification plumbing; these do not simulate an actual trackpad.</summary>
public sealed class PinchPipelineTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static PointerDeltaEventArgs Magnify(LatencyTimeline graph, double delta, double x = 250)
        => new(Gestures.PointerTouchPadGestureMagnifyEvent, graph,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), graph,
            new Point(x, 100), 1, new PointerPointProperties(), KeyModifiers.None,
            new Vector(delta, delta));

    [AvaloniaFact]
    public void NativeMagnificationZoomsAroundFocalPositionAndSynchronizesGraphs()
    {
        var viewport = new TimelineViewport
        {
            First = End.AddDays(-2), Last = End, EndTime = End.AddHours(-1),
            Horizon = TimeSpan.FromMinutes(5)
        };
        var first = new LatencyTimeline { Viewport = viewport, Height = 220 };
        var second = new LatencyTimeline { Viewport = viewport, Height = 220 };
        var window = new Window { Width = 650, Height = 460, Content = new StackPanel { Children = { first, second } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var focalTime = first.TimestampAt(250);
            var outward = Magnify(first, .25);
            first.RaiseEvent(outward);
            Assert.True(outward.Handled);
            Assert.Equal(TimeSpan.FromMinutes(4), viewport.Horizon);
            Assert.InRange(Math.Abs((focalTime - first.TimestampAt(250)).Ticks), 0, 1);
            Assert.Equal(first.Horizon, second.Horizon);
            Assert.Equal(first.EndTime, second.EndTime);
            second.RaiseEvent(Magnify(second, -.2));
            Assert.Equal(TimeSpan.FromMinutes(5), viewport.Horizon);
            Assert.Equal(first.Horizon, second.Horizon);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NativeMagnificationUsesExistingZoomLimitsAndPreservesSelection()
    {
        var viewport = new TimelineViewport { First = End.AddDays(-20), Last = End, Horizon = TimeSpan.FromMinutes(5) };
        viewport.SelectRange(End.AddMinutes(-2), End.AddMinutes(-1));
        var graph = new LatencyTimeline { Viewport = viewport };
        graph.Measure(new Size(600, 300));
        graph.Arrange(new Rect(0, 0, 600, 300));
        graph.RaiseEvent(Magnify(graph, 1e6));
        Assert.Equal(TimelineViewport.MinimumHorizon, viewport.Horizon);
        graph.RaiseEvent(Magnify(graph, -.999999999));
        Assert.Equal(TimelineViewport.MaximumHorizon, viewport.Horizon);
        Assert.Equal(End.AddMinutes(-2), viewport.SelectionStart);
        Assert.Equal(End.AddMinutes(-1), viewport.SelectionEnd);
    }

    [AvaloniaFact]
    public void InvalidNativeMagnificationAndEventsOutsideGraphDoNotChangeTimeline()
    {
        var viewport = new TimelineViewport { Last = End, Horizon = TimeSpan.FromMinutes(5) };
        var graph = new LatencyTimeline { Viewport = viewport };
        graph.Measure(new Size(600, 300));
        graph.Arrange(new Rect(0, 0, 600, 300));
        foreach (var value in new[] { 0, -1, -2, double.NaN, double.PositiveInfinity })
        {
            var input = Magnify(graph, value);
            graph.RaiseEvent(input);
            Assert.False(input.Handled);
        }
        var outside = Magnify(graph, .2, -1);
        graph.RaiseEvent(outside);
        Assert.False(outside.Handled);
        Assert.Equal(TimeSpan.FromMinutes(5), viewport.Horizon);
        Assert.Null(viewport.EndTime);
    }
}
