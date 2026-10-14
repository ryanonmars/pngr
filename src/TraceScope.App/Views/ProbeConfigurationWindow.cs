using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed record ProbeConfigurationChange(ProbeOptions Options, int TcpPort, int UdpPort,
    string? ProfileName, IReadOnlyList<MonitoringProfile> Profiles);

/// <summary>A private per-target draft; only Apply and Save invoke the owner's configuration update.</summary>
public sealed class ProbeConfigurationWindow : Window
{
    private ProbeOptions _basis;
    private readonly Func<ProbeConfigurationChange, Task> _apply;
    private readonly IReadOnlyList<MonitoringProfile> _profiles;
    private readonly StackPanel _body = new() { Spacing = 8, Margin = new Thickness(22, 16) };
    private readonly StackPanel _footer = new() { Spacing = 10, Margin = new Thickness(22, 12, 22, 18) };
    private bool _resizeQueued;
    private bool _loading, _busy;
    private string? _profile;
    private readonly ComboBox _protocol = Choice("ProbeProtocol", Enum.GetValues<ProbeProtocol>());
    private readonly TextBox _tcpPort = Input("ProbeTcpPort");
    private readonly TextBox _udpPort = Input("ProbeUdpPort");
    private readonly ComboBox _udpMode = Choice("ProbeUdpMode", Enum.GetValues<UdpPortMode>());
    private readonly TextBox _endPort = Input("ProbeUdpEndPort");
    private readonly TextBox _size = Input("ProbeUdpSize");
    private readonly CheckBox _fragmentation = new() { Name = "ProbeFragmentation", Content = "Allow IP fragmentation", FontSize = 13 };
    private readonly ComboBox _source = new() { Name = "ProbeSource", MinWidth = 140, MaxWidth = 320, FontSize = 13, MinHeight = 32 };
    private readonly TextBox _timeout = Input("ProbeTimeout");
    private readonly TextBox _hops = Input("ProbeMaxHops");
    private readonly TextBox _concurrency = Input("ProbeConcurrency");
    private readonly CheckBox _resolve = new() { Name = "ProbeResolve", Content = "Resolve router hostnames", FontSize = 13 };
    private readonly TextBlock _status = Note("");
    private readonly Button _applyButton = Button("ProbeApply", "Apply");
    private readonly Button _saveButton = Button("ProbeSave", "Save");
    private readonly Button _cancelButton = Button("ProbeCancel", "Cancel");
    private readonly List<(Control Container, Control Input, Func<bool> Show)> _conditional = [];

