using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private bool _allTargetsVisible;
    private bool _previewingOverviewTarget;
    private bool _clearingOverviewTab;

    private void InitializeAllTargets()
    {
        AllTargetsPane.TargetSelected = PreviewOverviewTarget;
        AllTargetsPane.TargetOpened = item => TargetList.SelectedItem = item;
        _targets.CollectionChanged += (_, _) =>
        {
            if (_allTargetsVisible && _selected is not null && !_targets.Contains(_selected))
            {
                _selected = null;
                if (_targets.FirstOrDefault() is { } next) PreviewOverviewTarget(next);
                else RefreshSelected();
            }
            RefreshAllTargets();
        };
        _clock.Tick += (_, _) => RefreshAllTargets();
    }

    private void SetAllTargetsVisible(bool visible)
    {
        _allTargetsVisible = visible;
        AllTargetsPane.IsVisible = visible;
        SingleTargetPane.IsVisible = !visible;
        AllTargetsButton.Background = Brush.Parse(visible ? "#397FF0" : "#30333A");
        AllTargetsButton.Foreground = Brush.Parse(visible ? "#FFFFFF" : "#D5D8DE");
    }

    private void OpenAllTargets(object? sender, RoutedEventArgs e)
    {
        SetAllTargetsVisible(true);
        _selectedHop = null;
        CurrentHistoryView.LoadedKey = null;
        // The overview is a tab, while its selected row supplies the graph/toolbar context.
        _clearingOverviewTab = true;
        try { TargetList.SelectedItem = null; }
        finally { _clearingOverviewTab = false; }
        RefreshSelected();
        RefreshAllTargets();
    }

    private void PreviewOverviewTarget(TargetItem item)
    {
        var heldEnd = CurrentHistoryView.Viewport.EndTime;
        var horizon = CurrentHistoryView.Viewport.Horizon;
        var focus = FocusInput.SelectedIndex;
        var position = HistorySlider.Value;
        _previewingOverviewTarget = true;
        try
        {
            TargetList.SelectedItem = item;
            _clearingOverviewTab = true;
            try { TargetList.SelectedItem = null; }
            finally { _clearingOverviewTab = false; }
        }
        finally { _previewingOverviewTarget = false; }
        _settingFocus = true;
        try { FocusInput.SelectedIndex = focus; }
        finally { _settingFocus = false; }
        HistorySlider.Value = position;
        if (heldEnd.HasValue) CurrentHistoryView.Viewport.EndTime = heldEnd;
        CurrentHistoryView.Viewport.Horizon = horizon;
        _heldEnd = CurrentHistoryView.Viewport.EndTime;
        RefreshGraph();
    }

    private void NewTargetFromOverview(object? sender, RoutedEventArgs e)
    {
        _creatingTarget = true;
        _newTargetOverride = false;
        _draftOptions = NewTargetOptions();
        SynchronizeProbeControls(_draftOptions);
        _settingOptions = true;
        try { ProfileInput.SelectedItem = _configuration.LastUsedProfile; }
        finally { _settingOptions = false; }
        TargetInput.Text = "";
        TargetInput.Focus();
        UpdateButtons();
    }

    private void RefreshAllTargets()
    {
        if (!_allTargetsVisible || AllTargetsPane is null) return;
        foreach (var item in _targets)
            if (item.Session?.LatestSnapshot is { } latest && latest.Round > (item.Snapshot?.Round ?? 0))
            { item.Snapshot = latest; item.Refresh(); }
        var horizon = CurrentHistoryView.Viewport.Horizon;
        var end = CurrentHistoryView.Viewport.SelectionEnd ?? (FocusInput.SelectedIndex == 2 ? _heldEnd ?? Timeline.EndTime ?? DateTimeOffset.UtcNow
            : FocusInput.SelectedIndex == 1 ? _targets.Select(t => t.Snapshot?.Timestamp).Max() ?? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow);
        if (CurrentHistoryView.Viewport.SelectionStart is { } start) horizon = end - start > TimeSpan.Zero ? end - start : TimeSpan.FromTicks(1);
        AllTargetsPane.Refresh(_targets, _selected?.Id, end, horizon, _configuration.Profiles, _configuration.Display);
    }
}
