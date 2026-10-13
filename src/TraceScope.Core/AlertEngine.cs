namespace TraceScope.Core;

public enum AlertCondition { Latency, PacketLoss, Jitter, Unreachable, RouteChanged, Recovery }

public sealed record AlertRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Monitoring alert";
    public AlertCondition Condition { get; init; } = AlertCondition.PacketLoss;
    public double Threshold { get; init; } = 10;
    public TimeSpan EvaluationPeriod { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RecoveryPeriod { get; init; } = TimeSpan.FromSeconds(10);
    public bool Enabled { get; init; } = true;
    public string? Target { get; init; }
    public string? Group { get; init; }
}

public sealed record MonitoringEvent(Guid Id, Guid SessionId, DateTimeOffset Timestamp,
    string Kind, string Message, string? AlertRuleId = null)
{
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string? AcknowledgmentNote { get; init; }
}

/// <summary>Stateful evaluator. A breach must persist for EvaluationPeriod; a healthy condition must
/// persist for RecoveryPeriod. Probe errors cannot trigger outages or recover a network alert.</summary>
public sealed class AlertEngine
{
    private sealed class State
    {
        public DateTimeOffset? Pending;
        public DateTimeOffset? HealthySince;
        public DateTimeOffset? LastFired;
        public bool Active;
        public bool Outage;
    }
    private readonly Dictionary<(Guid Session, string Rule), State> _states = new();
    private readonly object _gate = new();

    public void ClearSession(Guid sessionId)
    {
        lock (_gate) foreach (var key in _states.Keys.Where(k => k.Session == sessionId).ToArray()) _states.Remove(key);
    }

    public IReadOnlyList<MonitoringEvent> Evaluate(RoundSnapshot snapshot, IReadOnlyList<AlertRule> rules)
    {
        lock (_gate)
        {
            var configured = rules.Where(r => r.Enabled).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _states.Keys.Where(k => k.Session == snapshot.SessionId && !configured.Contains(k.Rule)).ToArray())
                _states.Remove(key);
            var events = new List<MonitoringEvent>();
            foreach (var rule in rules)
            {
                var key = (snapshot.SessionId, rule.Id);
                if (!rule.Enabled || rule.Target is { Length: > 0 } target &&
                    !target.Equals(snapshot.Target, StringComparison.OrdinalIgnoreCase))
                { _states.Remove(key); continue; }
                if (!_states.TryGetValue(key, out var state)) _states[key] = state = new State();
                var period = rule.EvaluationPeriod < TimeSpan.Zero ? TimeSpan.Zero : rule.EvaluationPeriod;
                var recovery = rule.RecoveryPeriod < TimeSpan.Zero ? TimeSpan.Zero : rule.RecoveryPeriod;
                var cooldown = rule.Cooldown < TimeSpan.Zero ? TimeSpan.Zero : rule.Cooldown;
                var now = snapshot.Timestamp;
                bool CanFire() => state.LastFired is null || now - state.LastFired >= cooldown;
                void Emit(string kind, string detail)
                {
                    events.Add(new(Guid.NewGuid(), snapshot.SessionId, now, kind,
                        $"{snapshot.Target}: {rule.Name} — {detail}", rule.Id));
                    state.LastFired = now;
                }
                if (rule.Condition == AlertCondition.RouteChanged)
                {
                    if (snapshot.RouteChanged && CanFire()) Emit("RouteChange", snapshot.RouteDescription ?? "Network route changed");
                    continue;
                }
                if (snapshot.Destination.Status == ProbeStatus.Error) { state.Pending = null; state.HealthySince = null; continue; }
                var responds = snapshot.Destination.Status == ProbeStatus.Success && snapshot.Destination.LastMs is >= 0;
                if (rule.Condition == AlertCondition.Recovery)
                {
                    if (!responds)
                    {
                        state.HealthySince = null;
                        state.Pending ??= now;
                        if (now - state.Pending >= period) state.Outage = true;
                    }
                    else
                    {
                        state.Pending = null;
                        if (state.Outage)
                        {
                            state.HealthySince ??= now;
                            if (now - state.HealthySince >= recovery && CanFire())
                            { Emit("Recovery", "Connection recovered after an outage"); state.Outage = false; state.HealthySince = null; }
                        }
                    }
                    continue;
                }
                var summary = MonitoringStatistics.Calculate(snapshot.Destination.History,
                    period == TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : period, now);
                var (breach, detail) = rule.Condition switch
                {
                    AlertCondition.Latency => (summary.AverageMs > rule.Threshold, $"Average latency {summary.AverageMs:0.0} ms exceeds {rule.Threshold:0.0} ms"),
                    AlertCondition.PacketLoss => (summary.Sent > 0 && summary.LossPercent > rule.Threshold, $"Packet loss {summary.LossPercent:0.0}% exceeds {rule.Threshold:0.0}%"),
                    AlertCondition.Jitter => (summary.JitterMs > rule.Threshold, $"Jitter {summary.JitterMs:0.0} ms exceeds {rule.Threshold:0.0} ms"),
                    AlertCondition.Unreachable => (!responds, "Destination is unreachable"),
                    _ => (false, "")
                };
                if (breach)
                {
                    state.HealthySince = null;
                    state.Pending ??= now;
                    if (now - state.Pending >= period && CanFire())
                    { Emit("Alert", detail); state.Active = true; }
                }
                else
                {
                    state.Pending = null;
                    if (state.Active)
                    {
                        state.HealthySince ??= now;
                        if (now - state.HealthySince >= recovery)
                        { Emit("Recovery", "Condition returned to normal"); state.Active = false; state.HealthySince = null; }
                    }
                }
            }
            return events;
        }
    }
}
