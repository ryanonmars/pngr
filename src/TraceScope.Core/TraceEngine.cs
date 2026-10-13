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
        var session = new TargetSession(Guid.NewGuid(), target, address.ToString(), options);
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
        if (_sessions.TryGetValue(sessionId, out var session)) session.Options = options;
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
        int? destinationHop = null;
        var route = new RouteTracker();
        var probeIdentity = (session.Options.Protocol, session.Options.DestinationPort, session.Options.SourceAddress, session.Options.InterfaceId);
        try
        {
            while (!token.IsCancellationRequested)
            {
                while (session.State == SessionState.Paused) await Task.Delay(100, token);
                var stopwatch = Stopwatch.StartNew();
                var options = session.Options;
                var currentIdentity = (options.Protocol, options.DestinationPort, options.SourceAddress, options.InterfaceId);
                if (probeIdentity != currentIdentity)
                {
                    statistics.Clear(); lastAddresses.Clear(); destinationStatistics = new HopStatistics();
                    destinationHop = null; route.Reset(); probeIdentity = currentIdentity;
                }
                // Refresh beyond the known endpoint periodically so changes to a longer route are discovered.
                var cap = destinationHop.HasValue && round % 10 != 0
                    ? Math.Min(options.MaxHops, destinationHop.Value + 2) : options.MaxHops;
                using var roundLimiter = new SemaphoreSlim(options.Concurrency);
                var hopTasks = Enumerable.Range(1, cap).Select(async hop =>
                {
                    await roundLimiter.WaitAsync(token);
                    try { return (Hop: hop, Result: await ProbeAsync(destination, hop, options, token)); }
                    finally { roundLimiter.Release(); }
                }).ToArray();
                var destinationTask = ProbeAsync(destination, 255, options, token);
                var results = await Task.WhenAll(hopTasks);
                var endpoint = await destinationTask;
                var timestamp = DateTimeOffset.UtcNow;
                round++;
                var reached = results.Where(r => r.Result.Status == ProbeStatus.Success
                    && r.Result.Address == session.ResolvedAddress).Select(r => (int?)r.Hop).Min();
                destinationHop = reached;
                var hops = new List<HopSnapshot>();
                foreach (var (hop, result) in results)
                {
                    if (reached.HasValue && hop > reached) break;
                    if (result.Address is { } address) lastAddresses[hop] = address;
                    var currentAddress = result.Address ?? lastAddresses.GetValueOrDefault(hop);
                    var key = (hop, currentAddress ?? "?");
                    if (!statistics.TryGetValue(key, out var stats)) statistics[key] = stats = new HopStatistics();
                    stats.Add(timestamp, result, options.HistoryLimit);
                    if (options.ResolveHostnames && currentAddress is { } ip) BeginResolve(ip);
                    hops.Add(stats.Snapshot(hop, currentAddress,
                        currentAddress is { } current ? _hostnames.GetValueOrDefault(current) : null,
                        currentAddress == session.ResolvedAddress));
                }
                // Historic routes remain on disk; retain only the current route's statistics in memory.
                var currentKeys = hops.Select(h => (h.Hop, h.Address ?? "?")).ToHashSet();
                foreach (var key in statistics.Keys.Where(k => !currentKeys.Contains(k)).ToArray()) statistics.Remove(key);
                destinationStatistics.Add(timestamp, endpoint, options.HistoryLimit);
                if (options.ResolveHostnames) BeginResolve(session.ResolvedAddress);
                var endpointSnapshot = destinationStatistics.Snapshot(0, session.ResolvedAddress,
                    _hostnames.GetValueOrDefault(session.ResolvedAddress), true);
                var warning = results.Select(r => r.Result).Append(endpoint)
                    .FirstOrDefault(r => r.Status == ProbeStatus.Error)?.Detail;
                var snapshot = new RoundSnapshot(session.Id, session.Target, session.ResolvedAddress,
                    timestamp, round, reached.HasValue, hops, endpointSnapshot, stopwatch.Elapsed, warning);
                var routeChange = route.Observe(hops);
                snapshot = snapshot with { RouteChanged = routeChange is not null, RouteDescription = routeChange };
                session.LatestSnapshot = snapshot;
                try { await Storage.AppendAsync(snapshot, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { snapshot = snapshot with { Warning = "History could not be saved: " + ex.Message }; }
                if (routeChange is not null)
                    await RecordEventAsync(new MonitoringEvent(Guid.NewGuid(), session.Id, timestamp, "RouteChange", routeChange, null), token);
                foreach (var alert in _alerts.Evaluate(snapshot, _alertRules.GetValueOrDefault(session.Id) ?? []))
                    await RecordEventAsync(alert, token);
                session.LatestSnapshot = snapshot;
                SnapshotUpdated?.Invoke(this, snapshot);
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
        try { return _transport is IConfiguredProbeTransport configured
            ? await configured.ProbeAsync(destination, ttl, options, token)
            : await _transport.ProbeAsync(destination, ttl, options.Timeout, token); }
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
