namespace TraceScope.Core;

public sealed record TargetHistoryLayout(string Target, int PrimaryHop = 0, bool PrimaryVisible = true,
    IReadOnlyList<int>? PinnedHops = null, IReadOnlyDictionary<int, double>? GraphWeights = null);

/// <summary>Workspace presentation preferences, independent of probe configuration.</summary>
public sealed record HistoryLayoutPreferences
{
    public double PanelFraction { get; init; } = .68;
    public IReadOnlyList<TargetHistoryLayout> Targets { get; init; } = [];

    public void Validate()
    {
        if (!double.IsFinite(PanelFraction) || PanelFraction is < .1 or > .9 || Targets is null || Targets.Count > 500)
            throw new InvalidDataException("Invalid history panel layout.");
        if (Targets.Select(t => t?.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Targets.Count)
            throw new InvalidDataException("History layout targets must be unique.");
        foreach (var target in Targets)
            if (target is null || string.IsNullOrWhiteSpace(target.Target) || target.Target.Length > 253
                || target.PrimaryHop is < 0 or > 255 || target.PinnedHops?.Count > 256 || target.GraphWeights?.Count > 256
                || target.PinnedHops?.Any(h => h is < 0 or > 255) == true
                || target.GraphWeights?.Any(w => w.Key is < 0 or > 255 || !double.IsFinite(w.Value) || w.Value <= 0 || w.Value > 1e6) == true)
                throw new InvalidDataException("Invalid target history layout.");
    }
}
