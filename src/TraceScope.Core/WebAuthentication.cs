using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace TraceScope.Core;

/// <summary>Process-local web credentials. No access key or browser session survives in this object after rotation.</summary>
internal sealed class WebAuthentication(string verifier, TimeProvider clock)
{
    internal const string SessionCookie = "pngr_session";
    internal const string LoginCookie = "pngr_login";
    private readonly object _gate = new();
    private string _verifier = verifier;
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, Attempts> _attempts = [];
    private DateTimeOffset _globalWindow = clock.GetUtcNow();
    private int _globalAttempts;
    private sealed record Session(DateTimeOffset Created, DateTimeOffset LastUsed, string Csrf);
    private sealed record Attempts(DateTimeOffset Since, int Count);
    private static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static bool IsToken(string? token) => token is { Length: 64 }
        && token.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool Equal(string? left, string? right) => left is { Length: 64 } && right is { Length: 64 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    public void Rotate(string newVerifier)
    {
        if (!WebAccessKey.IsValidVerifier(newVerifier)) throw new ArgumentException("Invalid access key verifier.", nameof(newVerifier));
        lock (_gate) { _verifier = newVerifier; _sessions.Clear(); }
    }

    private void PruneSessions(DateTimeOffset now)
    {
        foreach (var id in _sessions.Where(p => now - p.Value.Created >= TimeSpan.FromHours(12)
            || now - p.Value.LastUsed >= TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray()) _sessions.Remove(id);
    }

    private bool SessionAuthenticated(HttpContext context)
    {
        var token = context.Request.Cookies[SessionCookie];
        if (token is not { Length: 64 }) return false;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            PruneSessions(now);
            var id = Digest(token);
            if (!_sessions.TryGetValue(id, out var session)) return false;
            _sessions[id] = session with { LastUsed = now };
            context.Items["WebCsrf"] = session.Csrf;
            return true;
        }
    }

    private bool RateLimited(HttpContext context, bool count)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now - _globalWindow >= TimeSpan.FromMinutes(5)) { _globalWindow = now; _globalAttempts = 0; }
            foreach (var id in _attempts.Where(p => now - p.Value.Since >= TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray())
                _attempts.Remove(id);
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _attempts.TryGetValue(address, out var entry);
            if ((entry?.Count ?? 0) >= 10 || _globalAttempts >= 120 || (_attempts.Count >= 1024 && entry is null)) return true;
            if (count)
            {
                _attempts[address] = new Attempts(entry?.Since ?? now, (entry?.Count ?? 0) + 1);
                _globalAttempts++;
            }
            return false;
        }
    }

    private static bool SameOrigin(HttpRequest request)
    {
        if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site") return false;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // Token validation also covers clients without Origin.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.GetLeftPart(UriPartial.Authority).Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    private static CookieOptions Cookie(HttpContext context) => new()
    { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/", IsEssential = true };

    public async Task HandleAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var path = request.Path;
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsGet(request.Method))
            {
                // A favicon redirect or another login tab must not invalidate a form
                // already displayed by this browser. Keep its well-formed double-submit
                // token until the cookie expires or successful login deletes it.
                var existing = request.Cookies[LoginCookie];
                var token = IsToken(existing) ? existing! : Token();
                var options = Cookie(context); options.MaxAge = TimeSpan.FromMinutes(5);
                context.Response.Cookies.Append(LoginCookie, token, options);
                await Html(context, LoginPage(token));
                return;
            }
            if (!HttpMethods.IsPost(request.Method)) { context.Response.StatusCode = 405; return; }
            if (RateLimited(context, true)) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "300"; return; }
            if (!SameOrigin(request) || !request.HasFormContentType) { context.Response.StatusCode = 400; return; }
            IFormCollection form;
            try { form = await request.ReadFormAsync(context.RequestAborted); }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
            { context.Response.StatusCode = 400; return; }
            if (!Equal(form["csrf"], request.Cookies[LoginCookie])) { context.Response.StatusCode = 400; return; }
            lock (_gate)
            {
                if (!WebAccessKey.Verify(form["key"], _verifier)) { context.Response.StatusCode = 401; }
                else
                {
                    var now = clock.GetUtcNow(); PruneSessions(now);
                    // Bound memory even when a valid client repeatedly signs in.
                    if (_sessions.Count >= 256) _sessions.Remove(_sessions.MinBy(p => p.Value.LastUsed).Key);
                    var token = Token();
                    _sessions[Digest(token)] = new Session(now, now, Token());
                    context.Response.Cookies.Append(SessionCookie, token, Cookie(context));
                    context.Response.Cookies.Delete(LoginCookie, Cookie(context));
                    context.Response.Redirect("/");
                }
            }
            if (context.Response.StatusCode == 401) await Html(context, LoginPage(form["csrf"].ToString(), true));
            return;
        }
        var authenticated = SessionAuthenticated(context);
        if (path.Equals("/logout", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsPost(request.Method)) { context.Response.StatusCode = 405; return; }
            if (!authenticated) { context.Response.StatusCode = 401; return; }
            if (!SameOrigin(request) || !request.HasFormContentType) { context.Response.StatusCode = 400; return; }
            IFormCollection form;
            try { form = await request.ReadFormAsync(context.RequestAborted); }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
            { context.Response.StatusCode = 400; return; }
            if (!Equal(form["csrf"], context.Items["WebCsrf"] as string)) { context.Response.StatusCode = 400; return; }
            lock (_gate) _sessions.Remove(Digest(request.Cookies[SessionCookie]!));
            context.Response.Cookies.Delete(SessionCookie, Cookie(context));
            context.Response.Redirect("/login"); return;
        }
        if (!authenticated)
        {
            var authorization = request.Headers.Authorization.ToString();
            lock (_gate) authenticated = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && WebAccessKey.Verify(authorization[7..], _verifier);
            if (!authenticated)
            {
                if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(authorization))
                {
                    if (RateLimited(context, true))
                    { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "300"; return; }
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                }
                else context.Response.Redirect("/login");
                return;
            }
        }
        await next(context);
    }

    public static string LogoutForm(HttpContext context) => context.Items["WebCsrf"] is string csrf
        ? $"<form method=\"post\" action=\"/logout\"><input type=\"hidden\" name=\"csrf\" value=\"{csrf}\"><button>Log out</button></form>" : "";
    private static Task Html(HttpContext context, string html)
    { context.Response.ContentType = "text/html; charset=utf-8"; return context.Response.WriteAsync(html, context.RequestAborted); }
    private static string LoginPage(string token, bool invalid = false) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Pngr · Sign in</title>
        <style>:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#191a1d;color:#d8d9dc;font:14px system-ui;min-height:100vh;display:grid;place-items:center}main{width:min(380px,90vw)}h1{font-size:28px;margin-bottom:6px}p{color:#aeb1ba;line-height:1.5}label{display:block;margin-top:24px}input,button{width:100%;border:1px solid #474953;border-radius:4px;padding:11px;margin-top:8px;background:#292b31;color:#ddd;font:inherit}button{cursor:pointer;background:#365f45;border-color:#63876b}small{color:#9b9da7;line-height:1.5;display:block;margin-top:24px}.error{color:#ee8686}</style></head>
        <body><main><h1>Pngr</h1><p>Sign in to this desktop's read-only monitor.</p>{{(invalid ? "<p class=\"error\" role=\"alert\">Access key not accepted.</p>" : "")}}
        <form method="post" action="/login"><input type="hidden" name="csrf" value="{{WebUtility.HtmlEncode(token)}}"><label for="key">Access key</label><input id="key" name="key" type="password" autocomplete="off" maxlength="69" required autofocus spellcheck="false"><button type="submit">Sign in</button></form>
        <small>Find the generated key in Pngr's Web Server settings. HTTP traffic is not encrypted. Use a trusted network or an encrypted VPN/tunnel.</small></main></body></html>
        """;
}
