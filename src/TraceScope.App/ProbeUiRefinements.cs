using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private static readonly double[] ProbeIntervalSeconds = [.5, 1, 2.5, 5, 10];
    // Present only during the existing options commit, so a popover never overrides
    // another target's packet size when the selection changes.
    private int? _packetSizeCommit;

    private void UpdateProbeDetailsAvailability()
    {
        if (this.FindControl<Button>("ProbeDetailsButton") is { } button)
        {
            button.IsVisible = _displayedProtocol == ProbeProtocol.Udp;
            ToolTip.SetTip(button, $"UDP packet size: {ReadOptions().UdpPacketSize} bytes, including IP and UDP headers.");
        }
    }

    private void OpenProbeDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ReadOptions().Protocol != ProbeProtocol.Udp) return;
        var owner = _selected?.Id;
        var creating = _creatingTarget;
        var flyout = new Flyout();
        var editor = BuildUdpPacketSizeEditor(() =>
        {
            if (_selected?.Id != owner || _creatingTarget != creating)
            {
                flyout.Hide();
                return false;
            }
            return true;
        }, () => flyout.Hide());
        flyout.Content = editor;
        flyout.ShowAt(button);
    }

    private Control BuildUdpPacketSizeEditor(Func<bool> ownerIsCurrent, Action close)
    {
        var size = new NumericUpDown
        {
            Name = "ProbePacketSizeInput", Minimum = 56, Maximum = 65535,
            Value = ReadOptions().UdpPacketSize, FormatString = "0",
            ShowButtonSpinner = false, Width = 100, HorizontalAlignment = HorizontalAlignment.Left
        };
        var message = new TextBlock { Text = "56–65535 bytes, including IP and UDP headers.", FontSize = 11 };
        var apply = new Button { Name = "ApplyProbePacketSize", Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) =>
        {
            if (!ownerIsCurrent()) return;
            if (!ApplyUdpPacketSize(size.Value))
            { message.Text = "Enter a whole number from 56 to 65535."; return; }
            close();
        };
        return new StackPanel
        {
            Spacing = 8, Margin = new Avalonia.Thickness(8),
            Children = { new TextBlock { Text = "UDP packet size", FontWeight = Avalonia.Media.FontWeight.SemiBold }, size, message,
                new TextBlock { Text = "IP headers: 20 bytes (IPv4), 40 bytes (IPv6).", FontSize = 11 }, apply }
        };
    }

    private bool ApplyUdpPacketSize(decimal? value)
    {
        if (value is null || value < 56 || value > 65535 || value != decimal.Truncate(value.Value)
            || ReadOptions().Protocol != ProbeProtocol.Udp) return false;
        var options = ReadOptions() with { UdpPacketSize = (int)value.Value };
        try { (_configuration with { DefaultProbeOptions = options }).Validate(); }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return false; }
        _packetSizeCommit = options.UdpPacketSize;
        try { ApplySelectedOptions(); }
        finally { _packetSizeCommit = null; }
        return true;
    }
}
