using Avalonia.Controls;
using Avalonia.Interactivity;
using TraceScope.App.Views;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private ProbeConfigurationWindow? _probeConfigurationWindow;

    private ProbeOptions CurrentProbeConfiguration() => _creatingTarget || _selected is null
        ? _draftOptions : _selected.Session?.Options ?? _selected.Snapshot?.MeasurementOptions ?? _draftOptions;

    private void UpdateProbeSummary()
    {
        if (this.FindControl<TextBlock>("ProbeSummary") is not { } summary) return;
        var options = CurrentProbeConfiguration();
        summary.Text = _selected is { IsHistorical: true } && !_creatingTarget && _selected.Snapshot?.MeasurementOptions is null
            ? "Saved configuration unavailable" : options.Protocol switch
            {
                ProbeProtocol.Icmp => "ICMP",
                ProbeProtocol.Tcp => $"TCP · {options.DestinationPort}",
                _ => options.UdpPortMode == UdpPortMode.Range
                    ? $"UDP · {options.DestinationPort}–{options.UdpEndPort}" : $"UDP · {options.DestinationPort}"
            };
        ConfigureProbeButton.IsEnabled = !_closing && (_creatingTarget || _selected is null || _selected.Session is not null);
    }

    private async void OpenProbeConfiguration(object? sender, RoutedEventArgs e)
    {
        if (_closing || !_creatingTarget && _selected is { IsHistorical: true }) return;
        if (_probeConfigurationWindow is { IsVisible: true }) { _probeConfigurationWindow.Activate(); return; }
        var owner = _selected;
        var creating = _creatingTarget || owner is null;
        _probeConfigurationWindow = new ProbeConfigurationWindow(CurrentProbeConfiguration(),
            creating ? _configuration.LastTcpPort : owner!.TcpPort,
            creating ? _configuration.LastUdpPort : owner!.UdpPort,
            SourceInput.ItemsSource!.Cast<SourceChoice>().Select(s => new ProbeSourceChoice(s.Label, s.Interface, s.Source)).ToArray(),
            _configuration.Profiles, creating ? _configuration.LastUsedProfile : owner!.ProfileName,
            change => CommitProbeConfigurationAsync(change, owner, creating))
        { Title = $"Probe Configuration — {(creating ? "New target" : owner!.Target)}" };
        if (_args.Contains("--capture"))
        {
            if (int.TryParse(ArgumentValue("--capture-probe-width"), out var width))
                _probeConfigurationWindow.Width = Math.Max(_probeConfigurationWindow.MinWidth, width);
            if (int.TryParse(ArgumentValue("--capture-probe-height"), out var height))
                _probeConfigurationWindow.Height = Math.Max(_probeConfigurationWindow.MinHeight, height);
        }
        try { await _probeConfigurationWindow.ShowDialog(this); }
        finally { _probeConfigurationWindow = null; }
    }

    private async Task CommitProbeConfigurationAsync(ProbeConfigurationChange change, TargetItem? owner, bool creating)
    {
        if (_closing || !ReferenceEquals(_selected, owner) || (_creatingTarget || owner is null) != creating)
            throw new InvalidOperationException("The configuration target changed. Reopen Configure Probe.");
        var configuration = _configuration with
        {
            LastUsedProfile = change.ProfileName
        };
        if (creating)
        {
            await CommitSettingsAsync(configuration with
            {
                LastUsedProbeOptions = change.Options, LastTcpPort = change.TcpPort, LastUdpPort = change.UdpPort
            });
            RememberProbeOptions(change.Options, change.ProfileName);
            _newTargetOverride = true;
            _creatingTarget = true;
            SynchronizeProbeControls(change.Options);
        }
        else
        {
            if (owner?.Session is null || !_engine.Sessions.Any(s => s.Id == owner.Id))
                throw new InvalidOperationException("This target is no longer available.");
            var previousProfile = owner.ProfileName;
            owner.ProfileName = change.ProfileName;
            try { await CommitSettingsAsync(configuration, owner.Id, change.Options, change.TcpPort, change.UdpPort); }
            catch { owner.ProfileName = previousProfile; throw; }
        }
        _settingOptions = true;
        try { ProfileInput.SelectedItem = change.ProfileName; }
        finally { _settingOptions = false; }
        UpdateProbeSummary();
        StatusText.Text = "Probe configuration saved · applies next round · existing history preserved";
    }
}
