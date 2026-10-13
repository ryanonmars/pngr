using System;

namespace TraceScope.App.Controls;

/// <summary>Timestamp-based navigation shared by every graph of one monitoring session.</summary>
public sealed class TimelineViewport
{
    public static readonly TimeSpan MinimumHorizon = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumHorizon = TimeSpan.FromDays(7);
    private TimeSpan _horizon = TimeSpan.FromMinutes(10);
    private DateTimeOffset? _endTime, _first, _last;
    public event Action? Changed;
    public TimeSpan Horizon
    {
        get => _horizon;
        set { var next = ClampHorizon(value); if (next == _horizon) return; _horizon = next; ClampEnd(); Changed?.Invoke(); }
    }
    public DateTimeOffset? EndTime
    {
        get => _endTime;
        set { var next = value is { } end ? Clamp(end) : (DateTimeOffset?)null; if (_endTime == next) return; _endTime = next; Changed?.Invoke(); }
    }
    public DateTimeOffset? First { get => _first; set { if (_first == value) return; _first = value; ClampEnd(); Changed?.Invoke(); } }
    public DateTimeOffset? Last { get => _last; set { if (_last == value) return; _last = value; ClampEnd(); Changed?.Invoke(); } }
    public DateTimeOffset? SelectionStart { get; private set; }
    public DateTimeOffset? SelectionEnd { get; private set; }
    public DateTimeOffset EffectiveEnd => EndTime ?? Last ?? DateTimeOffset.UtcNow;
    public void Pan(TimeSpan offset) => EndTime = EffectiveEnd + offset;
    /// <param name="factor">Greater than one zooms out; less than one zooms in.</param>
    public void Zoom(double factor, double anchorFraction)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        anchorFraction = Math.Clamp(anchorFraction, 0, 1);
        var oldEnd = EffectiveEnd;
        var next = ClampHorizon(TimeSpan.FromTicks((long)Math.Clamp(_horizon.Ticks * factor, MinimumHorizon.Ticks, MaximumHorizon.Ticks)));
        if (next == _horizon) return;
        _endTime = oldEnd + TimeSpan.FromTicks((long)Math.Round((next.Ticks - _horizon.Ticks) * (1 - anchorFraction)));
        _horizon = next;
        ClampEnd();
        Changed?.Invoke();
    }
    public void SelectRange(DateTimeOffset from, DateTimeOffset to)
    {
        var start = from <= to ? from : to;
        var end = from <= to ? to : from;
        if (First is { } first) { start = start < first ? first : start; end = end < first ? first : end; }
        if (Last is { } last) { start = start > last ? last : start; end = end > last ? last : end; }
        SelectionStart = start; SelectionEnd = end;
        if (!_endTime.HasValue) _endTime = EffectiveEnd;
        Changed?.Invoke();
    }
    public void ClearSelection()
    {
        if (SelectionStart is null && SelectionEnd is null) return;
        SelectionStart = SelectionEnd = null;
        Changed?.Invoke();
    }
    public void GoLive()
    {
        _endTime = null;
        SelectionStart = SelectionEnd = null;
        Changed?.Invoke();
    }
    private static TimeSpan ClampHorizon(TimeSpan value) => value < MinimumHorizon ? MinimumHorizon : value > MaximumHorizon ? MaximumHorizon : value;
    private void ClampEnd() { if (_endTime is { } end) _endTime = Clamp(end); }
    private DateTimeOffset Clamp(DateTimeOffset end)
    {
        if (Last is { } last && end > last) end = last;
        if (First is { } first && end < first) end = first;
        return end;
    }
}
