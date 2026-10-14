using Avalonia;
using TraceScope.App.Controls;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WindowPlacementTests
{
    private static readonly WindowDisplay Primary = new(new PixelRect(0, 25, 1920, 1055), 1, true);

    [Fact]
    public void FirstLaunchCentersLargerDefaultWindow()
    {
        var result = WindowPlacementCalculator.Calculate(null, [Primary]);
        Assert.Equal(1200, result.Width);
        Assert.Equal(800, result.Height);
        Assert.Equal(360, result.X);
        Assert.Equal(136, result.Y);
        Assert.Equal(980, result.MinWidth);
        Assert.Equal(640, result.MinHeight);
    }

    [Fact]
    public void ExistingSizeAndPositionRestoreOnSameDisplay()
    {
        var saved = new MainWindowLayout { Width = 1300, Height = 850, X = 100, Y = 75 };
        var result = WindowPlacementCalculator.Calculate(saved, [Primary]);
        Assert.Equal(1300, result.Width);
        Assert.Equal(850, result.Height);
        Assert.Equal(100, result.X);
        Assert.Equal(75, result.Y);
    }

    [Fact]
    public void NegativeMonitorCoordinatesRemainValid()
    {
        var secondary = new WindowDisplay(new PixelRect(-1920, 0, 1920, 1080), 1, false);
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { X = -1800, Y = 50 }, [Primary, secondary]);
        Assert.Equal(-1800, result.X);
        Assert.Equal(50, result.Y);
        Assert.Equal(secondary.WorkingArea, result.WorkingArea);
    }

    [Fact]
    public void DisconnectedMonitorFallsBackToPrimary()
    {
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { X = -1800, Y = 50 }, [Primary]);
        Assert.Equal(360, result.X);
        Assert.Equal(136, result.Y);
    }

    [Fact]
    public void ChangedResolutionKeepsCompleteWindowOnScreen()
    {
        var screen = new WindowDisplay(new PixelRect(0, 25, 1366, 743), 1, true);
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { Width = 1600, Height = 900, X = 1200, Y = 600 }, [screen]);
        Assert.Equal(1366, result.Width);
        Assert.Equal(711, result.Height);
        Assert.Equal(0, result.X);
        Assert.Equal(25, result.Y);
    }

    [Fact]
    public void SmallerDisplayReducesMinimumsInsteadOfOpeningOffScreen()
    {
        var result = WindowPlacementCalculator.Calculate(null, [new WindowDisplay(new PixelRect(0, 0, 800, 600), 1, true)]);
        Assert.Equal(800, result.Width);
        Assert.Equal(568, result.Height);
        Assert.Equal(800, result.MinWidth);
        Assert.Equal(568, result.MinHeight);
        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
    }

    [Fact]
    public void LogicalDimensionsRespectRetinaWorkingArea()
    {
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { Scaling = 1, X = 1800, Y = 1000 },
            [new WindowDisplay(new PixelRect(0, 50, 2560, 1550), 2, true)]);
        Assert.Equal(1200, result.Width);
        Assert.Equal(743, result.Height);
        Assert.Equal(160, result.X);
        Assert.Equal(50, result.Y);
        Assert.Equal(2, result.Scaling);
    }

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(0, -1)]
    public void CorruptDimensionsUseDefaults(double width, double height)
    {
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { Width = width, Height = height }, [Primary]);
        Assert.Equal(1200, result.Width);
        Assert.Equal(800, result.Height);
    }

    [Fact]
    public void PositionExactlyOutsideMonitorDoesNotRestoreOffScreen()
    {
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { X = 1920, Y = 100 }, [Primary]);
        Assert.Equal(360, result.X);
    }

    [Fact]
    public void UndersizedSavedDimensionsRespectCurrentMinimums()
    {
        var result = WindowPlacementCalculator.Calculate(new MainWindowLayout { Width = 100, Height = 100 }, [Primary]);
        Assert.Equal(980, result.Width);
        Assert.Equal(640, result.Height);
    }

    [Fact]
    public void MissingScreenInformationHasSafeFallback()
    {
        var result = WindowPlacementCalculator.Calculate(null, []);
        Assert.Equal(1200, result.Width);
        Assert.Equal(800, result.Height);
        Assert.Equal(40, result.X);
        Assert.Equal(34, result.Y);
    }
}
