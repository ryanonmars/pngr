namespace TraceScope.Core;

/// <summary>Discover in ascending, bounded waves. Silence does not invalidate a known terminal;
/// a TTL-expired response at or beyond it is affirmative evidence that discovery must continue.
/// Complete each wave before deciding: a fast higher-TTL reply must not outrun a delayed lower one.
/// Different UDP flows can take different paths, so retain higher router evidence.</summary>
internal sealed class UdpRouteDiscovery
{
    internal const int SilentRoundsBeforeRediscovery = 10;
    private int _silentRounds;
    public int? TerminalHop { get; private set; }

    public async Task<(int Hop, ProbeResult Result)[]> DiscoverAsync(int maximum, int concurrency,
        string destination, Func<int, Task<ProbeResult>> probe)
    {
        var observations = new List<(int Hop, ProbeResult Result)>();
        var limit = Math.Min(maximum, TerminalHop ?? maximum);
        int? reached = null;
        var furthestRouter = 0;
        for (var first = 1; first <= limit;)
        {
            var count = Math.Min(concurrency, limit - first + 1);
            var tasks = Enumerable.Range(first, count).Select(async hop => (Hop: hop, Result: await probe(hop))).ToArray();
            var wave = await Task.WhenAll(tasks);
            observations.AddRange(wave);
            foreach (var (hop, result) in wave)
            {
                if (result.Responded && result.Status == ProbeStatus.TimeExceeded)
                    furthestRouter = Math.Max(furthestRouter, hop);
            }
            reached = observations.Where(x => x.Hop > furthestRouter && x.Result.Responded
                && x.Result.Status == ProbeStatus.Success && x.Result.Address == destination)
                .Select(x => (int?)x.Hop).Min();
            first += count;
            // A terminal response bounds only the flow that produced it. Higher router replies
            // are real alternate/changed-route evidence, and must not be clipped away.
            if (reached is { } terminal && furthestRouter < terminal) break;
            if (first > limit && furthestRouter >= limit)
                limit = maximum;
        }
        if (reached is { } detected && furthestRouter < detected)
        { TerminalHop = detected; _silentRounds = 0; }
        else if (TerminalHop is { } old && furthestRouter >= old)
        { TerminalHop = null; _silentRounds = 0; } // Affirmative evidence immediately invalidates the old bound.
        else if (TerminalHop.HasValue && ++_silentRounds >= SilentRoundsBeforeRediscovery)
        { TerminalHop = null; _silentRounds = 0; } // A stale bound cannot suppress rediscovery indefinitely.

        // Already transmitted probes can exceed the terminal inside the final concurrent wave.
        // A destination response there is redundant reachability, not another route position.
        var displayedLimit = TerminalHop ?? observations.Count;
        return observations.Where(x => x.Hop <= displayedLimit).ToArray();
    }
}
