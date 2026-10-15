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
        <style>:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#191a1d;color:#d8d9dc;font:12px system-ui}header{padding:12px 18px;background:#24262b;border-bottom:1px solid #40434b;display:flex;gap:20px;align-items:center}h1{font-size:17px;margin:0}small{color:#9b9da7}main{padding:12px 18px}table{width:100%;border-collapse:collapse;margin-bottom:16px}th,td{padding:5px 8px;border-bottom:1px solid #303239;text-align:right;white-space:nowrap}th{background:#24262b;color:#a7a9b2;font-weight:500}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}tr:hover{background:#282b33}button,select,input{background:#292b31;color:#ddd;border:1px solid #474953;padding:5px 8px;border-radius:3px}button{cursor:pointer}.healthy{color:#58cf88}.degraded{color:#f2bf55}.unreachable,.error{color:#ee6868}.panel{display:none}.toolbar{display:flex;align-items:center;gap:8px;margin:12px 0}canvas{width:100%;height:220px;background:#212328;border:1px solid #3a3d46}#message{color:#aeb1ba}a{color:#80b6f5}.latency{height:3px;background:#6cc48c;min-width:1px;max-width:200px}#hops th:nth-child(3),#hops td:nth-child(3){text-align:left}footer{padding:10px 18px;color:#989ba7}</style></head>
        <body><header><h1>Pngr</h1><small>LOCAL MONITOR · READ ONLY</small><span id="message">Connecting…</span><!--AUTH--></header><main>
        <table><thead><tr><th>Target</th><th>Health</th><th>Protocol</th><th>Current</th><th>Average</th><th>Response loss</th><th>Jitter</th><th>Round</th><th>Route</th></tr></thead><tbody id="targets"></tbody></table>
        <div id="detail" class="panel"><div class="toolbar"><strong id="title"></strong><label>Hop <select id="hop"></select></label><label>History <select id="range"><option value="5">5 minutes</option><option value="30">30 minutes</option><option value="60" selected>1 hour</option><option value="360">6 hours</option><option value="1440">24 hours</option></select></label><button id="older">← Earlier</button><button id="newer">Later →</button><button id="live">Live</button></div>
        <table id="hops"><thead><tr><th>Hop</th><th>Address</th><th>Name</th><th>Avg</th><th>Min</th><th>Max</th><th>Response loss</th><th>Jitter</th><th>Latency</th></tr></thead><tbody></tbody></table><canvas id="chart"></canvas><small id="chartInfo"></small></div>
        <details><summary>Saved monitoring sessions</summary><div id="sessions"></div></details></main><footer>Measurements from the desktop instance. Red history bands indicate diagnostic response loss, not proven forwarding loss. <a href="/api/targets">Targets API</a> · <a href="/api/interfaces">Interfaces API</a></footer>
        <script>
        const $=id=>document.getElementById(id),fmt=v=>v==null?'—':Number(v).toFixed(1),escape=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));let targets=[],selected=null,end=null;
        function choose(id){selected=id;end=null;$('detail').style.display='block';updateDetail();history()}
        function updateDetail(){const t=targets.find(x=>x.id===selected);if(!t)return;$('title').textContent=t.target+' · '+t.resolvedAddress;const hops=t.snapshot?.hops??[];const old=$('hop').value;$('hop').innerHTML='<option value="0">Destination</option>'+hops.map(h=>`<option value="${h.hop}">${h.hop} · ${escape(h.address??'No response')}</option>`).join('');$('hop').value=old||'0';$('hops').querySelector('tbody').innerHTML=hops.map(h=>`<tr><td>${h.hop}</td><td>${escape(h.address??'*')}</td><td>${escape(h.hostname)}</td><td>${fmt(h.averageMs)}</td><td>${fmt(h.minimumMs)}</td><td>${fmt(h.maximumMs)}</td><td>${fmt(h.lossPercent)}%</td><td>${fmt(h.jitterMs)}</td><td><div class="latency" style="width:${Math.min(200,Math.max(1,h.averageMs??0))}px"></div></td></tr>`).join('')}
        async function refresh(){try{const response=await fetch('/api/targets');if(response.status===401){location.replace('/login');return}targets=await response.json();$('targets').innerHTML=targets.map(t=>{const d=t.snapshot?.destination;return `<tr data-id="${t.id}"><td><button>${escape(t.target)}</button> <small>${escape(t.resolvedAddress)}</small></td><td class="${t.health.toLowerCase()}">${escape(t.health)}</td><td>${escape(t.options.protocol)}</td><td>${fmt(d?.lastMs)} ms</td><td>${fmt(d?.averageMs)} ms</td><td>${fmt(d?.lossPercent)}%</td><td>${fmt(d?.jitterMs)} ms</td><td>${t.snapshot?.round??'—'}</td><td>${t.snapshot?.routeChanged?'Changed':'—'}</td></tr>`}).join('');$('targets').querySelectorAll('tr').forEach(r=>r.onclick=()=>choose(r.dataset.id));$('message').textContent=targets.length+' targets · '+new Date().toLocaleTimeString();updateDetail();if(selected)await history()}catch(e){$('message').textContent='Desktop connection unavailable'}}
        async function history(){if(!selected)return;const to=end??new Date(),from=new Date(to.getTime()-Number($('range').value)*60000);try{const r=await fetch(`/api/history/${selected}?hop=${$('hop').value||0}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&maxPoints=1000`);if(!r.ok)throw Error('History not available');const data=await r.json();draw(data.points,from,to);$('chartInfo').textContent=`${from.toLocaleString()} — ${to.toLocaleString()} · ${data.sampleCount} samples${data.truncated?' · result truncated':''}`}catch(e){$('chartInfo').textContent=e.message}}
        function draw(points,from,to){const c=$('chart'),ratio=devicePixelRatio||1;c.width=c.clientWidth*ratio;c.height=220*ratio;const ctx=c.getContext('2d');ctx.scale(ratio,ratio);const w=c.clientWidth,h=220,left=42,right=w-8,top=12,bottom=h-22,max=Math.max(50,...points.map(p=>p.maximumMs??0));ctx.strokeStyle='#363a43';ctx.fillStyle='#9a9da8';ctx.font='10px system-ui';for(let i=0;i<=4;i++){let y=bottom-(bottom-top)*i/4;ctx.beginPath();ctx.moveTo(left,y);ctx.lineTo(right,y);ctx.stroke();ctx.fillText((max*i/4).toFixed(0),3,y+4)}const x=p=>left+(new Date(p.timestamp)-from)/(to-from)*(right-left),y=v=>bottom-v/max*(bottom-top);for(let p of points)if(p.lossPercent>0){ctx.fillStyle=`rgba(240,75,80,${.12+p.lossPercent/200})`;ctx.fillRect(x(p),top,Math.max(2,(right-left)/points.length),bottom-top)}ctx.strokeStyle='#c9d9ca';ctx.beginPath();let active=false;for(let p of points){if(p.averageMs==null){active=false;continue}if(active)ctx.lineTo(x(p),y(p.averageMs));else ctx.moveTo(x(p),y(p.averageMs));active=true}ctx.stroke();ctx.strokeStyle='#e7a55d';ctx.beginPath();active=false;for(let p of points){if(p.jitterMs==null){active=false;continue}if(active)ctx.lineTo(x(p),y(p.jitterMs));else ctx.moveTo(x(p),y(p.jitterMs));active=true}ctx.stroke();ctx.fillStyle='#9a9da8';ctx.fillText(from.toLocaleTimeString(),left,h-5);ctx.fillText(to.toLocaleTimeString(),Math.max(left,right-65),h-5)}
        $('hop').onchange=history;$('range').onchange=history;$('older').onclick=()=>{end=new Date((end??new Date()).getTime()-Number($('range').value)*60000);history()};$('newer').onclick=()=>{end=new Date(Math.min(Date.now(),(end??new Date()).getTime()+Number($('range').value)*60000));history()};$('live').onclick=()=>{end=null;history()};window.onresize=history;
        fetch('/api/sessions').then(r=>r.json()).then(s=>{$('sessions').innerHTML=s.map(t=>`<p><button data-id="${t.id}">${escape(t.target)}</button> <small>${escape(new Date(t.startedAt).toLocaleString())} · ${t.sampleCount} hop samples</small></p>`).join('');$('sessions').querySelectorAll('button').forEach(b=>b.onclick=()=>{choose(b.dataset.id);$('title').textContent=s.find(t=>t.id===selected)?.target??'Saved session'})});refresh();setInterval(refresh,2500);
        </script></body></html>
        """;
}
