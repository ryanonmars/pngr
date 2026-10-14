using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private const double MinimumHistoryGraphHeight = 80;
    private const double GraphDividerHeight = 4;
    private double _historyPanelFraction = .68;
    private string? _graphLayoutKey;
    private bool _resetHistoryPanelDrag;

    private void InitializeHistoryLayout()
    {
        AnalysisGrid.RowDefinitions[0].MinHeight = 70;
        AnalysisGrid.RowDefinitions[2].MinHeight = MinimumHistoryGraphHeight + 23;
        HistoryPanelDivider.AddHandler(PointerPressedEvent, ResetHistoryPanelLayout, RoutingStrategies.Tunnel, handledEventsToo: true);
        HistoryPanelDivider.DragCompleted += (_, _) =>
        {
            if (_resetHistoryPanelDrag) { _resetHistoryPanelDrag = false; return; }
            var total = AnalysisGrid.RowDefinitions[0].ActualHeight + AnalysisGrid.RowDefinitions[2].ActualHeight;
            if (total > 0) ApplyHistoryPanelFraction(AnalysisGrid.RowDefinitions[2].ActualHeight / total);
        };
        ApplyHistoryPanelFraction(_configuration.HistoryLayout.PanelFraction);
    }

    private void ApplyHistoryPanelFraction(double fraction)
    {
        _historyPanelFraction = Math.Clamp(double.IsFinite(fraction) ? fraction : .68, .1, .9);
        AnalysisGrid.RowDefinitions[0].Height = new GridLength(1 - _historyPanelFraction, GridUnitType.Star);
        AnalysisGrid.RowDefinitions[2].Height = new GridLength(_historyPanelFraction, GridUnitType.Star);
        RememberHistoryLayout();
    }

    private void ResetHistoryPanelLayout(object? sender, PointerPressedEventArgs? e)
    {
        if (e is not null && e.ClickCount != 2) { _resetHistoryPanelDrag = false; return; }
        ApplyHistoryPanelFraction(.68);
        if (e is not null) { _resetHistoryPanelDrag = true; e.Handled = true; }
    }

    private void RememberHistoryLayout()
    {
        _configuration = _configuration with { HistoryLayout = CaptureHistoryLayout() };
        ScheduleWorkspaceSave();
    }

    private HistoryLayoutPreferences CaptureHistoryLayout()
    {
        var layouts = _configuration.HistoryLayout.Targets.ToDictionary(t => t.Target, StringComparer.OrdinalIgnoreCase);
        foreach (var target in _targets.Where(t => !t.IsHistorical))
        {
            if (!_historyViews.TryGetValue(target.Id, out var view)) continue;
            layouts[target.Target] = new(target.Target, view.PrimaryHop, view.PrimaryVisible,
                view.PinnedHops.Order().ToArray(), new Dictionary<int, double>(view.GraphWeights));
        }
        // Keep preferences bounded as targets are replaced over the lifetime of a workspace.
        return new() { PanelFraction = _historyPanelFraction, Targets = layouts.Values.TakeLast(500).ToArray() };
    }

    private void RestoreTargetHistoryLayout(Guid id, HistoryViewState view)
    {
        var target = _targets.FirstOrDefault(t => t.Id == id);
        if (target is null) return;
        var saved = _configuration.HistoryLayout.Targets.LastOrDefault(t => t.Target.Equals(target.Target, StringComparison.OrdinalIgnoreCase));
        if (saved is null) return;
        view.PrimaryHop = saved.PrimaryHop; view.PrimaryVisible = saved.PrimaryVisible;
        foreach (var hop in saved.PinnedHops ?? []) view.PinnedHops.Add(hop);
        foreach (var (hop, weight) in saved.GraphWeights ?? new Dictionary<int, double>()) view.GraphWeights[hop] = weight;
    }

    private List<(int Hop, Control Frame)> HistoryFrames()
    {
        var frames = new List<(int, Control)>();
        if (PrimaryGraphFrame.IsVisible) frames.Add((CurrentHistoryView.PrimaryHop, PrimaryGraphFrame));
        frames.AddRange(_additionalGraphs.OrderBy(p => p.Key).Select(p => (p.Key, (Control)p.Value.Parent!)));
        return frames;
    }

    private void ArrangeHistoryGraphFrames()
    {
        var frames = HistoryFrames();
        // One shared control set follows the first visible graph, including when
        // the reusable destination graph has been closed.
        var heading = frames.FirstOrDefault().Frame is Grid first ? first.Children[0] as Grid : PrimaryGraphHeadingRow;
        if (heading is not null && !ReferenceEquals(HistoryTimeControls.Parent, heading))
        {
            if (HistoryTimeControls.Parent is Panel old) old.Children.Remove(HistoryTimeControls);
            Grid.SetColumn(HistoryTimeControls, 1);
            heading.Children.Add(HistoryTimeControls);
        }
        var key = $"{_selected?.Id}:{string.Join(',', frames.Select(f => f.Hop))}";
        if (key == _graphLayoutKey) return;
        _graphLayoutKey = key;
        HistoryGraphsPanel.Children.Clear();
        // Keep the named destination control attached even when hidden.
        if (!PrimaryGraphFrame.IsVisible) HistoryGraphsPanel.Children.Add(PrimaryGraphFrame);
        for (var index = 0; index < frames.Count; index++)
        {
            if (index > 0)
            {
                var upper = frames[index - 1].Hop; var lower = frames[index].Hop;
                var divider = new Thumb
                {
                    Height = GraphDividerHeight, Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                    Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brush.Parse("#373A40") }),
                    Focusable = true
                };
                Avalonia.Automation.AutomationProperties.SetName(divider, $"Resize hop {upper} and hop {lower} graphs");
                ToolTip.SetTip(divider, "Drag to resize graphs · Up/Down to adjust");
                divider.DragDelta += (_, e) => ResizeHistoryGraphPair(upper, lower, e.Vector.Y);
                divider.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Up or Key.Down)) return;
                    ResizeHistoryGraphPair(upper, lower, e.Key == Key.Up ? -10 : 10); e.Handled = true;
                };
                HistoryGraphsPanel.Children.Add(divider);
            }
            HistoryGraphsPanel.Children.Add(frames[index].Frame);
        }
    }

    private void SizeHistoryGraphs()
    {
        if (HistoryGraphsScroll is null) return;
        var frames = HistoryFrames();
        if (frames.Count == 0) return;
        var usable = Math.Max(frames.Count * MinimumHistoryGraphHeight,
            HistoryGraphsScroll.Bounds.Height - (frames.Count - 1) * GraphDividerHeight);
        var pending = frames.ToList();
        var remaining = usable;
        // Allocate proportionally, redistributing space when a graph reaches its readable minimum.
        while (pending.Count > 0)
        {
            var sum = pending.Sum(f => CurrentHistoryView.GraphWeights.GetValueOrDefault(f.Hop, 1));
            var constrained = pending.Where(f => remaining * CurrentHistoryView.GraphWeights.GetValueOrDefault(f.Hop, 1) / sum < MinimumHistoryGraphHeight).ToArray();
            if (constrained.Length == 0)
            {
                // Divide in device pixels so layout rounding cannot make the
                // combined frames taller than the available scroll viewport.
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                var allocated = 0d;
                for (var index = 0; index < pending.Count; index++)
                {
                    var frame = pending[index];
                    var height = index == pending.Count - 1 ? remaining - allocated
                        : Math.Floor(remaining * CurrentHistoryView.GraphWeights.GetValueOrDefault(frame.Hop, 1) / sum * scale) / scale;
                    frame.Frame.Height = height;
                    allocated += height;
                }
                break;
            }
            foreach (var frame in constrained)
            {
                frame.Frame.Height = MinimumHistoryGraphHeight;
                remaining -= MinimumHistoryGraphHeight;
                pending.Remove(frame);
            }
        }
    }

    private void ResizeHistoryGraphPair(int upperHop, int lowerHop, double deltaY)
    {
        var frames = HistoryFrames();
        var upper = frames.FirstOrDefault(f => f.Hop == upperHop).Frame;
        var lower = frames.FirstOrDefault(f => f.Hop == lowerHop).Frame;
        if (upper is null || lower is null || !double.IsFinite(deltaY)) return;
        var total = upper.Height + lower.Height;
        var first = Math.Clamp(upper.Height + deltaY, MinimumHistoryGraphHeight, total - MinimumHistoryGraphHeight);
        // Capture all current allocations so resizing one pair leaves the remaining graphs stable.
        var average = frames.Average(f => f.Frame.Height);
        foreach (var frame in frames) CurrentHistoryView.GraphWeights[frame.Hop] = frame.Frame.Height / average;
        CurrentHistoryView.GraphWeights[upperHop] = first / average;
        CurrentHistoryView.GraphWeights[lowerHop] = (total - first) / average;
        SizeHistoryGraphs();
        RememberHistoryLayout();
    }
}
