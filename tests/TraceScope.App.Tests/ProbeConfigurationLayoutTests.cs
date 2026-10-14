using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProbeConfigurationLayoutTests
{
    [AvaloniaTheory]
    [InlineData(620, 650)]
    [InlineData(520, 500)]
    public void UdpInputsAndActionsFitNormalAndReducedModal(double width, double height)
    {
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, UdpPortMode = UdpPortMode.Range };
        var dialog = new ProbeConfigurationWindow(options, 443, 33434,
            [new ProbeSourceChoice("System route", null, null)], [], null, _ => Task.CompletedTask)
        { Width = width, Height = height };
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            T Find<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            foreach (var name in new[] { "ProbeUdpPort", "ProbeUdpEndPort", "ProbeUdpSize", "ProbeTimeout" })
            {
                var input = Find<TextBox>(name);
                Assert.True(input.IsVisible);
                Assert.Equal(13, input.FontSize);
                Assert.Equal(VerticalAlignment.Center, input.VerticalContentAlignment);
                var text = new FormattedText(input.Text!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(input.FontFamily), input.FontSize, Brushes.White);
                Assert.True(input.Bounds.Width - input.Padding.Left - input.Padding.Right > text.Width);
                var point = input.TranslatePoint(default, dialog)!.Value;
                Assert.True(point.X + input.Bounds.Width <= dialog.Bounds.Width);
            }
            foreach (var name in new[] { "ProbeApply", "ProbeSave", "ProbeCancel" })
            {
                var button = Find<Button>(name);
                var point = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(point.X >= 0 && point.X + button.Bounds.Width <= dialog.Bounds.Width);
                Assert.True(point.Y >= 0 && point.Y + button.Bounds.Height <= dialog.Bounds.Height);
            }
            Assert.False(Find<TextBox>("ProbeTcpPort").IsVisible);
        }
        finally { dialog.Close(); }
    }
}
