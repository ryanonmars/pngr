using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class ProbeConfigurationTooltipTests
{
    [AvaloniaFact]
    public void ProbeControlsExplainConcurrencyAndUdpSemanticsWithoutChangingDraft()
    {
        var options = new ProbeOptions { Protocol = ProbeProtocol.Udp, DestinationPort = 33434, UdpPortMode = UdpPortMode.Range };
        var applies = 0;
        var dialog = new ProbeConfigurationWindow(options, 443, 33434,
            [new ProbeSourceChoice("System route", null, null)], [], null, _ => { applies++; return Task.CompletedTask; });
        dialog.Show();
        try
        {
            Control Find(string name) => dialog.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
            var expected = new Dictionary<string, string>
            {
                ["ProbeConcurrency"] = "Maximum simultaneous outstanding probes; higher values may speed discovery.",
                ["ProbeUdpMode"] = "Use one fixed destination port or cycle sequentially through a range. Different ports may affect routing or load balancing.",
                ["ProbeUdpSize"] = "Total packet size, including IP and UDP headers. Larger packets may encounter MTU limitations.",
                ["ProbeFragmentation"] = "Permits IPv4 fragmentation where supported; this may affect probe behavior."
            };
            foreach (var (name, text) in expected) Assert.Equal(text, Assert.IsType<string>(ToolTip.GetTip(Find(name))));
            foreach (var name in new[] { "ProbeUdpPort", "ProbeUdpEndPort", "ProbeTimeout", "ProbeSource", "ProbeMaxHops", "ProbeResolve" })
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(ToolTip.GetTip(Find(name)))));
            Assert.Equal("33434", Assert.IsType<TextBox>(Find("ProbeUdpPort")).Text);
            Assert.Equal(options.Protocol, Assert.IsType<ComboBox>(Find("ProbeProtocol")).SelectedItem);
            Assert.Equal(0, applies);
        }
        finally { dialog.Close(); }
    }
}
