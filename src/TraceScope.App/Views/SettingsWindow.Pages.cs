using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using TraceScope.Core;
using Ui = TraceScope.App.Views.SettingsUi;

namespace TraceScope.App.Views;

public sealed partial class SettingsWindow
{
    private Control? _portField;
    private Control? _udpRangeField;
    private StackPanel? _udpDetails;
    private readonly Expander _probeAdvanced = new() { Name = "SettingsProbeAdvanced", Header = "Advanced probe options", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly RadioButton _graphAutomatic = new() { Name = "SettingsGraphAutomatic", Content = "Automatic", GroupName = "GraphScaling" };
    private readonly RadioButton _graphFixed = new() { Name = "SettingsGraphFixed", Content = "Fixed maximum", GroupName = "GraphScaling" };
    private Control? _graphMaximumField;

    private Control BuildGeneralPage(string directory) => Ui.Page(
        Ui.Section("Startup", _restore), Ui.Divider(),
        Ui.Section("Workspace", ToolButton("Apply & manage workspaces…", "workspace")), Ui.Divider(),
        Ui.Section("Local files", Ui.Location(directory), Note("Settings: workspace.json   ·   History: tracescope.db")));

    private Control BuildMonitoringPage()
    {
        foreach (var combo in new[] { _scope, _source, _profiles })
        { combo.Width = double.NaN; combo.MinWidth = 0; combo.HorizontalAlignment = HorizontalAlignment.Stretch; }
        _scope.ItemTemplate = new FuncDataTemplate<string>((value, _) => ChoiceLabel(value));
        _profiles.ItemTemplate = new FuncDataTemplate<string>((value, _) => ChoiceLabel(value));
        _source.ItemTemplate = new FuncDataTemplate<ProbeSourceChoice>((value, _) => ChoiceLabel(value?.Label));
        _profileName.HorizontalAlignment = HorizontalAlignment.Left;
        _protocol.ItemTemplate = new FuncDataTemplate<ProbeProtocol>((protocol, _) => new TextBlock { Text = protocol.ToString().ToUpperInvariant() });
        _protocol.Width = double.NaN; _protocol.HorizontalAlignment = HorizontalAlignment.Stretch;
        _source.MaxWidth = double.PositiveInfinity;
        _udpFragmentation.Content = "Allow UDP IP fragmentation";
        ToolTip.SetTip(_useDefaults, "Checked: new targets use the saved defaults. Unchecked: new targets inherit the last-used probe configuration. Existing targets retain their own settings.");
        ToolTip.SetTip(_scope, "Changes affect only this scope. Select a running target to edit its options without changing global defaults.");
        ToolTip.SetTip(_port, "TCP destination port, or the starting UDP destination port. ICMP does not use a port.");
        ToolTip.SetTip(_interval, "Minimum time between round starts. Rounds never overlap; a longer round delays the next start.");
        ToolTip.SetTip(_timeout, "Maximum wait for a probe response. The macOS ICMP traceroute tool uses a minimum one-second timeout.");
        ToolTip.SetTip(_udpSize, "Total IP packet size, including IP and UDP headers. IPv4 uses 20 + 8 header bytes; IPv6 uses 40 + 8. No silent truncation.");
        ToolTip.SetTip(_udpFragmentation, "Allow fragmentation of UDP packets; the path MTU can still restrict delivery.");
        ToolTip.SetTip(_memory, "Maximum in-memory samples per hop. Retained SQLite history is independent of this limit.");
        ToolTip.SetTip(_udpMode, "Fixed sends to one port; a range uses ports from the starting port through the inclusive ending port.");
        _portField = Ui.Field("Destination / starting port", _port);
        _udpRangeField = Ui.Field("Ending port (inclusive)", _udpEndPort);
        _udpDetails = new StackPanel { Name = "SettingsUdpDetails", Spacing = 8, Children = {
            Ui.Columns(Ui.Field("UDP ports", _udpMode), _udpRangeField),
            Ui.Field("UDP packet size", _udpSize, "bytes"), _udpFragmentation } };
        _probeAdvanced.Content = Ui.Section("Probe limits", _udpDetails,
            Ui.Columns(Ui.Field("Maximum hops", _hops), Ui.Field("Concurrent hop probes", _concurrency)),
            Ui.Field("In-memory samples per hop", _memory));
        _probeAdvanced.IsExpanded = _protocol.SelectedItem is ProbeProtocol.Udp;
        var profiles = new Expander { Name = "SettingsProfilesAdvanced", Header = "Saved monitoring profiles",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Section("Profiles", Ui.Field("Load profile", _profiles), Ui.Field("Profile name", _profileName),
                Ui.Actions(ProfileButton("Save profile", "SettingsSaveProfile", SaveProfile), ProfileButton("Delete profile", "SettingsDeleteProfile", DeleteProfile)),
                Note("Profile changes are staged until Apply or OK.")) };
        return Ui.Page(Ui.Section("Target configuration", Ui.Field("Configuration scope", _scope), _useDefaults), Ui.Divider(),
            Ui.Section("Probe settings", Ui.Columns(Ui.Field("Protocol", _protocol), _portField),
                Ui.Field("Network source", _source), Ui.Columns(Ui.Field("Probe interval", _interval, "seconds"), Ui.Field("Probe timeout", _timeout, "seconds")), _resolve),
            _probeAdvanced, profiles);
    }

    private Control BuildDisplayPage(DisplayConfiguration display)
    {
        _graphAutomatic.IsChecked = display.FixedMaximumMs == 0;
        _graphFixed.IsChecked = display.FixedMaximumMs != 0;
        _graphMaximumField = Ui.Field("Maximum latency", _maximum, "ms");
        _graphMaximumField.IsVisible = _graphFixed.IsChecked == true;
        void ScalingChanged()
        {
            _graphMaximumField.IsVisible = _graphFixed.IsChecked == true;
            if (_graphFixed.IsChecked == true && _maximum.Value == 0) _maximum.Value = 300;
        }
        _graphAutomatic.IsCheckedChanged += (_, _) => ScalingChanged();
        _graphFixed.IsCheckedChanged += (_, _) => ScalingChanged();
        _maximum.ValueChanged += (_, _) => { if (_maximum.Value > 0) _graphFixed.IsChecked = true; };
        ToolTip.SetTip(_warning, "The visual warning threshold in graphs; it does not change alert rules.");
        ToolTip.SetTip(_critical, "Must exceed the warning threshold. These are display colors, not alert thresholds.");
        ToolTip.SetTip(_graphAutomatic, "Graphs choose their own vertical scale. The stored maximum remains 0.");
        ToolTip.SetTip(_maximum, "Fixed vertical limit for latency graphs. Choose Automatic to let graphs scale themselves.");
        return Ui.Page(Ui.Section("Latency thresholds", Ui.Columns(Ui.Field("Warning threshold", _warning, "ms"), Ui.Field("Critical threshold", _critical, "ms"))),
            Ui.Divider(), Ui.Section("Graph scaling", Ui.Actions(_graphAutomatic, _graphFixed), _graphMaximumField),
            Ui.Divider(), Ui.Section("Graph content", _jitter, _loss, _events),
            Ui.Divider(), Ui.Section("History display", Ui.Field("Default time range", _range)));
    }

    private static TextBlock ChoiceLabel(string? text)
    {
        var label = new TextBlock { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(label, text); return label;
    }

    private double ReadGraphMaximum()
    {
        var value = _maximum.ReadDouble();
        if (value <= 0) throw new InvalidDataException("Maximum latency must be at least 1 ms, or select Automatic scaling.");
        return value;
    }

    private Control BuildHistoryPage(string directory)
    {
        ToolTip.SetTip(_retention, "Raw monitoring history is retained for this many days. Retention runs at startup and every 30 minutes.");
        ToolTip.SetTip(_aggregation, "Groups measurements into time buckets for historical graphs. Raw retained samples and CSV exports remain unchanged.");
        return Ui.Page(Ui.Section("History retention", Ui.Field("Keep monitoring history", _retention, "days")),
            Ui.Divider(), Ui.Section("Historical graphs", Ui.Field("Historical graph aggregation", _aggregation, "seconds")),
            Ui.Divider(), Ui.Section("Data storage", Ui.Location(directory), ToolButton("Apply & open data maintenance…", "workspace")));
    }
}
