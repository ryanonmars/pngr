namespace TraceScope.Core;

/// <summary>Confirms changed responding routers over two rounds; silence never replaces a known route.</summary>
public sealed class RouteTracker
{
    private readonly Dictionary<int, string> _known = [];
    private readonly Dictionary<int, (string Address, int Count)> _pending = [];

    public string? Observe(IReadOnlyList<HopSnapshot> hops)
    {
        var changes = new List<string>();
        foreach (var hop in hops)
        {
            if (hop.Address is not { } address || hop.Status is not (ProbeStatus.Success or ProbeStatus.TimeExceeded))
                continue;
            if (!_known.TryGetValue(hop.Hop, out var previous)) { _known[hop.Hop] = address; continue; }
            if (address == previous) { _pending.Remove(hop.Hop); continue; }
            var candidate = _pending.GetValueOrDefault(hop.Hop);
            var count = candidate.Address == address ? candidate.Count + 1 : 1;
            if (count < 2) { _pending[hop.Hop] = (address, count); continue; }
            changes.Add($"Hop {hop.Hop}: {previous} → {address}");
            _known[hop.Hop] = address;
            _pending.Remove(hop.Hop);
        }
        return changes.Count == 0 ? null : string.Join("; ", changes);
    }

    public void Reset() { _known.Clear(); _pending.Clear(); }
}
