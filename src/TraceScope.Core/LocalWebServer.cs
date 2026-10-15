using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace TraceScope.Core;

/// <summary>A read-only desktop-owned server, with optional authenticated access on one assigned private interface.</summary>
public sealed class LocalWebServer(TraceEngine engine, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _application;
    private bool _disposed;
    private WebServerOptions? _options;
    private WebAuthentication? _authentication;
    private IPAddress? _watchedAddress;
    public string? Url { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> ListeningUrls { get; private set; } = [];

    public Task StartAsync(int port = 8787, CancellationToken cancellationToken = default)
        => StartAsync(new WebServerOptions { Port = port }, cancellationToken);

    public async Task StartAsync(WebServerOptions configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(configuration));
        var lanAddress = configuration.LanEnabled ? WebServerNetwork.ValidateLanAddress(configuration,
            WebServerNetwork.GetLanAddresses()) : null;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null && _options == configuration) return;
            await StopApplicationAsync();
            LastError = null;
            var port = configuration.Port;
            if (lanAddress is not null && port == 0)
            {
                // Select a common ephemeral port for both exact listeners. A race is reported as a normal bind failure.
                var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
            }
            var authentication = configuration.LanEnabled
                ? new WebAuthentication(configuration.AccessKeyVerifier!, timeProvider ?? TimeProvider.System) : null;
            // This server belongs to the desktop process. Do not import appsettings/environment
            // endpoints or install ConsoleLifetime's competing SIGTERM/Ctrl+C handlers.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            builder.Services.AddRouting();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 4096;
                options.Limits.MaxRequestHeadersTotalSize = 16384;
                options.Limits.MaxRequestLineSize = 4096;
                options.Limits.MaxConcurrentConnections = 128;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                options.Listen(IPAddress.Loopback, port);
                if (lanAddress is not null) options.Listen(lanAddress, port);
            });
            builder.Services.Configure<FormOptions>(options =>
            {
                options.ValueCountLimit = 4; options.KeyLengthLimit = 64; options.ValueLengthLimit = 256;
                options.MultipartBodyLengthLimit = 4096; options.BufferBodyLengthLimit = 4096;
            });
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                // Keep same-origin POST Origin intact (no-referrer makes it "null" on HTTP),
                // while withholding referrers from every other origin.
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                context.Items["WebNonce"] = nonce;
                context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self'; base-uri 'none'; object-src 'none'; form-action 'self'; frame-ancestors 'none'";
                // Explicit Host validation prevents DNS rebinding. Forwarded headers are deliberately not trusted.
                var host = context.Request.Host.Host;
                var allowedHost = host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || (lanAddress is not null && IPAddress.TryParse(host.Trim('[', ']'), out var parsed) && parsed.Equals(lanAddress));
                if (!allowedHost || (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80)) != context.Connection.LocalPort)
                { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                var origin = context.Request.Headers.Origin.ToString();
                if (context.Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site"
                    || (!string.IsNullOrEmpty(origin) && !origin.Equals($"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
                { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                if (context.Request.ContentLength > 4096)
                {
                    // Do not reuse an HTTP/1 connection whose oversized request body was deliberately not drained.
                    if (context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal)) context.Response.Headers.Connection = "close";
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return;
                }
                if (authentication is not null) await authentication.HandleAsync(context, next);
                else await next(context);
            });
            app.MapGet("/", (HttpContext context) => Results.Content(Dashboard.Replace("<!--AUTH-->",
                WebAuthentication.LogoutForm(context), StringComparison.Ordinal).Replace("<script>",
                $"<script nonce=\"{context.Items["WebNonce"]}\">", StringComparison.Ordinal), "text/html; charset=utf-8"));
            app.MapGet("/api/targets", () => Results.Json(engine.Sessions.Select(session => new
            {
                session.Id, session.Target, session.ResolvedAddress, session.State, session.StartedAt, session.Group,
                session.Options, session.Error, session.Capability,
                LossAccounting = "Failed eligible probe attempts / eligible attempts. Sent includes explicit network failures; local setup errors are excluded. Sent transmission records probe submission/attempt evidence, not verified delivery. Diagnostic failures do not prove forwarding loss.",
                Health = GetHealth(session),
                Measurement = (session.LatestSnapshot?.MeasurementOptions ?? session.Options).Protocol == ProbeProtocol.Udp
                    ? "UDP host reachability; ICMP Port Unreachable does not establish UDP service availability."
                    : (session.LatestSnapshot?.MeasurementOptions ?? session.Options).Protocol == ProbeProtocol.Tcp ? "TCP connect/RST host reachability." : "ICMP echo reachability.",
                Snapshot = session.LatestSnapshot is { } snapshot ? new
                {
                    snapshot.Timestamp, snapshot.Round, snapshot.DestinationReached,
                    snapshot.RoundDuration, snapshot.SamplingInterval, snapshot.MeasurementEpoch, snapshot.MeasurementOptions, snapshot.RouteBoundIsHistorical,
                    snapshot.Warning, snapshot.RouteChanged, snapshot.RouteDescription,
                    Destination = CompactHop(snapshot.Destination), Hops = snapshot.Hops.Select(CompactHop)
                } : null
            })));
            app.MapGet("/api/sessions", async (CancellationToken ct) => Results.Json(await engine.Storage.LoadSessionsAsync(ct)));
            app.MapGet("/api/interfaces", () => Results.Json(NetworkDiscovery.GetInterfaces()));
            app.MapGet("/api/neighbors", async (CancellationToken ct) => Results.Json(await NetworkDiscovery.LocalNeighborsAsync(ct)));
            app.MapGet("/api/gateway", async (CancellationToken ct) => Results.Json(new { Gateway = await NetworkDiscovery.GetLocalGatewayAsync(ct) }));
            app.MapGet("/api/history/{sessionId}", LoadHistoryAsync);
            app.MapGet("/api/events/{sessionId}", LoadEventsAsync);
            try
            {
                await app.StartAsync(cancellationToken);
                ListeningUrls = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
                Url = lanAddress is null ? ListeningUrls.Single() : ListeningUrls.Single(url => new Uri(url).Host.Trim('[', ']') == lanAddress.ToString());
                _authentication = authentication;
                _options = configuration;
                _application = app;
                if (lanAddress is not null)
                {
                    _watchedAddress = lanAddress;
                    NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
                    // Catch an interface disappearing during server startup as well.
                    _ = CheckNetworkAsync();
                }
            }
            catch
            {
                NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
                _watchedAddress = null; _application = null; _authentication = null; _options = null;
                Url = null; ListeningUrls = [];
                await app.DisposeAsync();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<IResult> LoadHistoryAsync(string sessionId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { Error = "Invalid session id." });
        if (!TryDate(request.Query["from"], DateTimeOffset.UtcNow.AddHours(-1), out var from)
            || !TryDate(request.Query["to"], DateTimeOffset.UtcNow, out var to) || from > to)
            return Results.BadRequest(new { Error = "Use valid ISO dates with from <= to." });
        if (!TryNumber(request.Query["hop"], 0, 0, 64, out var hop)
            || !TryNumber(request.Query["maxPoints"], 1200, 1, 10000, out var maxPoints))
            return Results.BadRequest(new { Error = "hop must be 0–64 and maxPoints 1–10000." });
        var sessions = await engine.Storage.LoadSessionsAsync(cancellationToken);
        if (!sessions.Any(s => s.Id == id)) return Results.NotFound(new { Error = "Session not found." });
        var points = await engine.Storage.LoadAggregatedSamplesAsync(id, hop, from, to, maxPoints, cancellationToken);
        var events = await engine.Storage.LoadEventsAsync(id, from, to, cancellationToken);
        return Results.Json(new { SessionId = id, Hop = hop, From = from, To = to,
            SampleCount = points.Sum(p => p.Count), ErrorCount = points.Sum(p => p.Errors), Truncated = false, Points = points, Events = events });
    }

    private async Task<IResult> LoadEventsAsync(string sessionId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { Error = "Invalid session id." });
        if (!TryDate(request.Query["from"], DateTimeOffset.UtcNow.AddHours(-24), out var from)
            || !TryDate(request.Query["to"], DateTimeOffset.UtcNow, out var to) || from > to)
            return Results.BadRequest(new { Error = "Use valid ISO dates with from <= to." });
        if (!(await engine.Storage.LoadSessionsAsync(cancellationToken)).Any(s => s.Id == id))
            return Results.NotFound(new { Error = "Session not found." });
        return Results.Json(await engine.Storage.LoadEventsAsync(id, from, to, cancellationToken));
    }

    private static bool TryDate(string? value, DateTimeOffset fallback, out DateTimeOffset date)
    {
        date = fallback;
        return string.IsNullOrEmpty(value) || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out date);
    }
    private static bool TryNumber(string? value, int fallback, int minimum, int maximum, out int number)
    {
        number = fallback;
        return (string.IsNullOrEmpty(value) || int.TryParse(value, out number)) && number >= minimum && number <= maximum;
    }
    private static string GetHealth(TargetSession session)
    {
        if (session.State is SessionState.Stopped or SessionState.Paused) return session.State.ToString();
        var snapshot = session.LatestSnapshot;
        if (session.State == SessionState.Error || snapshot?.Destination.Status is ProbeStatus.Error) return "Error";
        if (snapshot is null || snapshot.Destination.Sent == 0) return "Waiting";
        if ((snapshot.MeasurementOptions ?? session.Options).Protocol == ProbeProtocol.Udp)
        {
            if (snapshot.Destination.Status == ProbeStatus.Timeout) return "NoResponse";
            if (snapshot.Destination.Status == ProbeStatus.Unreachable) return "ProbeRejected";
            if (snapshot.Destination.Status == ProbeStatus.Success && snapshot.Destination.LastMs.HasValue)
                return snapshot.Destination.ResponseKind == ProbeResponseKind.UdpPortUnreachable ? "Reachable" : "Responded";
            return "Unavailable";
        }
        if (snapshot.Destination.Status is ProbeStatus.Timeout or ProbeStatus.Unreachable) return "Unreachable";
        return snapshot.Destination.LossPercent >= 5 || snapshot.Destination.AverageMs >= 150
            || snapshot.Destination.JitterMs >= 30 ? "Degraded" : "Healthy";
    }
    private static object CompactHop(HopSnapshot hop) => new
    {
        hop.Hop, hop.Address, hop.Hostname, hop.LastMs, hop.AverageMs, hop.MinimumMs, hop.MaximumMs,
        hop.JitterMs, hop.Sent, hop.Received, hop.LossPercent, LossAvailable = hop.Sent > 0, hop.IsDestination, hop.Status,
        hop.Detail, hop.ResponseKind, hop.Transmission, hop.MeasurementEpoch,
        ApplicationServiceAvailable = hop.Status != ProbeStatus.Success || hop.LastMs is null ? (bool?)null
            : hop.ResponseKind is ProbeResponseKind.UdpReply or ProbeResponseKind.TcpConnected
            ? (bool?)true : hop.ResponseKind is ProbeResponseKind.UdpPortUnreachable or ProbeResponseKind.TcpReset ? false : null
    };

    /// <summary>Immediately replaces the verifier and invalidates every browser session.</summary>
    public void RotateAccessKey(string verifier)
    {
        if (!WebAccessKey.IsValidVerifier(verifier)) throw new ArgumentException("Invalid access key verifier.", nameof(verifier));
        _authentication?.Rotate(verifier);
        if (_options is not null) _options = _options with { AccessKeyVerifier = verifier };
    }

    private void NetworkAddressChanged(object? sender, EventArgs args) => _ = CheckNetworkAsync();

    private async Task CheckNetworkAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_watchedAddress is null || WebServerNetwork.GetLanAddresses().Any(a => a.Address == _watchedAddress.ToString())) return;
            LastError = "The selected network address is unavailable. Refresh interfaces and re-enable the web server.";
            await StopApplicationAsync();
        }
        catch (Exception) { LastError = "The web server stopped after a network interface change."; }
        finally { _lifecycle.Release(); }
    }

    private async Task StopApplicationAsync()
    {
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        _watchedAddress = null;
        var app = _application;
        _application = null; _authentication = null; _options = null;
        Url = null; ListeningUrls = [];
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopApplicationAsync();
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class DesktopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string Dashboard = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Pngr · Local monitor</title>
        <style>
        :root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#191a1d;color:#d8d9dc;font:12px system-ui}header{padding:12px 18px;background:#24262b;border-bottom:1px solid #40434b;display:flex;gap:20px;align-items:center;flex-wrap:wrap}h1{font-size:17px;margin:0}small{color:#9b9da7}main{padding:12px 18px}table{width:100%;border-collapse:collapse;margin-bottom:16px}th,td{padding:5px 8px;border-bottom:1px solid #303239;text-align:right;white-space:nowrap}th{background:#24262b;color:#a7a9b2;font-weight:500}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}tr:hover{background:#282b33}button,select,input{background:#292b31;color:#ddd;border:1px solid #474953;padding:5px 8px;border-radius:3px}button{cursor:pointer}button:hover{background:#353841}button:disabled{opacity:.5;cursor:default}button[aria-pressed=true]{border-color:#80ae92;color:#c9d9ca}.healthy{color:#58cf88}.degraded{color:#f2bf55}.unreachable,.error{color:#ee6868}.panel{display:none}.toolbar,.chart-controls,.control-group{display:flex;flex-wrap:wrap;align-items:center;gap:8px}.toolbar{margin:12px 0}.toolbar strong{min-width:0;max-width:100%;overflow-wrap:anywhere}.toolbar label,.chart-controls label{display:flex;align-items:center;gap:6px}.table-scroll{overflow-x:auto}.chart-controls{justify-content:space-between;margin:0 0 10px;gap:12px}.control-group{gap:4px}.chart-controls input{margin:0;accent-color:#e7a55d}.chart-controls label{cursor:pointer}.charts{position:relative;display:grid;grid-template-rows:minmax(0,2fr) minmax(0,1fr);gap:10px;height:420px}.charts.jitter-hidden{grid-template-rows:minmax(0,1fr)}.graph{display:flex;flex-direction:column;min-height:0;min-width:0}.graph[hidden]{display:none}.graph h2{font-size:12px;font-weight:500;line-height:18px;margin:0 0 4px;color:#c9d9ca}.graph h2 small{font-size:10px;margin-left:8px;font-weight:400}#jitterPanel h2{color:#e7a55d}canvas{display:block;width:100%;flex:1;min-height:0;background:#212328;border:1px solid #3a3d46;touch-action:pan-y}#inspection{position:fixed;z-index:5;pointer-events:none;max-width:min(300px,calc(100vw - 16px));padding:8px 10px;background:#292c33;border:1px solid #626774;border-radius:3px;box-shadow:0 3px 12px #0006;font-size:11px;white-space:pre-line}#chartInfo{display:block;min-height:18px;margin-top:5px;overflow-wrap:anywhere}.chart-help{display:block;margin:5px 0 12px;font-size:10px}#message{color:#aeb1ba}a{color:#80b6f5}.latency{height:3px;background:#6cc48c;min-width:1px;max-width:200px}#hops th:nth-child(3),#hops td:nth-child(3){text-align:left}footer{padding:10px 18px;color:#989ba7}@media(max-width:600px){main{padding:10px}header{padding:10px;gap:12px}.toolbar{gap:10px}.chart-controls{align-items:flex-start}button,select{min-height:36px}.control-group button{min-width:38px}.charts{height:390px}.graph h2 small{display:none}footer{padding:10px}.chart-help{line-height:1.6}}
        </style></head>
        <body><header><h1>Pngr</h1><small>LOCAL MONITOR · READ ONLY</small><span id="message">Connecting…</span><!--AUTH--></header><main>
        <div class="table-scroll"><table><thead><tr><th>Target</th><th>Health</th><th>Protocol</th><th>Current</th><th>Average</th><th>Response loss</th><th>Jitter</th><th>Round</th><th>Route</th></tr></thead><tbody id="targets"></tbody></table></div>
        <div id="detail" class="panel"><div class="toolbar"><strong id="title"></strong><label>Hop <select id="hop"></select></label><label>History <select id="range"><option value="5">5 minutes</option><option value="30">30 minutes</option><option value="60" selected>1 hour</option><option value="360">6 hours</option><option value="1440">24 hours</option></select></label></div>
        <div class="table-scroll"><table id="hops"><thead><tr><th>Hop</th><th>Address</th><th>Name</th><th>Avg</th><th>Min</th><th>Max</th><th>Response loss</th><th>Jitter</th><th>Latency</th></tr></thead><tbody></tbody></table></div>
        <div class="chart-controls"><div class="control-group" role="group" aria-label="History navigation"><button id="older" title="Move earlier by the visible duration">← Earlier</button><button id="newer" title="Move later by the visible duration">Later →</button><button id="live" aria-pressed="true" title="Follow the latest data using the current zoom duration">Live</button></div><div class="control-group" role="group" aria-label="Chart zoom"><button id="zoomOut" aria-label="Zoom out" title="Zoom out around the center">−</button><button id="zoomIn" aria-label="Zoom in" title="Zoom in around the center">+</button><button id="fit" title="Fit available samples within the selected history range ending at the current window end">Fit</button><button id="resetZoom" title="Restore the duration selected in History">Reset Zoom</button></div><label><input id="showJitter" type="checkbox" checked>Show Jitter</label></div>
        <div id="charts" class="charts"><section class="graph"><h2>Latency (ms)<small>Average · faint whiskers show min–max</small></h2><canvas id="chart" role="img" aria-label="Average latency history in milliseconds"></canvas></section><section id="jitterPanel" class="graph"><h2>Jitter (ms)</h2><canvas id="jitterChart" role="img" aria-label="Jitter history in milliseconds"></canvas></section><div id="inspection" role="status" hidden></div></div>
        <small id="chartInfo" aria-live="polite"></small><small class="chart-help">Scroll to zoom · drag to select · double-click to reset. Fit uses the selected History range; gaps remain empty.</small></div>
        <details><summary>Saved monitoring sessions</summary><div id="sessions"></div></details></main><footer>Measurements from the desktop instance. Red history bands indicate diagnostic response loss, not proven forwarding loss. <a href="/api/targets">Targets API</a> · <a href="/api/interfaces">Interfaces API</a></footer>
        <script>
        const $=id=>document.getElementById(id),fmt=v=>Number.isFinite(v)?v.toFixed(1):'—',escape=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
        const MIN_WINDOW=10000,MAX_WINDOW=86400000,MAX_POINTS=1000,LEFT=56,RIGHT=12;
        let targets=[],savedSessions=[],selected=null,historyRequest=0,historyPendingKey=null,controller=null,zoomTimer=null,chartData=null,scopeData=null,scope=null,inspection=null,drag=null,emptyMessage='No samples in this time range';
        // Both canvases use this one timeline and the same response. All durations are milliseconds.
        const timeline={from:0,to:0,duration:3600000,live:true};
        const baseDuration=()=>Number($('range').value)*60000,selectionKey=()=>`${selected}|${$('hop').value||0}`;
        const jitterPreference='pngr.dashboard.showJitter';let showJitter=true;try{showJitter=localStorage.getItem(jitterPreference)!=='false'}catch{}$('showJitter').checked=showJitter;
        function latestTime(){const stopped=Date.parse(savedSessions.find(s=>s.id===selected)?.stoppedAt);return !targets.some(t=>t.id===selected)&&Number.isFinite(stopped)?Math.min(Date.now(),stopped):Date.now()}
        function resetScope(){scope={from:Math.max(0,timeline.to-baseDuration()),to:timeline.to};scopeData=null}
        function stopRequest(){historyRequest++;controller?.abort();controller=null;historyPendingKey=null;clearTimeout(zoomTimer);zoomTimer=null}
        function clearInspection(){inspection=null;$('inspection').hidden=true}
        function setWindow(from,to,live=false){if(!Number.isFinite(from)||!Number.isFinite(to)||to<=from)return;const duration=Math.min(MAX_WINDOW,Math.max(MIN_WINDOW,to-from));timeline.to=Math.max(duration,Math.min(Date.now(),to));timeline.from=timeline.to-duration;timeline.duration=duration;timeline.live=live;clearInspection();$('live').setAttribute('aria-pressed',String(live))}
        function choose(id){stopRequest();selected=id;chartData=null;$('hop').innerHTML='<option value="0">Destination</option>';$('detail').style.display='block';updateDetail();setWindow(latestTime()-baseDuration(),latestTime(),true);resetScope();history()}
        function updateDetail(){const t=targets.find(x=>x.id===selected);if(!t){$('hops').querySelector('tbody').replaceChildren();return;}$('title').textContent=t.target+' · '+t.resolvedAddress;const hops=t.snapshot?.hops??[],old=$('hop').value;$('hop').innerHTML='<option value="0">Destination</option>'+hops.map(h=>`<option value="${h.hop}">${h.hop} · ${escape(h.address??'No response')}</option>`).join('');$('hop').value=[...$('hop').options].some(o=>o.value===old)?old:'0';if(old&&old!==$('hop').value){stopRequest();chartData=null;resetScope()}$('hops').querySelector('tbody').innerHTML=hops.map(h=>`<tr><td>${h.hop}</td><td>${escape(h.address??'*')}</td><td>${escape(h.hostname)}</td><td>${fmt(h.averageMs)}</td><td>${fmt(h.minimumMs)}</td><td>${fmt(h.maximumMs)}</td><td>${fmt(h.lossPercent)}%</td><td>${fmt(h.jitterMs)}</td><td><div class="latency" style="width:${Math.min(200,Math.max(1,h.averageMs??0))}px"></div></td></tr>`).join('')}
        async function refresh(){try{const response=await fetch('/api/targets');if(response.status===401){location.replace('/login');return}if(!response.ok)throw Error();targets=await response.json();$('targets').innerHTML=targets.map(t=>{const d=t.snapshot?.destination;return `<tr data-id="${t.id}"><td><button>${escape(t.target)}</button> <small>${escape(t.resolvedAddress)}</small></td><td class="${t.health.toLowerCase()}">${escape(t.health)}</td><td>${escape(t.options.protocol)}</td><td>${fmt(d?.lastMs)} ms</td><td>${fmt(d?.averageMs)} ms</td><td>${fmt(d?.lossPercent)}%</td><td>${fmt(d?.jitterMs)} ms</td><td>${t.snapshot?.round??'—'}</td><td>${t.snapshot?.routeChanged?'Changed':'—'}</td></tr>`}).join('');$('targets').querySelectorAll('tr').forEach(r=>r.onclick=()=>choose(r.dataset.id));$('message').textContent=targets.length+' targets · '+new Date().toLocaleTimeString();updateDetail();if(selected&&!historyPendingKey&&!zoomTimer&&(timeline.live||!chartData)){if(timeline.live){setWindow(latestTime()-timeline.duration,latestTime(),true);resetScope()}await history()}}catch{$('message').textContent='Desktop connection unavailable'}}
        function currentData(){return chartData?.key===selectionKey()?chartData:null}
        function visiblePoints(){return (currentData()?.points??[]).filter(p=>p.time>=timeline.from&&p.time<=timeline.to)}
        function describe(data){$('chartInfo').textContent=`${new Date(timeline.from).toLocaleString()} — ${new Date(timeline.to).toLocaleString()} · ${data.sampleCount} samples · ${timeline.live?'Live':'Inspecting'}${data.truncated?' · result truncated':''}`}
        async function history(fit=false){
            if(!selected)return;const key=selectionKey(),from=fit?scope.from:timeline.from,to=fit?scope.to:timeline.to,pending=`${key}|${from}|${to}|${fit}`;
            if(historyPendingKey===pending)return;
            stopRequest();const request=historyRequest;historyPendingKey=pending;controller=new AbortController();
            if(!currentData())emptyMessage='Loading history…';$('chartInfo').textContent='Loading history…';redraw();
            try{const response=await fetch(`/api/history/${encodeURIComponent(selected)}?hop=${$('hop').value||0}&from=${encodeURIComponent(new Date(from).toISOString())}&to=${encodeURIComponent(new Date(to).toISOString())}&maxPoints=${MAX_POINTS}`,{signal:controller.signal});if(response.status===401){location.replace('/login');return}if(!response.ok)throw Error();const result=await response.json();if(request!==historyRequest||key!==selectionKey())return;
                const data={...result,key,from,to,bucketMs:Math.max(1,(to-from+1)/MAX_POINTS),points:result.points.map(p=>({...p,time:Date.parse(p.timestamp)})).filter(p=>Number.isFinite(p.time)).sort((a,b)=>a.time-b.time)};
                if(from===scope.from&&to===scope.to)scopeData=data;chartData=data;emptyMessage='No samples in this time range';if(fit)fitData(data);else{describe(data);redraw()}
            }catch(error){if(request!==historyRequest||error.name==='AbortError')return;chartData=null;emptyMessage='History not available';$('chartInfo').textContent=emptyMessage;redraw()}
            finally{if(request===historyRequest){historyPendingKey=null;controller=null}}
        }
        function requestWindow(debounce=false){stopRequest();redraw();$('chartInfo').textContent='Updating history…';if(debounce)zoomTimer=setTimeout(()=>{zoomTimer=null;history()},120);else history()}
        function resetZoom(){setWindow(timeline.to-baseDuration(),timeline.to,timeline.live);resetScope();requestWindow()}
        function navigate(direction){const end=Math.min(latestTime(),timeline.to+direction*timeline.duration);setWindow(end-timeline.duration,end,false);resetScope();requestWindow()}
        function zoom(factor,anchor=.5){
            const duration=Math.max(MIN_WINDOW,Math.min(MAX_WINDOW,timeline.duration*factor));if(duration===timeline.duration)return;
            const at=timeline.from+timeline.duration*anchor;let from=at-duration*anchor,to=from+duration;
            // Zoom cannot wander into older retained history implicitly. Earlier/Later explicitly establish a new scope.
            let low=Math.min(timeline.from,scope.from),high=Math.max(timeline.to,scope.to);
            if(scopeData?.points.length){const padding=Math.max(MIN_WINDOW/2,(scopeData.points.at(-1).time-scopeData.points[0].time)*.05);low=Math.min(timeline.from,Math.max(scope.from,scopeData.points[0].time-padding));high=Math.max(timeline.to,Math.min(scope.to,scopeData.points.at(-1).time+padding))}
            const span=Math.min(duration,high-low),points=visiblePoints();
            // A short new session can sit at the edge of a mostly empty window. Keep its samples
            // visible instead of magnifying the empty time beside them. Populated views retain cursor anchoring.
            if(factor<1&&points.length){
                const data=currentData(),first=points[0].time;
                // Timestamps mark the first sample in each bucket; its remaining samples must stay visible too.
                const last=Math.min(timeline.to,data.from+(Math.floor((points.at(-1).time-data.from)/data.bucketMs)+1)*data.bucketMs),recordedSpan=last-first;
                if(recordedSpan<=span)from=Math.max(last-span,Math.min(from,first));
                else if(from+span<first)from=first-span*.05;
                else if(from>last)from=last-span*.95;
            }
            from=Math.max(low,Math.min(from,high-span));to=from+span;if(from===timeline.from&&to===timeline.to)return;
            setWindow(from,to,false);requestWindow(true)
        }
        function fitData(data){if(!data.points.length){$('chartInfo').textContent='No samples to fit in the selected History range';redraw();return}const first=data.points[0].time,last=data.points.at(-1).time,pad=Math.min(Math.max(MIN_WINDOW/2,(last-first)*.05),(MAX_WINDOW-(last-first))/2);setWindow(first-pad,last+pad,false);describe(data);$('chartInfo').textContent+=' · Fit within selected History range';redraw()}
        function fitHistory(){stopRequest();if(scopeData?.key===selectionKey()){chartData=scopeData;fitData(scopeData)}else history(true)}
        function niceScale(values,minimum){const peak=Math.max(minimum,...values.filter(v=>Number.isFinite(v)&&v>=0)),raw=peak/4,power=10**Math.floor(Math.log10(raw)),fraction=raw/power,step=([1,2,2.5,5,10].find(n=>n>=fraction)??10)*power;return {step,max:Math.ceil(peak/step)*step}}
        function cadence(){const value=targets.find(t=>t.id===selected)?.snapshot?.samplingInterval??targets.find(t=>t.id===selected)?.options?.interval;if(typeof value==='number')return value;const match=String(value??'').match(/^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/);return match?((Number(match[1]??0)*24+Number(match[2]))*3600+Number(match[3])*60+Number(match[4]))*1000:0}
        function gapLimit(){return Math.max(currentData()?.bucketMs??1,cadence())*1.8}
        function contiguous(a,b){const data=currentData(),adjacent=cadence()>data.bucketMs||Math.floor((b.time-data.from)/data.bucketMs)-Math.floor((a.time-data.from)/data.bucketMs)<=1;return adjacent&&b.time-a.time<=gapLimit()&&a.measurementEpoch!==-1&&b.measurementEpoch!==-1&&a.measurementEpoch===b.measurementEpoch}
        function dimensions(canvas){return {w:canvas.clientWidth,h:canvas.clientHeight,left:LEFT,right:canvas.clientWidth-RIGHT,top:10,bottom:canvas.clientHeight-24}}
        function xAt(time,d){return d.left+(time-timeline.from)/timeline.duration*(d.right-d.left)}
        function redraw(){
            $('jitterPanel').hidden=!showJitter;$('charts').classList.toggle('jitter-hidden',!showJitter);const points=visiblePoints();
            drawGraph($('chart'),points,'averageMs','#c9d9ca',!showJitter);if(showJitter)drawGraph($('jitterChart'),points,'jitterMs','#e7a55d',true)
        }
        function drawGraph(canvas,points,field,color,timeLabels){
            const ratio=devicePixelRatio||1,d=dimensions(canvas);canvas.width=Math.round(d.w*ratio);canvas.height=Math.round(d.h*ratio);const ctx=canvas.getContext('2d');ctx.scale(ratio,ratio);
            const values=points.flatMap(p=>field==='averageMs'?[p.averageMs,p.minimumMs,p.maximumMs]:[p.jitterMs]),scale=niceScale(values,field==='averageMs'?1:.1),y=v=>d.bottom-v/scale.max*(d.bottom-d.top);
            ctx.strokeStyle='#363a43';ctx.fillStyle='#9a9da8';ctx.font='10px system-ui';
            const label=v=>Number(v.toFixed(scale.step<1?Math.min(4,Math.ceil(-Math.log10(scale.step))+1):1)).toString();
            for(let value=0;value<=scale.max+scale.step*.01;value+=scale.step){const py=y(value);ctx.beginPath();ctx.moveTo(d.left,py);ctx.lineTo(d.right,py);ctx.stroke();ctx.fillText(label(value),4,py+3)}
            ctx.save();ctx.beginPath();ctx.rect(d.left,d.top,d.right-d.left,d.bottom-d.top);ctx.clip();
            const bucket=currentData()?.bucketMs??1;
            for(const point of points){const x=xAt(point.time,d);if(point.lossPercent>0){ctx.fillStyle=`rgba(240,75,80,${.12+Math.min(100,point.lossPercent)/200})`;ctx.fillRect(x,d.top,Math.max(2,bucket/timeline.duration*(d.right-d.left)),d.bottom-d.top)}
                if(field==='averageMs'&&Number.isFinite(point.averageMs)&&Number.isFinite(point.minimumMs)&&Number.isFinite(point.maximumMs)){ctx.strokeStyle='#c9d9ca55';ctx.beginPath();ctx.moveTo(x,y(point.minimumMs));ctx.lineTo(x,y(point.maximumMs));ctx.stroke()}}
            ctx.strokeStyle=color;ctx.lineWidth=1.2;ctx.beginPath();let previous=null;
            for(const point of points){const value=point[field];if(!Number.isFinite(value)||value<0){previous=null;continue}const x=xAt(point.time,d),py=y(value);if(previous&&contiguous(previous,point))ctx.lineTo(x,py);else ctx.moveTo(x,py);previous=point}ctx.stroke();
            // A single measurement (or an isolated bucket) must still be visible.
            ctx.fillStyle=color;for(const point of points)if(Number.isFinite(point[field])&&point[field]>=0){ctx.beginPath();ctx.arc(xAt(point.time,d),y(point[field]),1.25,0,Math.PI*2);ctx.fill()}
            if(inspection){const x=xAt(inspection.time,d);ctx.strokeStyle='#b6bdc988';ctx.setLineDash([3,3]);ctx.beginPath();ctx.moveTo(x,d.top);ctx.lineTo(x,d.bottom);ctx.stroke();ctx.setLineDash([])}
            if(drag){ctx.fillStyle='#99b4cd22';const a=xAt(drag.start,d),b=xAt(drag.end,d);ctx.fillRect(Math.min(a,b),d.top,Math.abs(a-b),d.bottom-d.top)}ctx.restore();
            ctx.fillStyle='#9a9da8';if(!points.length){ctx.textAlign='center';ctx.fillText(emptyMessage,(d.left+d.right)/2,(d.top+d.bottom)/2);ctx.textAlign='left'}
            if(timeLabels){const count=d.w<500?2:4;for(let i=0;i<=count;i++){const time=timeline.from+timeline.duration*i/count;ctx.textAlign=i===0?'left':i===count?'right':'center';ctx.fillText(new Date(time).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',...(timeline.duration<120000?{second:'2-digit'}:{})}),d.left+(d.right-d.left)*i/count,d.h-6)}ctx.textAlign='left'}
            canvas.setAttribute('aria-label',`${field==='averageMs'?'Average latency, with minimum and maximum':'Jitter'} history in milliseconds${points.length?'':': '+emptyMessage}`)
        }
        function timeAt(event,canvas){const d=dimensions(canvas),x=Math.min(d.right,Math.max(d.left,event.clientX-canvas.getBoundingClientRect().left));return timeline.from+(x-d.left)/(d.right-d.left)*timeline.duration}
        function inspect(event,canvas){
            const time=timeAt(event,canvas),data=currentData();inspection={time};redraw();const tooltip=$('inspection');tooltip.hidden=false;
            let point=null;
            if(data){const slot=Math.floor((time-data.from)/data.bucketMs);point=data.points.find(p=>Math.floor((p.time-data.from)/data.bucketMs)===slot)}
            const stamp=new Date(point?.time??time).toLocaleString(),aggregated=point&&(point.count+point.errors>1||point.received>1),value=n=>Number.isFinite(n)?`${n.toFixed(1)} ms`:'Unavailable';
            tooltip.textContent=point?`${stamp}${aggregated?'\nBucket averages · '+(point.count+point.errors)+' attempts':''}\nAverage latency: ${value(point.averageMs)}\nJitter: ${value(point.jitterMs)}${aggregated?'\nLatency min–max: '+value(point.minimumMs)+' – '+value(point.maximumMs):''}`:`${stamp}\nLatency: Unavailable\nJitter: Unavailable\nNo recorded measurement here`;
            const box=tooltip.getBoundingClientRect();tooltip.style.left=Math.max(8,Math.min(innerWidth-box.width-8,event.clientX+12))+'px';tooltip.style.top=Math.max(8,Math.min(innerHeight-box.height-8,event.clientY+12))+'px'
        }
        for(const canvas of [$('chart'),$('jitterChart')]){
            canvas.addEventListener('wheel',event=>{if(event.deltaY===0)return;event.preventDefault();zoom(Math.exp(Math.max(-.4,Math.min(.4,event.deltaY*.002))),Math.max(0,Math.min(1,(timeAt(event,canvas)-timeline.from)/timeline.duration)))},{passive:false});
            canvas.addEventListener('pointerdown',event=>{if(event.pointerType!=='mouse'||event.button!==0)return;const time=timeAt(event,canvas);drag={start:time,end:time,x:event.clientX,pointer:event.pointerId};canvas.setPointerCapture(event.pointerId);clearInspection()});
            canvas.addEventListener('pointermove',event=>{if(drag){drag.end=timeAt(event,canvas);redraw()}else if(event.pointerType==='mouse')inspect(event,canvas)});
            canvas.addEventListener('pointerup',event=>{if(!drag){if(event.pointerType!=='mouse')inspect(event,canvas);return}const selection=drag;drag=null;if(canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);if(Math.abs(event.clientX-selection.x)>=8&&Math.abs(selection.end-selection.start)>=MIN_WINDOW){setWindow(Math.min(selection.start,selection.end),Math.max(selection.start,selection.end),false);requestWindow(true)}else redraw()});
            canvas.addEventListener('pointercancel',()=>{drag=null;clearInspection();redraw()});canvas.addEventListener('pointerleave',event=>{if(event.pointerType==='mouse'&&!drag){clearInspection();redraw()}});canvas.addEventListener('dblclick',resetZoom)
        }
        $('showJitter').onchange=()=>{showJitter=$('showJitter').checked;try{localStorage.setItem(jitterPreference,String(showJitter))}catch{}redraw()};
        $('hop').onchange=()=>{stopRequest();chartData=null;resetScope();clearInspection();history()};$('range').onchange=resetZoom;$('older').onclick=()=>navigate(-1);$('newer').onclick=()=>navigate(1);$('zoomIn').onclick=()=>zoom(.5);$('zoomOut').onclick=()=>zoom(2);$('resetZoom').onclick=resetZoom;$('fit').onclick=fitHistory;$('live').onclick=()=>{setWindow(latestTime()-timeline.duration,latestTime(),true);resetScope();requestWindow()};window.onresize=redraw;
        window.addEventListener('scroll',()=>{if(inspection){clearInspection();redraw()}},{passive:true});
        new ResizeObserver(redraw).observe($('charts'));
        fetch('/api/sessions').then(r=>r.ok?r.json():[]).then(s=>{savedSessions=s;$('sessions').innerHTML=s.map(t=>`<p><button data-id="${t.id}">${escape(t.target)}</button> <small>${escape(new Date(t.startedAt).toLocaleString())} · ${t.sampleCount} hop samples</small></p>`).join('');$('sessions').querySelectorAll('button').forEach(b=>b.onclick=()=>{choose(b.dataset.id);$('title').textContent=s.find(t=>t.id===selected)?.target??'Saved session'})}).catch(()=>{});refresh();setInterval(refresh,2500);
        </script></body></html>
        """;
}
