using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProbeCleanupTests
{
    [AvaloniaFact]
    public async Task ProtocolsFitWithoutScrollingAndHaveNoProfileOrRedundantHeadingControls()
    {
        var dialog = new ProbeConfigurationWindow(new ProbeOptions(), 443, 33434,
            [new ProbeSourceChoice("System route", null, null)], [], null, _ => Task.CompletedTask);
        dialog.Show();
        try
        {
            async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); dialog.UpdateLayout(); }
            await Layout();
            var children = dialog.GetVisualDescendants().OfType<Control>().ToArray();
            Assert.DoesNotContain(children, c => c.Name is "ProbeProfiles" or "ProbeProfileName" or "ProbeSaveProfile" or "ProbeDeleteProfile");
            Assert.DoesNotContain(children.OfType<TextBlock>(), c => string.Equals(c.Text, "Probe configuration", StringComparison.OrdinalIgnoreCase));
            var scroll = children.OfType<ScrollViewer>().Single(c => c.Name == "ProbeBodyScroll");
            var protocol = children.OfType<ComboBox>().Single(c => c.Name == "ProbeProtocol");
            var icmpHeight = dialog.Height;
            Assert.True(icmpHeight < 420);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
            protocol.SelectedItem = ProbeProtocol.Tcp;
            await Layout();
            var tcpHeight = dialog.Height;
            Assert.InRange(tcpHeight, icmpHeight, 440);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
            protocol.SelectedItem = ProbeProtocol.Udp;
            children.OfType<ComboBox>().Single(c => c.Name == "ProbeUdpMode").SelectedItem = UdpPortMode.Range;
            await Layout();
            Assert.True(dialog.Height > tcpHeight + 100);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
            dialog.Height = 320;
            await Layout();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var save = children.OfType<Button>().Single(c => c.Name == "ProbeSave");
            var point = save.TranslatePoint(default, dialog)!.Value;
            Assert.True(point.Y + save.Bounds.Height <= dialog.Bounds.Height);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void NativeMenuModelContainsToolsProfilesOperations()
    {
        var menu = (NativeMenu)typeof(App).GetMethod("CreateWindowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Application.Current, [])!;
        var tools = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Tools").Menu!;
        var profiles = tools.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Profiles").Menu!;
        Assert.Equal(new[] { "Load Profile…", "Save Current Configuration…", "Manage/Delete Profiles…" },
            profiles.Items.OfType<NativeMenuItem>().Select(i => i.Header));
    }
}
