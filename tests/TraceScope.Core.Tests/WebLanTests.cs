using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

[Collection("Embedded web lifecycle")]
public sealed class WebLanTests
{
    [Fact]
    public void KeyGenerationUses256RandomBitsAndPersistsOnlyVerifier()
    {
        var key = WebAccessKey.Generate();
        Assert.Matches("^pngr_[0-9a-f]{64}$", key);
        Assert.NotEqual(key, WebAccessKey.Generate());
        var verifier = WebAccessKey.CreateVerifier(key);
        Assert.DoesNotContain(key, verifier);
        Assert.True(WebAccessKey.Verify(key, verifier));
        Assert.False(WebAccessKey.Verify(WebAccessKey.Generate(), verifier));
        Assert.False(WebAccessKey.Verify(key, "sha256:invalid"));
        Assert.False(WebAccessKey.Verify(null, verifier));
        Assert.Throws<ArgumentException>(() => WebAccessKey.CreateVerifier("user supplied password"));
        Assert.DoesNotContain(verifier, new WebServerOptions { AccessKeyVerifier = verifier }.ToString());
    }

    [Fact]
    public async Task DefaultOptionsPreserveUnauthenticatedExactLoopbackAndDesktopEngine()
    {
        Assert.False(new WebServerOptions().LanEnabled);
        Assert.Equal(8787, new WebServerOptions().Port);
        await using var fixture = new Fixture();
        await fixture.Server.StartAsync(0);
        Assert.Equal("127.0.0.1", new Uri(fixture.Server.Url!).Host);
        Assert.Single(fixture.Server.ListeningUrls);
        using var client = fixture.Client();
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        Assert.Contains("LOCAL MONITOR", await client.GetStringAsync("/"));
        Assert.DoesNotContain("/logout", await client.GetStringAsync("/"));
        Assert.Empty(fixture.Engine.Sessions); // No web login is involved in desktop engine access.
        using var response = await client.GetAsync("/api/interfaces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public void InterfaceSelectionRequiresAcknowledgementVerifierAndOneAssignedPrivateAddress()
    {
        var options = new WebServerOptions { LanEnabled = true, LanAddress = "192.168.30.50", HttpAcknowledged = true,
            AccessKeyVerifier = WebAccessKey.CreateVerifier(WebAccessKey.Generate()) };
        WebServerAddress[] interfaces = [new("Ethernet", "192.168.30.50"), new("Wi-Fi", "10.0.0.2"), new("IPv6", "fd00::2")];
        Assert.Equal(IPAddress.Parse("192.168.30.50"), WebServerNetwork.ValidateLanAddress(options, interfaces));
        Assert.Equal(IPAddress.Parse("10.0.0.2"), WebServerNetwork.ValidateLanAddress(options with { LanAddress = "10.0.0.2" }, interfaces));
        Assert.Equal(IPAddress.Parse("fd00::2"), WebServerNetwork.ValidateLanAddress(options with { LanAddress = "fd00::2" }, interfaces));
        Assert.Throws<InvalidOperationException>(() => WebServerNetwork.ValidateLanAddress(options with { HttpAcknowledged = false }, interfaces));
        Assert.Throws<InvalidOperationException>(() => WebServerNetwork.ValidateLanAddress(options with { AccessKeyVerifier = null }, interfaces));
        Assert.Throws<InvalidOperationException>(() => WebServerNetwork.ValidateLanAddress(options, []));
        foreach (var unsafeAddress in new[] { "0.0.0.0", "::", "127.0.0.1", "::1", "8.8.8.8", "fe80::1", "::ffff:192.168.1.2", "invalid" })
            Assert.Throws<InvalidOperationException>(() => WebServerNetwork.ValidateLanAddress(options with { LanAddress = unsafeAddress }, [new("Injected", unsafeAddress)]));
        Assert.All(WebServerNetwork.GetLanAddresses(), address => Assert.True(WebServerNetwork.IsPrivateAddress(IPAddress.Parse(address.Address))));
    }

    [Fact]
    public async Task EveryLanEndpointAndLoopbackRequiresAuthenticationAndApiRemainsReadOnly()
    {
        await using var fixture = new Fixture();
        await fixture.StartLan();
        Assert.Equal(2, fixture.Server.ListeningUrls.Count);
        Assert.DoesNotContain(fixture.Server.ListeningUrls, url => new Uri(url).Host is "0.0.0.0" or "[::]");
        foreach (var url in fixture.Server.ListeningUrls)
        {
            using var client = fixture.Client(url);
            foreach (var path in new[] { "/api/targets", "/api/sessions", "/api/interfaces", "/api/neighbors", "/api/gateway", "/api/history/invalid", "/api/events/invalid", "/API/targets", "/api/unknown" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
                Assert.Equal("", await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
            Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
            Assert.Equal("[]", await client.GetStringAsync("/api/sessions"));
            Assert.Contains("LOCAL MONITOR", await client.GetStringAsync("/"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/targets", null)).StatusCode);
            Assert.DoesNotContain(fixture.Key, await client.GetStringAsync("/api/interfaces"));
            Assert.DoesNotContain(fixture.Options.AccessKeyVerifier!, await client.GetStringAsync("/api/interfaces"));
        }
    }

    [Fact]
    public async Task MissingInvalidAndQueryCredentialsFailAndValidBearerSurvivesRateLimit()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WebAccessKey.Generate());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets?access_key=" + fixture.Key)).StatusCode);
        HttpStatusCode last = default;
        for (var i = 0; i < 12; i++) last = (await client.GetAsync("/api/targets")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
    }

    [Fact]
    public async Task BrowserLoginSessionLogoutAndCsrfAreProtected()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var client = fixture.Client();
        var login = await fixture.LoginForm(client);
        Assert.Contains("HttpOnly", login.Cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", login.Cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Key, login.Html);
        Assert.DoesNotContain("localStorage", login.Html);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/login", Form(("key", fixture.Key)))).StatusCode);
        using var wrong = await client.PostAsync("/login", Form(("csrf", login.Token), ("key", WebAccessKey.Generate())));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("Access key not accepted", await wrong.Content.ReadAsStringAsync());
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = Form(("csrf", login.Token), ("key", fixture.Key)) };
        crossSite.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/login", Form(("csrf", login.Token), ("key", fixture.Key)))).StatusCode);
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("Log out", dashboard);
        Assert.DoesNotContain(fixture.Key, dashboard);
        var logoutToken = Csrf(dashboard);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/logout", Form(("csrf", new string('0', 64))))).StatusCode);
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/logout", Form(("csrf", logoutToken)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
        Assert.Empty(fixture.Engine.Sessions);
    }

    [Fact]
    public async Task SessionsExpireAfterIdleTimeoutAndAbsoluteTwelveHours()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var client = fixture.Client();
        await fixture.Login(client);
        fixture.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
        await fixture.Login(client);
        for (var i = 0; i < 24; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(24)); // 12 hours from login despite activity.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/targets")).StatusCode);
    }

    [Fact]
    public async Task RotationInvalidatesEverySessionAndOldApiKeyImmediately()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var browser = fixture.Client(); using var otherBrowser = fixture.Client(); using var api = fixture.Client();
        await fixture.Login(browser); await fixture.Login(otherBrowser);
        var replacement = WebAccessKey.Generate();
        fixture.Server.RotateAccessKey(WebAccessKey.CreateVerifier(replacement));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/targets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/targets")).StatusCode);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/targets")).StatusCode);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacement);
        Assert.Equal("[]", await api.GetStringAsync("/api/targets"));
        var login = await fixture.LoginForm(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/login", Form(("csrf", login.Token), ("key", fixture.Key)))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/login", Form(("csrf", login.Token), ("key", replacement)))).StatusCode);
    }

    [Fact]
    public async Task HostOriginBodyLimitsAndTraversalAreRejectedWithoutCredentialLeakage()
    {
        await using var fixture = new Fixture(); await fixture.StartLan(); using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        using var host = new HttpRequestMessage(HttpMethod.Get, "/api/targets"); host.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(host)).StatusCode);
        using var origin = new HttpRequestMessage(HttpMethod.Get, "/api/targets"); origin.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(origin)).StatusCode);
        using var fetch = new HttpRequestMessage(HttpMethod.Get, "/api/targets"); fetch.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(fetch)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/login", new StringContent(new string('x', 5000)))).StatusCode);
        foreach (var path in new[] { "/.git/config", "/appsettings.json", "/%2e%2e/WorkspaceStore.cs", "/api/settings", "/api/access-key", "/favicon.ico" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(fixture.Key, await response.Content.ReadAsStringAsync());
        }
        using var dashboard = await client.GetAsync("/");
        var html = await dashboard.Content.ReadAsStringAsync();
        var nonce = Regex.Match(html, "<script nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains("'nonce-" + nonce + "'", dashboard.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task LanEnableDisableRebindAndFailedBindRemainRecoverable()
    {
        await using var fixture = new Fixture(); await fixture.Server.StartAsync(0);
        var original = fixture.Server.Url!;
        await fixture.StartLan();
        Assert.NotEqual(original, fixture.Server.Url);
        using var client = fixture.Client(); await fixture.Login(client);
        await fixture.Server.StartAsync(new WebServerOptions { Port = 0 });
        using var local = fixture.Client(); Assert.Equal("[]", await local.GetStringAsync("/api/targets"));
        Assert.Single(fixture.Server.ListeningUrls);
        var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        try { await Assert.ThrowsAnyAsync<IOException>(() => fixture.Server.StartAsync(fixture.Options with { Port = ((IPEndPoint)occupied.LocalEndpoint).Port })); }
        finally { occupied.Stop(); }
        Assert.Null(fixture.Server.Url); Assert.Empty(fixture.Server.ListeningUrls);
        await fixture.StartLan();
        using var restarted = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.GetAsync("/api/targets")).StatusCode);
        restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        Assert.Equal("[]", await restarted.GetStringAsync("/api/targets"));
    }

    [Fact]
    public async Task RestartPreservesApiVerifierButDropsBrowserSessions()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var browser = fixture.Client(); await fixture.Login(browser);
        // Changing only the port forces a real stop/rebind and recreates process-local sessions.
        var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port; occupied.Stop();
        await fixture.Server.StartAsync(fixture.Options with { Port = port });
        // Reuse the cookie container across the changed port, just as browsers do.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(fixture.Server.Url + "/api/targets")).StatusCode);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Key);
        Assert.Equal("[]", await browser.GetStringAsync(fixture.Server.Url + "/api/targets"));
    }

    [Fact]
    public async Task DisappearingSelectedAddressStopsListenersWithoutSwitchingInterfaces()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var client = fixture.Client();
        // Simulate the event observing a selected address that is no longer assigned.
        typeof(LocalWebServer).GetField("_watchedAddress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Server, IPAddress.Parse("192.0.2.231"));
        await (Task)typeof(LocalWebServer).GetMethod("CheckNetworkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Server, null)!;
        Assert.Null(fixture.Server.Url);
        Assert.Empty(fixture.Server.ListeningUrls);
        Assert.Contains("unavailable", fixture.Server.LastError);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/targets"));
        Assert.Empty(fixture.Engine.Sessions);
    }

    [Fact]
    public async Task BrowserLoginBruteForceIsBoundedAndRecoversAfterWindow()
    {
        await using var fixture = new Fixture(); await fixture.StartLan();
        using var client = fixture.Client(); var form = await fixture.LoginForm(client);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/login", Form(("csrf", form.Token), ("key", "invalid")))).StatusCode);
        using var limited = await client.PostAsync("/login", Form(("csrf", form.Token), ("key", fixture.Key)));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("300", limited.Headers.RetryAfter!.ToString());
        fixture.Clock.Advance(TimeSpan.FromMinutes(5)); await fixture.Login(client);
        Assert.Equal("[]", await client.GetStringAsync("/api/targets"));
    }

    [Fact]
    public async Task AuthenticationCookiesUseSecureWhenHttpsIsPresent()
    {
        var authentication = new WebAuthentication(WebAccessKey.CreateVerifier(WebAccessKey.Generate()), TimeProvider.System);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "https"; context.Request.Method = "GET"; context.Request.Path = "/login";
        context.Response.Body = new MemoryStream();
        await authentication.HandleAsync(context, _ => throw new InvalidOperationException("Login must be handled by authentication."));
        Assert.Contains("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs)
        => new(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
    private static string Csrf(string html) => Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"").Groups[1].Value;
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "test-data", "web-lan-" + Guid.NewGuid());
        public TraceEngine Engine { get; }
        public LocalWebServer Server { get; }
        public ManualClock Clock { get; } = new();
        public string Key { get; } = WebAccessKey.Generate();
        public WebServerOptions Options { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"));
            Server = new LocalWebServer(Engine, Clock);
            Options = new WebServerOptions { Port = 0, LanEnabled = true, HttpAcknowledged = true,
                LanAddress = WebServerNetwork.GetLanAddresses().FirstOrDefault()?.Address,
                AccessKeyVerifier = WebAccessKey.CreateVerifier(Key) };
        }
        public Task StartLan()
        {
            Assert.True(Options.LanAddress is not null, "Real LAN integration tests require an active private interface.");
            return Server.StartAsync(Options);
        }
        public HttpClient Client(string? url = null) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri(url ?? Server.Url!), Timeout = TimeSpan.FromSeconds(5) };
        public async Task<(string Token, string Cookie, string Html)> LoginForm(HttpClient client)
        {
            using var page = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            // Browser form POSTs on HTTP need this policy to retain their same-origin Origin.
            Assert.Equal("same-origin", page.Headers.GetValues("Referrer-Policy").Single());
            var html = await page.Content.ReadAsStringAsync();
            return (Csrf(html), page.Headers.GetValues("Set-Cookie").Single(), html);
        }
        public async Task Login(HttpClient client)
        {
            var form = await LoginForm(client);
            using var response = await client.PostAsync("/login", Form(("csrf", form.Token), ("key", Key)));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync(); await Engine.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }
}
