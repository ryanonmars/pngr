using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WordmarkTests
{
    [AvaloniaFact]
    public void WordmarkUsesCyanAndWhiteVectorsAndFitsAtDifferentSizes()
    {
        Assert.True(Application.Current!.Resources.TryGetResource("PngrWordmark", null, out var resource));
        var image = Assert.IsType<DrawingImage>(resource);
        var drawing = Assert.IsType<DrawingGroup>(image.Drawing);
        var paths = drawing.Children.Cast<GeometryDrawing>().ToArray();
        Assert.Equal(4, paths.Length);
        Assert.Equal(Color.Parse("#1b95ff"), Assert.IsAssignableFrom<ISolidColorBrush>(paths[0].Brush).Color);
        Assert.All(paths.Skip(1), path => Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(path.Brush).Color));
        Assert.All(paths, path => Assert.Null(path.Pen));
        Assert.InRange(image.Size.AspectRatio, 2.8, 3.0);
        foreach (var width in new[] { 50d, 150d, 300d })
        {
            var control = new Image { Source = image, Stretch = Stretch.Uniform };
            var available = new Size(width, width / image.Size.AspectRatio);
            control.Measure(available);
            control.Arrange(new Rect(available));
            Assert.Equal(width, control.Bounds.Width, 4);
            Assert.Equal(available.Height, control.Bounds.Height, 4);
        }
        Assert.True(AssetLoader.Exists(new Uri("avares://pngr/Assets/Branding/Pngr-Font.svg")));
        Assert.True(AssetLoader.Exists(new Uri("avares://pngr/Assets/Icons/pngr.png")));
    }
}
