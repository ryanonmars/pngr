using System;

namespace TraceScope.App.Controls;

/// <summary>A measured latency sample. Missing replies are represented explicitly.</summary>
public sealed record GraphPoint(DateTimeOffset Time, double? Latency, bool Lost,
    double? LossPercent = null, double? JitterMs = null, bool IsError = false);

public sealed record GraphEvent(DateTimeOffset Time, string Kind, string Message);

/// <summary>Measured latency statistics for one traceroute hop.</summary>
public sealed record HopPlotRow(
    int Hop,
    double? Minimum,
    double? Average,
    double? Maximum,
    double? Current,
    double LossPercent);
