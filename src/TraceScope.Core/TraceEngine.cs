using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TraceScope.Core;

public sealed class TraceEngine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TargetSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _hostnames = new();
    private readonly ConcurrentDictionary<string, byte> _lookupStarted = new();
    private readonly IProbeTransport _transport;
    private readonly ProbeOptions _defaults;
    private readonly SemaphoreSlim _probes = new(16, 16);
    private readonly SemaphoreSlim _dns = new(4, 4);
    private bool _disposed;
    private readonly AlertEngine _alerts = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<AlertRule>> _alertRules = new();
    private readonly ConcurrentQueue<MonitoringEvent> _events = new();

    public TraceEngine(string databasePath, ProbeOptions? options = null, IProbeTransport? transport = null)
    {
        _defaults = options ?? new ProbeOptions();
        _defaults.Validate();
        Storage = new HistoryStore(databasePath);
        _transport = transport ?? new AdvancedProbeTransport();
    }
    public HistoryStore Storage { get; }
    public IReadOnlyList<TargetSession> Sessions => _sessions.Values.OrderBy(s => s.StartedAt).ToArray();
    public event EventHandler<RoundSnapshot>? SnapshotUpdated;
    public event EventHandler<TargetSession>? SessionChanged;
    public event EventHandler<MonitoringEvent>? MonitoringEventOccurred;
    public IReadOnlyList<MonitoringEvent> RecentEvents => _events.OrderByDescending(e => e.Timestamp).ToArray();
    public void SetAlertRules(Guid sessionId, IReadOnlyList<AlertRule> rules) => _alertRules[sessionId] = rules.ToArray();

    public bool ForgetSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        if (session.Worker is { IsCompleted: false }) throw new InvalidOperationException("Stop a target before releasing it.");
        if (!_sessions.TryRemove(sessionId, out _)) return false;
        _alertRules.TryRemove(sessionId, out _); _alerts.ClearSession(sessionId);
        session.Cancellation.Dispose();
        return true;
    }

    public async Task<TargetSession> StartTargetAsync(string target, CancellationToken cancellationToken = default)
        => await StartTargetAsync(target, _defaults, cancellationToken);

    public async Task<TargetSession> StartTargetAsync(string target, ProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        options.Validate();
        target = target.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0) target = uri.Host;
        var addresses = IPAddress.TryParse(target, out var literal) ? [literal]
            : await Dns.GetHostAddressesAsync(target, cancellationToken);
        var preferredFamily = IPAddress.TryParse(options.SourceAddress, out var source) ? source.AddressFamily : AddressFamily.InterNetwork;
        var address = addresses.FirstOrDefault(a => a.AddressFamily == preferredFamily)
            ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            ?? throw new ArgumentException("The target has no usable IPv4 or IPv6 address.", nameof(target));
        var session = new TargetSession(Guid.NewGuid(), target, address.ToString(), options)
        { Capability = ProtocolCapabilities.Get(options.Protocol, address.AddressFamily) };
        await Storage.InitializeProfessionalAsync(cancellationToken);
        await Storage.AddSessionAsync(session, cancellationToken);
        _sessions.TryAdd(session.Id, session);
        SessionChanged?.Invoke(this, session);
        session.Worker = Task.Run(() => RunAsync(session, address));
        return session;
    }

    public void Pause(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.State == SessionState.Running)
        { session.State = SessionState.Paused; SessionChanged?.Invoke(this, session); }
    }
    public void Resume(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.State == SessionState.Paused)
        { session.State = SessionState.Running; SessionChanged?.Invoke(this, session); }
    }
    public void UpdateOptions(Guid sessionId, ProbeOptions options)
    {
        options.Validate();
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Options = options;
            session.Capability = ProtocolCapabilities.Get(options.Protocol, IPAddress.Parse(session.ResolvedAddress).AddressFamily);
        }
    }

    /// <summary>Reset current measurement counters without interrupting probes or changing saved history.</summary>
    public void ResetStatistics(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        lock (session.StatisticsGate)
        {
            session.StatisticsGeneration++;
            if (session.LatestSnapshot is not { } snapshot) return;
            static HopSnapshot Clear(HopSnapshot hop) => hop with
            {
                Sent = 0, Received = 0, LossPercent = 0, LastMs = null,
                AverageMs = null, MinimumMs = null, MaximumMs = null, JitterMs = null
            };
            session.LatestSnapshot = snapshot with
            {
                Hops = snapshot.Hops.Select(Clear).ToArray(), Destination = Clear(snapshot.Destination),
                RouteChanged = false, RouteDescription = null
            };
            SnapshotUpdated?.Invoke(this, session.LatestSnapshot);
        }
    }

    public async Task StopAsync(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        await session.Cancellation.CancelAsync();
        if (session.Worker is { } worker) await worker;
    }

    private async Task RunAsync(TargetSession session, IPAddress destination)
    {
        var token = session.Cancellation.Token;
        var statistics = new Dictionary<(int Hop, string Address), HopStatistics>();
        var lastAddresses = new Dictionary<int, string>();
        var destinationStatistics = new HopStatistics();
        var round = 0L;
        var statisticsGeneration = 0L;
        int? destinationHop = null;
        var route = new RouteTracker();
        // Display-only preferences do not create a new measurement configuration.
        static object Identity(ProbeOptions o) => o with { HistoryLimit = 0, ResolveHostnames = false };
        var probeIdentity = Identity(session.Options);
        var udpPorts = new UdpPortSequence();
        var udpRoute = new UdpRouteDiscovery();
        var measurementEpoch = 0;
        long? previousStart = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                while (session.State == SessionState.Paused) await Task.Delay(100, token);
                var started = Stopwatch.GetTimestamp();
                var samplingInterval = previousStart is { } previous ? Stopwatch.GetElapsedTime(previous, started) : (TimeSpan?)null;
                previousStart = started;
                var stopwatch = Stopwatch.StartNew();
                var options = session.Options;
                var currentIdentity = Identity(options);
                if (!probeIdentity.Equals(currentIdentity))
                {
                    measurementEpoch++;
                    statistics.Clear(); lastAddresses.Clear(); destinationStatistics = new HopStatistics();
                    destinationHop = null; route.Reset(); probeIdentity = currentIdentity; udpPorts = new UdpPortSequence(); udpRoute = new UdpRouteDiscovery();
                }
                // Retain existing ICMP/TCP refresh behavior; UDP uses evidence-driven discovery below.
                var cap = destinationHop.HasValue && round % 10 != 0
                    ? Math.Min(options.MaxHops, destinationHop.Value + 2) : options.MaxHops;
                using var roundLimiter = new SemaphoreSlim(options.Concurrency);
                var measurements = new ConcurrentQueue<ProbeMeasurement>();
                ProbeOptions NextOptions() => options.Protocol == ProbeProtocol.Udp
                    ? options with { DestinationPort = udpPorts.Next(options), UdpPortMode = UdpPortMode.Fixed }
                    : options;
                async Task<ProbeResult> InRoundAsync(int ttl, ProbeOptions configuredOptions)
                {
                    await roundLimiter.WaitAsync(token);
                    var admittedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        var result = await ProbeAsync(destination, ttl, configuredOptions, token);
                        if (options.Protocol == ProbeProtocol.Udp)
                            measurements.Enqueue(new(ttl, result.MeasuredAt ?? DateTimeOffset.UtcNow, result, configuredOptions));
                        return result;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (options.Protocol == ProbeProtocol.Udp)
                            measurements.Enqueue(new(ttl, admittedAt, new ProbeResult(null, null, ProbeStatus.Error,
                                "Monitoring canceled this attempt before a complete measurement was available; transmission unknown."), configuredOptions) { Canceled = true });
                        throw;
                    }
                    finally { roundLimiter.Release(); }
                }
                // The independent endpoint shares the budgets but never defines route length.
                var destinationTask = InRoundAsync(255, NextOptions());
                async Task<(int Hop, ProbeResult Result)[]> DiscoverAsync()
                {
                    if (options.Protocol == ProbeProtocol.Udp)
                        return await udpRoute.DiscoverAsync(options.MaxHops, options.Concurrency,
                            session.ResolvedAddress, hop => InRoundAsync(hop, NextOptions()));
                    var tasks = Enumerable.Range(1, cap).Select(async hop =>
                        (Hop: hop, Result: await InRoundAsync(hop, options))).ToArray();
                    return await Task.WhenAll(tasks);
                }
                var discoveryTask = DiscoverAsync();
                // Observe both branches on cancellation/failure; no probe outlives its round.
                try { await Task.WhenAll(discoveryTask, destinationTask); }
                catch
                {
                    // Completed replies and explicitly canceled attempts remain auditable. Never
                    // turn monitoring cancellation into remote loss or invent a completed round.
                    if (measurements.Count > 0)
                        try { await Storage.AppendProbeAuditAsync(session.Id, round + 1, measurementEpoch, options,
                            measurements.OrderBy(m => m.Timestamp).ToArray()); }
                        catch (Exception ex) { session.Error = "Incomplete round audit could not be saved: " + ex.Message; }
                    throw;
                }
                var results = await discoveryTask;
                var endpoint = await destinationTask;
                if (endpoint.Responded && (endpoint.Status != ProbeStatus.Success || endpoint.Address != session.ResolvedAddress))
                    endpoint = endpoint with { Status = ProbeStatus.Unreachable, LatencyMs = null,
                        Detail = $"Endpoint probe received {endpoint.Status} from {endpoint.Address}; destination response was not established. {endpoint.Detail}" };
                // Reset only measurement counters: route identity, round IDs and graph history survive.
                // An in-flight round becomes the first sample after a requested reset.
                lock (session.StatisticsGate)
                {
                    if (statisticsGeneration != session.StatisticsGeneration)
                    {
                        foreach (var stats in statistics.Values) stats.ResetCounters();
                        destinationStatistics.ResetCounters();
                        statisticsGeneration = session.StatisticsGeneration;
                    }
                }
                var timestamp = DateTimeOffset.UtcNow;
                round++;
                var reached = results.Where(r => r.Result.Responded && r.Result.Status == ProbeStatus.Success
                    && r.Result.Address == session.ResolvedAddress).Select(r => (int?)r.Hop).Min();
                destinationHop = options.Protocol == ProbeProtocol.Udp ? udpRoute.TerminalHop : reached;
                var hops = new List<HopSnapshot>();
                foreach (var (hop, result) in results)
                {
                    if (options.Protocol != ProbeProtocol.Udp && reached.HasValue && hop > reached) break;
                    if (result.Address is { } address) lastAddresses[hop] = address;
                    var currentAddress = result.Address ?? lastAddresses.GetValueOrDefault(hop);
                    var key = (hop, currentAddress ?? "?");
                    if (!statistics.TryGetValue(key, out var stats)) statistics[key] = stats = new HopStatistics();
                    stats.Add(result.MeasuredAt ?? timestamp, result, options.HistoryLimit);
                    if (options.ResolveHostnames && currentAddress is { } ip) BeginResolve(ip);
                    hops.Add(stats.Snapshot(hop, currentAddress,
                        currentAddress is { } current ? _hostnames.GetValueOrDefault(current) : null,
                        currentAddress == session.ResolvedAddress && (options.Protocol != ProbeProtocol.Udp || hop == destinationHop)) with { MeasurementEpoch = measurementEpoch });
                }
                // Retain cumulative per-router counters across revisits; release inactive history buffers.
                var currentKeys = hops.Select(h => (h.Hop, h.Address ?? "?")).ToHashSet();
                foreach (var key in statistics.Keys.Where(k => !currentKeys.Contains(k)).ToArray()) statistics[key].Suspend();
                destinationStatistics.Add(endpoint.MeasuredAt ?? timestamp, endpoint, options.HistoryLimit);
                if (options.ResolveHostnames) BeginResolve(session.ResolvedAddress);
                var endpointSnapshot = destinationStatistics.Snapshot(0, session.ResolvedAddress,
                    _hostnames.GetValueOrDefault(session.ResolvedAddress), true) with { MeasurementEpoch = measurementEpoch };
                var warning = results.Select(r => r.Result).Append(endpoint)
                    .FirstOrDefault(r => r.Status == ProbeStatus.Error)?.Detail;
                if (session.Capability is { HopDiscoveryAvailable: false } capability)
                    warning ??= capability.Detail;
                if (options.Protocol == ProbeProtocol.Udp && endpoint.ResponseKind == ProbeResponseKind.UdpPortUnreachable)
                    warning ??= "UDP destination reachability confirmed by ICMP Port Unreachable; UDP service availability is not established.";
                var snapshot = new RoundSnapshot(session.Id, session.Target, session.ResolvedAddress,
                    timestamp, round, reached.HasValue, hops, endpointSnapshot, stopwatch.Elapsed, warning)
                { MeasurementEpoch = measurementEpoch, MeasurementOptions = options,
                    Measurements = measurements.OrderBy(m => m.Timestamp).ToArray(), SamplingInterval = samplingInterval,
                    RouteBoundIsHistorical = options.Protocol == ProbeProtocol.Udp && !reached.HasValue && destinationHop.HasValue };
                var routeChange = route.Observe(hops);
                snapshot = snapshot with { RouteChanged = routeChange is not null, RouteDescription = routeChange };
                try { await Storage.AppendAsync(snapshot, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { snapshot = snapshot with { Warning = "History could not be saved: " + ex.Message }; }
                if (routeChange is not null)
                    await RecordEventAsync(new MonitoringEvent(Guid.NewGuid(), session.Id, timestamp, "RouteChange", routeChange, null), token);
                foreach (var alert in _alerts.Evaluate(snapshot, _alertRules.GetValueOrDefault(session.Id) ?? []))
                    await RecordEventAsync(alert, token);
                lock (session.StatisticsGate)
                {
                    // Do not overwrite a reset with a snapshot assembled before the reset request.
                    if (statisticsGeneration == session.StatisticsGeneration)
                    {
                        session.LatestSnapshot = snapshot;
                        SnapshotUpdated?.Invoke(this, snapshot);
                    }
                }
                var delay = options.Interval - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { session.State = SessionState.Error; session.Error = ex.Message; }
        finally
        {
            if (session.State != SessionState.Error) session.State = SessionState.Stopped;
            try { await Storage.StopSessionAsync(session.Id); }
            catch (Exception ex) { session.Error ??= "History finalization failed: " + ex.Message; }
            SessionChanged?.Invoke(this, session);
        }
    }

    private async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options, CancellationToken token)
    {
        await _probes.WaitAsync(token);
        try
        {
            // UTC dispatch time, after concurrency admission; RTT/deadlines remain monotonic in transports.
            var measuredAt = DateTimeOffset.UtcNow;
            var result = _transport is IConfiguredProbeTransport configured
                ? await configured.ProbeAsync(destination, ttl, options, token)
                : await _transport.ProbeAsync(destination, ttl, options.Timeout, token);
            return result with { MeasuredAt = measuredAt };
        }
        finally { _probes.Release(); }
    }

    private async Task RecordEventAsync(MonitoringEvent monitoringEvent, CancellationToken token)
    {
        _events.Enqueue(monitoringEvent);
        while (_events.Count > 500) _events.TryDequeue(out _);
        try { await Storage.AppendEventAsync(monitoringEvent, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Console.Error.WriteLine("Could not persist monitoring event: " + ex.Message); }
        MonitoringEventOccurred?.Invoke(this, monitoringEvent);
    }

    private void BeginResolve(string address)
    {
        if (_disposed || _lookupStarted.Count >= 4096) return;
        if (!_lookupStarted.TryAdd(address, 0)) return;
        _ = Task.Run(async () =>
        {
            await _dns.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var host = await Dns.GetHostEntryAsync(address, AddressFamily.Unspecified, timeout.Token);
                if (host.HostName != address) _hostnames[address] = host.HostName;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { }
            finally { _dns.Release(); }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Task.WhenAll(_sessions.Keys.Select(StopAsync));
        foreach (var session in _sessions.Values) session.Cancellation.Dispose();
        // DNS lookups are bounded and may still complete; retain their semaphore until those tasks finish.
        _probes.Dispose();
    }
}