    public ProbeConfigurationWindow(ProbeOptions options, int tcpPort, int udpPort,
        IReadOnlyList<ProbeSourceChoice> sources, IReadOnlyList<MonitoringProfile> profiles,
        string? profileName, Func<ProbeConfigurationChange, Task> apply)
    {
        _basis = options; _apply = apply; _profiles = profiles.ToArray(); _profile = profileName;
        _protocol.ItemTemplate = new FuncDataTemplate<ProbeProtocol>((value, _) => new TextBlock { Text = value.ToString().ToUpperInvariant(), FontSize = 13 });
        _udpMode.ItemTemplate = new FuncDataTemplate<UdpPortMode>((value, _) => new TextBlock { Text = value == UdpPortMode.Range ? "Port range" : "Fixed port", FontSize = 13 });
        Title = "Probe Configuration"; Width = 560; MinWidth = 500; MinHeight = 280;
        FontSize = 13; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _source.ItemsSource = sources.ToList(); _status.Name = "ProbeStatus";
        _tcpPort.Text = tcpPort.ToString(CultureInfo.InvariantCulture);
        _udpPort.Text = udpPort.ToString(CultureInfo.InvariantCulture);
        ToolTip.SetTip(_concurrency, "Maximum simultaneous outstanding probes; higher values may speed discovery.");
        ToolTip.SetTip(_udpMode, "Use one fixed destination port or cycle sequentially through a range. Different ports may affect routing or load balancing.");
        ToolTip.SetTip(_size, "Total packet size, including IP and UDP headers. Larger packets may encounter MTU limitations.");
        ToolTip.SetTip(_fragmentation, "Permits IPv4 fragmentation where supported; this may affect probe behavior.");
        ToolTip.SetTip(_tcpPort, "TCP destination port, from 1 to 65535.");
        ToolTip.SetTip(_udpPort, "UDP destination port, or the first port in the sequential range, from 1 to 65535.");
        ToolTip.SetTip(_endPort, "Last UDP destination port in the range, included in the sequence.");
        ToolTip.SetTip(_timeout, "Maximum wait for a qualifying probe response, in seconds.");
        ToolTip.SetTip(_source, "Use the system-selected route or an available network interface/source address.");
        ToolTip.SetTip(_hops, "Maximum TTL/hop-limit positions to probe; unanswered positions are not confirmed routers.");
        ToolTip.SetTip(_resolve, "Look up names for responding router addresses.");
        var body = _body;
        body.Children.Add(Row("Protocol", _protocol));
        AddConditional(body, "TCP destination port", _tcpPort, () => Protocol == ProbeProtocol.Tcp);
        AddConditional(body, "UDP port mode", _udpMode, () => Protocol == ProbeProtocol.Udp);
        AddConditional(body, "UDP destination / start port", _udpPort, () => Protocol == ProbeProtocol.Udp);
        AddConditional(body, "Ending port (inclusive)", _endPort, () => Protocol == ProbeProtocol.Udp && _udpMode.SelectedItem is UdpPortMode.Range);
        AddConditional(body, "UDP packet size (bytes)", _size, () => Protocol == ProbeProtocol.Udp);
        AddConditional(body, null, Note("56–65535 bytes, including IP and UDP headers."), () => Protocol == ProbeProtocol.Udp);
        AddConditional(body, null, _fragmentation, () => Protocol == ProbeProtocol.Udp);
        body.Children.Add(Row("Network source", _source));
        body.Children.Add(Row("Timeout (seconds)", _timeout));
        body.Children.Add(Row("Maximum hops", _hops));
        body.Children.Add(Row("Concurrent probes", _concurrency));
        body.Children.Add(_resolve);
        AddConditional(body, null, Note("macOS ICMP requires a timeout of at least 1 second."), () => OperatingSystem.IsMacOS() && Protocol == ProbeProtocol.Icmp);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = Brush.Parse("#191A1D") };
        root.Children.Add(new ScrollViewer { Name = "ProbeBodyScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = _footer;
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(_cancelButton); actions.Children.Add(_applyButton); actions.Children.Add(_saveButton); _saveButton.Classes.Add("primary");
        footer.Children.Add(actions); Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _protocol.SelectionChanged += (_, _) => { if (!_loading) { _profile = null; UpdateVisibility(); } };
        _udpMode.SelectionChanged += (_, _) => { if (!_loading) { _profile = null; UpdateVisibility(); } };
        foreach (var input in new[] { _tcpPort, _udpPort, _endPort, _size, _timeout, _hops, _concurrency })
            input.TextChanged += (_, _) => { if (!_loading) _profile = null; };
        _source.SelectionChanged += (_, _) => { if (!_loading) _profile = null; };
        _fragmentation.IsCheckedChanged += (_, _) => { if (!_loading) _profile = null; };
        _resolve.IsCheckedChanged += (_, _) => { if (!_loading) _profile = null; };
        _cancelButton.Click += (_, _) => Close();
        _applyButton.Click += async (_, _) => await ApplyAsync();
        _saveButton.Click += async (_, _) => await ApplyAsync(true);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) { Close(); e.Handled = true; } };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _status.IsVisible = false;
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) { _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text); QueueFitContents(); }
        };
        Opened += (_, _) => QueueFitContents();
        Load(options);
    }

    private ProbeProtocol Protocol => _protocol.SelectedItem is ProbeProtocol p ? p : throw new InvalidDataException("Select a protocol.");
    private void Load(ProbeOptions options)
    {
        _loading = true;
        try
        {
            _basis = options; _protocol.SelectedItem = options.Protocol;
            if (options.Protocol == ProbeProtocol.Tcp) _tcpPort.Text = options.DestinationPort.ToString(CultureInfo.InvariantCulture);
            if (options.Protocol == ProbeProtocol.Udp) _udpPort.Text = options.DestinationPort.ToString(CultureInfo.InvariantCulture);
            _udpMode.SelectedItem = options.UdpPortMode; _endPort.Text = options.UdpEndPort.ToString(CultureInfo.InvariantCulture);
            _size.Text = options.UdpPacketSize.ToString(CultureInfo.InvariantCulture); _fragmentation.IsChecked = options.UdpAllowFragmentation;
            _timeout.Text = options.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            _hops.Text = options.MaxHops.ToString(CultureInfo.InvariantCulture); _concurrency.Text = options.Concurrency.ToString(CultureInfo.InvariantCulture);
            _resolve.IsChecked = options.ResolveHostnames;
            var sources = _source.ItemsSource!.Cast<ProbeSourceChoice>().ToList();
            var choice = sources.FirstOrDefault(s => s.Interface == options.InterfaceId && s.Address == options.SourceAddress);
            if (choice is null) { choice = new ProbeSourceChoice($"Saved source (unavailable): {options.SourceAddress ?? options.InterfaceId ?? "system route"}", options.InterfaceId, options.SourceAddress); sources.Add(choice); _source.ItemsSource = sources; }
            _source.SelectedItem = choice;
        }
        finally { _loading = false; UpdateVisibility(); }
    }
    private ProbeConfigurationChange Read()
    {
        var protocol = Protocol;
        var tcp = Integer(_tcpPort, "TCP port", 1, 65535); var udp = Integer(_udpPort, "UDP port", 1, 65535);
        var mode = _udpMode.SelectedItem is UdpPortMode m ? m : throw new InvalidDataException("Select a UDP port mode.");
        var end = Integer(_endPort, "UDP ending port", 1, 65535);
        if (protocol == ProbeProtocol.Udp && mode == UdpPortMode.Range && end < udp) throw new InvalidDataException("UDP ending port must not be below the starting port.");
        var timeout = Number(_timeout, "Timeout", .1, 30);
        if (OperatingSystem.IsMacOS() && protocol == ProbeProtocol.Icmp && timeout < 1) throw new InvalidDataException("macOS ICMP requires a timeout of at least 1 second.");
        var source = _source.SelectedItem as ProbeSourceChoice ?? throw new InvalidDataException("Select a network source.");
        var options = _basis with { Protocol = protocol, DestinationPort = protocol == ProbeProtocol.Tcp ? tcp : protocol == ProbeProtocol.Udp ? udp : _basis.DestinationPort,
            UdpPortMode = mode, UdpEndPort = end, UdpPacketSize = Integer(_size, "UDP packet size", 56, 65535), UdpAllowFragmentation = _fragmentation.IsChecked == true,
            SourceAddress = source.Address, InterfaceId = source.Interface, Timeout = TimeSpan.FromSeconds(timeout), MaxHops = Integer(_hops, "Maximum hops", 1, 64),
            Concurrency = Integer(_concurrency, "Concurrent probes", 1, 32), ResolveHostnames = _resolve.IsChecked == true };
        new WorkspaceConfiguration { DefaultProbeOptions = options, Profiles = _profiles.ToArray() }.Validate();
        return new ProbeConfigurationChange(options, tcp, udp, _profile, _profiles.ToArray());
    }
    public async Task<bool> ApplyAsync(bool close = false)
    {
        if (_busy) return false;
        try
        {
            var change = Read(); _busy = true; SetBusy(true); await _apply(change);
            _basis = change.Options; _status.Text = "Probe configuration saved."; _busy = false;
            if (close) Close(); return true;
        }
        catch (Exception ex) { _status.Text = ex.Message; return false; }
        finally { _busy = false; SetBusy(false); }
    }
    private void SetBusy(bool busy)
    { _applyButton.IsEnabled = _saveButton.IsEnabled = _cancelButton.IsEnabled = !busy; if (Content is Grid grid && grid.Children[0] is ScrollViewer body) body.IsEnabled = !busy; }
    private void AddConditional(StackPanel body, string? label, Control input, Func<bool> show)
    { var container = label is null ? input : Row(label, input); _conditional.Add((container, input, show)); body.Children.Add(container); }
    private void UpdateVisibility()
    {
        foreach (var (container, input, show) in _conditional) { var visible = show(); container.IsVisible = input.IsVisible = visible; }
        QueueFitContents();
    }
    private void QueueFitContents()
    {
        if (_resizeQueued) return;
        _resizeQueued = true;
        Dispatcher.UIThread.Post(() => { _resizeQueued = false; FitContents(); }, DispatcherPriority.Loaded);
    }
    private void FitContents()
    {
        if (!IsVisible) return;
        // Measure the visible rows, not a fixed protocol-specific height. The scroll viewer
        // becomes a fallback only when the current display cannot accommodate the content.
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var availableHeight = screen is null ? 900 : screen.WorkingArea.Height / screen.Scaling - 64;
        var availableWidth = screen is null ? 1000 : screen.WorkingArea.Width / screen.Scaling - 48;
        MaxHeight = Math.Max(180, availableHeight);
        MaxWidth = Math.Max(300, availableWidth);
        MinHeight = Math.Min(280, MaxHeight);
        MinWidth = Math.Min(500, MaxWidth);
        Width = Math.Clamp(double.IsFinite(Width) ? Width : 560, MinWidth, MaxWidth);
        _body.Measure(new Size(Width, double.PositiveInfinity));
        _footer.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Clamp(Math.Ceiling(_body.DesiredSize.Height + _footer.DesiredSize.Height), MinHeight, MaxHeight);
    }
    private static TextBox Input(string name) => new() { Name = name, Width = 140, MinHeight = 32, FontSize = 13, Padding = new Thickness(8, 5), VerticalContentAlignment = VerticalAlignment.Center };
    private static ComboBox Choice<T>(string name, IEnumerable<T> values) => new() { Name = name, ItemsSource = values, Width = 140, MinHeight = 32, FontSize = 13, Padding = new Thickness(8, 4) };
    private static Button Button(string name, string label) => new() { Name = name, Content = label, MinWidth = 78, MinHeight = 32, FontSize = 13, Padding = new Thickness(12, 5) };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#ADB2BC"), FontSize = 12 };
    private static Grid Row(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        Grid.SetColumn(control, 1); control.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(control); return row;
    }
    private static int Integer(TextBox input, string label, int min, int max)
    { if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max) throw new InvalidDataException($"{label} must be a whole number from {min} to {max}."); return value; }
    private static double Number(TextBox input, string label, double min, double max)
    { if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < min || value > max) throw new InvalidDataException($"{label} must be between {min} and {max}."); return value; }
}
