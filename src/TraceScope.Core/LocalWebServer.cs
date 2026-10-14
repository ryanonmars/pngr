using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace TraceScope.Core;

/// <summary>A read-only view of this engine, deliberately bound only to IPv4 loopback.</summary>
public sealed class LocalWebServer(TraceEngine engine) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _application;
    private bool _disposed;
    public string? Url { get; private set; }

    public async Task StartAsync(int port = 8787, CancellationToken cancellationToken = default)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null) return;
            // This server belongs to the desktop process. Do not import appsettings/environment
            // endpoints or install ConsoleLifetime's competing SIGTERM/Ctrl+C handlers.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            builder.Services.AddRouting();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                // Host validation also blocks browser DNS rebinding to a hostname on another origin.
                var host = context.Request.Host.Host;
                if (host is not "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'";
                await next(context);
            });
            app.MapGet("/", () => Results.Content(Dashboard, "text/html; charset=utf-8"));
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
                Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
                _application = app;
            }
            catch { await app.DisposeAsync(); throw; }
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

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            var app = _application;
            _application = null;
            Url = null;
            if (app is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token); }
            finally { await app.DisposeAsync(); }
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
        <body><header><h1>Pngr</h1><small>LOCAL MONITOR · READ ONLY</small><span id="message">Connecting…</span></header><main>
        <table><thead><tr><th>Target</th><th>Health</th><th>Protocol</th><th>Current</th><th>Average</th><th>Response loss</th><th>Jitter</th><th>Round</th><th>Route</th></tr></thead><tbody id="targets"></tbody></table>
        <div id="detail" class="panel"><div class="toolbar"><strong id="title"></strong><label>Hop <select id="hop"></select></label><label>History <select id="range"><option value="5">5 minutes</option><option value="30">30 minutes</option><option value="60" selected>1 hour</option><option value="360">6 hours</option><option value="1440">24 hours</option></select></label><button id="older">← Earlier</button><button id="newer">Later →</button><button id="live">Live</button></div>
        <table id="hops"><thead><tr><th>Hop</th><th>Address</th><th>Name</th><th>Avg</th><th>Min</th><th>Max</th><th>Response loss</th><th>Jitter</th><th>Latency</th></tr></thead><tbody></tbody></table><canvas id="chart"></canvas><small id="chartInfo"></small></div>
        <details><summary>Saved monitoring sessions</summary><div id="sessions"></div></details></main><footer>Measurements from the desktop instance. Red history bands indicate diagnostic response loss, not proven forwarding loss. <a href="/api/targets">Targets API</a> · <a href="/api/interfaces">Interfaces API</a></footer>
        <script>
        const $=id=>document.getElementById(id),fmt=v=>v==null?'—':Number(v).toFixed(1),escape=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));let targets=[],selected=null,end=null;
        function choose(id){selected=id;end=null;$('detail').style.display='block';updateDetail();history()}
        function updateDetail(){const t=targets.find(x=>x.id===selected);if(!t)return;$('title').textContent=t.target+' · '+t.resolvedAddress;const hops=t.snapshot?.hops??[];const old=$('hop').value;$('hop').innerHTML='<option value="0">Destination</option>'+hops.map(h=>`<option value="${h.hop}">${h.hop} · ${escape(h.address??'No response')}</option>`).join('');$('hop').value=old||'0';$('hops').querySelector('tbody').innerHTML=hops.map(h=>`<tr><td>${h.hop}</td><td>${escape(h.address??'*')}</td><td>${escape(h.hostname)}</td><td>${fmt(h.averageMs)}</td><td>${fmt(h.minimumMs)}</td><td>${fmt(h.maximumMs)}</td><td>${fmt(h.lossPercent)}%</td><td>${fmt(h.jitterMs)}</td><td><div class="latency" style="width:${Math.min(200,Math.max(1,h.averageMs??0))}px"></div></td></tr>`).join('')}
        async function refresh(){try{targets=await(await fetch('/api/targets')).json();$('targets').innerHTML=targets.map(t=>{const d=t.snapshot?.destination;return `<tr data-id="${t.id}"><td><button>${escape(t.target)}</button> <small>${escape(t.resolvedAddress)}</small></td><td class="${t.health.toLowerCase()}">${escape(t.health)}</td><td>${escape(t.options.protocol)}</td><td>${fmt(d?.lastMs)} ms</td><td>${fmt(d?.averageMs)} ms</td><td>${fmt(d?.lossPercent)}%</td><td>${fmt(d?.jitterMs)} ms</td><td>${t.snapshot?.round??'—'}</td><td>${t.snapshot?.routeChanged?'Changed':'—'}</td></tr>`}).join('');$('targets').querySelectorAll('tr').forEach(r=>r.onclick=()=>choose(r.dataset.id));$('message').textContent=targets.length+' targets · '+new Date().toLocaleTimeString();updateDetail();if(selected)await history()}catch(e){$('message').textContent='Desktop connection unavailable'}}
        async function history(){if(!selected)return;const to=end??new Date(),from=new Date(to.getTime()-Number($('range').value)*60000);try{const r=await fetch(`/api/history/${selected}?hop=${$('hop').value||0}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&maxPoints=1000`);if(!r.ok)throw Error('History not available');const data=await r.json();draw(data.points,from,to);$('chartInfo').textContent=`${from.toLocaleString()} — ${to.toLocaleString()} · ${data.sampleCount} samples${data.truncated?' · result truncated':''}`}catch(e){$('chartInfo').textContent=e.message}}
        function draw(points,from,to){const c=$('chart'),ratio=devicePixelRatio||1;c.width=c.clientWidth*ratio;c.height=220*ratio;const ctx=c.getContext('2d');ctx.scale(ratio,ratio);const w=c.clientWidth,h=220,left=42,right=w-8,top=12,bottom=h-22,max=Math.max(50,...points.map(p=>p.maximumMs??0));ctx.strokeStyle='#363a43';ctx.fillStyle='#9a9da8';ctx.font='10px system-ui';for(let i=0;i<=4;i++){let y=bottom-(bottom-top)*i/4;ctx.beginPath();ctx.moveTo(left,y);ctx.lineTo(right,y);ctx.stroke();ctx.fillText((max*i/4).toFixed(0),3,y+4)}const x=p=>left+(new Date(p.timestamp)-from)/(to-from)*(right-left),y=v=>bottom-v/max*(bottom-top);for(let p of points)if(p.lossPercent>0){ctx.fillStyle=`rgba(240,75,80,${.12+p.lossPercent/200})`;ctx.fillRect(x(p),top,Math.max(2,(right-left)/points.length),bottom-top)}ctx.strokeStyle='#c9d9ca';ctx.beginPath();let active=false;for(let p of points){if(p.averageMs==null){active=false;continue}if(active)ctx.lineTo(x(p),y(p.averageMs));else ctx.moveTo(x(p),y(p.averageMs));active=true}ctx.stroke();ctx.strokeStyle='#e7a55d';ctx.beginPath();active=false;for(let p of points){if(p.jitterMs==null){active=false;continue}if(active)ctx.lineTo(x(p),y(p.jitterMs));else ctx.moveTo(x(p),y(p.jitterMs));active=true}ctx.stroke();ctx.fillStyle='#9a9da8';ctx.fillText(from.toLocaleTimeString(),left,h-5);ctx.fillText(to.toLocaleTimeString(),Math.max(left,right-65),h-5)}
        $('hop').onchange=history;$('range').onchange=history;$('older').onclick=()=>{end=new Date((end??new Date()).getTime()-Number($('range').value)*60000);history()};$('newer').onclick=()=>{end=new Date(Math.min(Date.now(),(end??new Date()).getTime()+Number($('range').value)*60000));history()};$('live').onclick=()=>{end=null;history()};window.onresize=history;
        fetch('/api/sessions').then(r=>r.json()).then(s=>{$('sessions').innerHTML=s.map(t=>`<p><button data-id="${t.id}">${escape(t.target)}</button> <small>${escape(new Date(t.startedAt).toLocaleString())} · ${t.sampleCount} hop samples</small></p>`).join('');$('sessions').querySelectorAll('button').forEach(b=>b.onclick=()=>{choose(b.dataset.id);$('title').textContent=s.find(t=>t.id===selected)?.target??'Saved session'})});refresh();setInterval(refresh,2500);
        </script></body></html>
        """;
}
