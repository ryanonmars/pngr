using Avalonia.Controls;
using Avalonia.Interactivity;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private bool _addingTarget;
    private bool _creatingTarget;
    private bool _newTargetOverride;
    private readonly HashSet<Guid> _monitoringOperations = [];
    private ProbeProtocol _displayedProtocol = ProbeProtocol.Icmp;

    private ProbeOptions NewTargetOptions() => _configuration.UseDefaultProbeOptionsForNewTargets
        ? _configuration.DefaultProbeOptions : _configuration.LastUsedProbeOptions ?? _configuration.DefaultProbeOptions;

    private void NewTargetTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_engine is null) return;
        if (!_addingTarget && !_creatingTarget && !string.IsNullOrWhiteSpace(TargetInput.Text))
        {
            _draftOptions = _selected?.Session?.Options ?? NewTargetOptions();
            _creatingTarget = true;
        }
        UpdateButtons();
    }

    private static void RememberTargetPort(TargetItem item, ProbeOptions options)
    {
        if (options.Protocol == ProbeProtocol.Tcp) item.TcpPort = options.DestinationPort;
        if (options.Protocol == ProbeProtocol.Udp) item.UdpPort = options.DestinationPort;
    }

    private void RememberProbeOptions(ProbeOptions options, string? profile = null)
    {
        _draftOptions = options;
        _configuration = _configuration with
        {
            LastUsedProbeOptions = options,
            LastTcpPort = options.Protocol == ProbeProtocol.Tcp ? options.DestinationPort : _configuration.LastTcpPort,
            LastUdpPort = options.Protocol == ProbeProtocol.Udp ? options.DestinationPort : _configuration.LastUdpPort,
            LastUsedProfile = profile ?? ProfileInput.SelectedItem as string
        };
    }

    private void UpdateProtocolControls()
    {
        _displayedProtocol = ProtocolInput.SelectedItem is ProbeProtocol protocol ? protocol : ProbeProtocol.Icmp;
        PortInput.IsVisible = PortInput.IsEnabled = _displayedProtocol != ProbeProtocol.Icmp;
    }

    private void ProtocolChanged()
    {
        if (_settingOptions) { UpdateProtocolControls(); return; }
        var item = !_creatingTarget ? _selected : null;
        var port = (int)(PortInput.Value ?? 443);
        if (item is not null)
        {
            if (_displayedProtocol == ProbeProtocol.Tcp) item.TcpPort = port;
            if (_displayedProtocol == ProbeProtocol.Udp) item.UdpPort = port;
        }
        var protocol = ProtocolInput.SelectedItem is ProbeProtocol selected ? selected : ProbeProtocol.Icmp;
        _settingOptions = true;
        try
        {
            PortInput.Value = protocol == ProbeProtocol.Udp ? item?.UdpPort ?? _configuration.LastUdpPort
                : protocol == ProbeProtocol.Tcp ? item?.TcpPort ?? _configuration.LastTcpPort : port;
            UpdateProtocolControls();
        }
        finally { _settingOptions = false; }
        ApplySelectedOptions();
    }

    private async void RestartTrace(object? sender, RoutedEventArgs e)
    {
        if (_selected is { Session: not null } item) await RestartTargetAsync(item);
    }

    private async Task RestartTargetAsync(TargetItem item)
    {
        if (item.Session is null || _closing || _closingTargets.Contains(item.Id) || !_monitoringOperations.Add(item.Id)) return;
        UpdateButtons();
        try
        {
            var options = item.Session.Options;
            await _engine.StopAsync(item.Id);
            var session = await _engine.StartTargetAsync(item.Target, options, _applicationCancellation.Token);
            if (_closing) { await _engine.StopAsync(session.Id); return; }
            var replacement = new TargetItem(session)
            { Group = item.Group, TcpPort = item.TcpPort, UdpPort = item.UdpPort, ProfileName = item.ProfileName };
            session.Group = item.Group;
            AssignAlerts(replacement);
            var index = _targets.IndexOf(item);
            var selected = ReferenceEquals(_selected, item);
            _targets[index] = replacement;
            _engine.ForgetSession(item.Id);
            if (selected)
            {
                if (_allTargetsVisible) PreviewOverviewTarget(replacement);
                else TargetList.SelectedItem = replacement;
            }
            if (session.LatestSnapshot is { } snapshot) ReceiveSnapshot(snapshot);
            ScheduleWorkspaceSave();
        }
        catch (Exception ex) { StatusText.Text = $"Could not restart {item.Target}: {ex.Message}"; }
        finally { _monitoringOperations.Remove(item.Id); UpdateButtons(); }
    }

    private void ResetStatistics(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Session is not { } session || _monitoringOperations.Contains(session.Id)) return;
        _engine.ResetStatistics(session.Id);
        _selected.Snapshot = session.LatestSnapshot;
        RefreshSelected();
        StatusText.Text = $"Statistics reset for {session.Target} · saved measurements retained";
    }
}
