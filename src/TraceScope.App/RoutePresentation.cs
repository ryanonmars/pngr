using Avalonia.Controls;
using Avalonia.Interactivity;
using TraceScope.Core;

namespace TraceScope.App;

/// <summary>Presentation only: never changes the source snapshots or measurements.</summary>
public sealed record RoutePresentation(IReadOnlyList<HopSnapshot> Rows, int? FirstHiddenHop, int? LastHiddenHop, int UnansweredCount)
{
    public bool HasTrailingGroup => UnansweredCount > 0;

    public static RoutePresentation Create(IReadOnlyList<HopSnapshot> hops, bool expanded)
    {
        var first = hops.Count;
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            var hop = hops[i];
            if (hop.IsDestination || hop.Address is not null || hop.Received > 0 || hop.Sent == 0 || hop.Status != ProbeStatus.Timeout)
                break;
            // Do not summarize gaps or multiple router identities at one position.
            if (i < hops.Count - 1 && hops[i + 1].Hop != hop.Hop + 1) break;
            first = i;
        }
        var count = hops.Count - first;
        if (count < 3) return new(hops, null, null, 0);
        return new(expanded ? hops : hops.Take(first).ToArray(), hops[first].Hop, hops[^1].Hop, count);
    }
}

public partial class MainWindow
{
    private readonly HashSet<Guid> _expandedUnansweredRoutes = [];

    private IReadOnlyList<HopSnapshot> PresentRoute(IReadOnlyList<HopSnapshot> hops)
    {
        var expanded = _selected is not null && _expandedUnansweredRoutes.Contains(_selected.Id);
        var presentation = RoutePresentation.Create(hops, expanded);
        var button = this.FindControl<Button>("UnansweredHopsButton");
        if (button is not null)
        {
            button.IsVisible = presentation.HasTrailingGroup;
            button.Content = presentation.HasTrailingGroup
                ? $"Hops {presentation.FirstHiddenHop}–{presentation.LastHiddenHop} · {presentation.UnansweredCount} unanswered positions · {(expanded ? "Collapse" : "Expand")}" : null;
            ToolTip.SetTip(button, "Attempted TTL positions without replies. Expand to inspect individual measurements.");
        }
        // Graphs and the selected hop continue referencing the complete source snapshot.
        return presentation.Rows;
    }

    private void ClearRoutePresentation()
    {
        if (this.FindControl<Button>("UnansweredHopsButton") is { } button) button.IsVisible = false;
    }

    private void ToggleUnansweredHops(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!_expandedUnansweredRoutes.Add(_selected.Id)) _expandedUnansweredRoutes.Remove(_selected.Id);
        RefreshSelected();
    }
}
