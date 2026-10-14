using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class DiscordWebhookTests
{
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456789012345678/test-token_do-not-log";
    private static MonitoringEvent Event(string kind = "Alert", string? rule = "rule-a", string? message = null) =>
        new(Guid.NewGuid(), Guid.Parse("b9b158a9-9459-4f58-b42c-04a3f4e48ec6"),
            DateTimeOffset.Parse("2026-10-14T12:34:56+00:00"), kind, message ?? "example.org: Latency alert — measured latency is high", rule);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        public readonly List<HttpMethod> Methods = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Methods.Add(request.Method);
            return await respond(request, cancellationToken);
        }
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null) =>
        new(status) { Content = new StringContent(body ?? "") };
    private static AlertActionConfiguration Configuration(WebhookFormat format = WebhookFormat.Discord, int timeout = 5) =>
        new() { ActionTimeoutSeconds = timeout, MinimumActionSpacingSeconds = 0,
            Webhook = new() { Enabled = true, Format = format, Url = DiscordUrl } };
    private static async Task<AlertActionResult> Deliver(Handler handler, MonitoringEvent? monitoringEvent = null, AlertActionConfiguration? config = null)
    {
        await using var dispatcher = new AlertActionDispatcher(config ?? Configuration(), httpHandler: handler);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(monitoringEvent ?? Event()));
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void LegacyWebhookConfigurationDefaultsToGenericJson()
    {
        var legacy = JsonSerializer.Deserialize<WebhookAlertConfiguration>("{\"Enabled\":true,\"Url\":\"https://example.org/hook\"}")!;
        Assert.Equal(WebhookFormat.GenericJson, legacy.Format);
        new AlertActionConfiguration { Webhook = legacy }.Validate();
    }

    [Fact]
    public async Task GenericJsonPreservesEntireEventPayloadAndDoesNotRetry()
    {
        var e = Event() with { AcknowledgedAt = DateTimeOffset.Parse("2026-10-14T13:00:00Z"), AcknowledgmentNote = "reviewed" };
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
        var result = await Deliver(handler, e, Configuration(WebhookFormat.GenericJson));
        Assert.False(result.Success);
        Assert.Equal(JsonSerializer.Serialize(e), Assert.Single(handler.Bodies));
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Methods));
    }

    [Theory]
    [InlineData("Alert", 0xed4245)]
    [InlineData("Recovery", 0x57f287)]
    [InlineData("RouteChange", 0x3498db)]
    public async Task DiscordFormatsEachEventWithEmbedColorAndAvailableInformation(string kind, int color)
    {
        var e = Event(kind);
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)));
        Assert.True((await Deliver(handler, e)).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var embed = Assert.Single(payload.RootElement.GetProperty("embeds").EnumerateArray());
        Assert.Equal($"Pngr — {kind}", embed.GetProperty("title").GetString());
        Assert.Equal(color, embed.GetProperty("color").GetInt32());
        Assert.Equal(e.Message, embed.GetProperty("description").GetString());
        Assert.Equal(e.Timestamp, DateTimeOffset.Parse(embed.GetProperty("timestamp").GetString()!));
        Assert.Contains(embed.GetProperty("fields").EnumerateArray(), field => field.GetProperty("value").GetString() == e.AlertRuleId);
        Assert.Empty(payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task DiscordSuppressesMentionsAndTruncatesDescriptionWithinEmbedLimit()
    {
        var message = "@everyone @here <@&123456789> " + new string('x', 5000);
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)));
        Assert.True((await Deliver(handler, Event(message: message))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Empty(payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var description = payload.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!;
        Assert.InRange(description.Length, 1, 4096);
    }

    [Fact]
    public async Task DiscordOmitsAbsentRuleAndDoesNotInventTargetInformation()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)));
        Assert.True((await Deliver(handler, Event("RouteChange", rule: null, message: "Route changed"))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var fields = payload.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray().ToArray();
        Assert.DoesNotContain(fields, field => field.GetProperty("name").GetString() is "Alert rule" or "Target");
        Assert.DoesNotContain(fields, field => field.GetProperty("value").ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/")]
    [InlineData("https://discord.com/api/webhooks/not-an-id/token")]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/token")]
    [InlineData("https://discord.com.evil.example/api/webhooks/123456789012345678/token")]
    [InlineData("https://user:pass@discord.com/api/webhooks/123456789012345678/token")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/token/extra")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/token#fragment")]
    [InlineData("https://example.org/api/webhooks/123456789012345678/token")]
    public void DiscordRejectsInvalidUrlsWithoutExposingCredentials(string url)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            (Configuration() with { Webhook = new() { Enabled = true, Format = WebhookFormat.Discord, Url = url } }).Validate());
        Assert.DoesNotContain("user:pass", error.Message);
        Assert.DoesNotContain("/token", error.Message);
    }

    [Fact]
    public void UnknownWebhookFormatIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => (Configuration() with
        { Webhook = new() { Enabled = true, Url = DiscordUrl, Format = (WebhookFormat)99 } }).Validate());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task DiscordDoesNotRetryClientErrorsOrRedirects(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(status, DiscordUrl)));
        var result = await Deliver(handler);
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.Contains(((int)status).ToString(), result.Message);
        Assert.DoesNotContain(DiscordUrl, result.Message);
        Assert.DoesNotContain("test-token", result.Message);
    }

    [Fact]
    public async Task DiscordRetriesServerErrorsAndSucceedsWithinThreeAttempts()
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(Response(++count < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent)));
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct());
    }

    [Fact]
    public async Task DiscordStopsAfterThreeFailedAttempts()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.InternalServerError)));
        var result = await Deliver(handler);
        Assert.False(result.Success);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task DiscordRespectsRateLimitRetryAfterHeader()
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            var response = Response(++count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.NoContent);
            if (count == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        var timer = Stopwatch.StartNew();
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task DiscordRespectsRateLimitBodyWhenHeaderIsMissing()
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(++count == 1
            ? Response(HttpStatusCode.TooManyRequests, "{\"retry_after\":0.15,\"global\":false}") : Response(HttpStatusCode.NoContent)));
        var timer = Stopwatch.StartNew();
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(125));
    }

    [Fact]
    public async Task RateLimitWaitCannotOutlastActionTimeout()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(HttpStatusCode.TooManyRequests, "{\"retry_after\":3600}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        var timer = Stopwatch.StartNew();
        var result = await Deliver(handler, config: Configuration(timeout: 1));
        Assert.False(result.Success);
        Assert.Equal("Action timed out", result.Message);
        Assert.Single(handler.Bodies);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task RequestTimeoutCancelsTransportAndDoesNotExposeUrl()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response(HttpStatusCode.NoContent); });
        var result = await Deliver(handler, config: Configuration(timeout: 1));
        Assert.False(result.Success);
        Assert.Equal("Action timed out", result.Message);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(WebhookFormat.GenericJson)]
    [InlineData(WebhookFormat.Discord)]
    public async Task TransportExceptionsNeverExposeWebhookToken(WebhookFormat format)
    {
        var handler = new Handler((_, _) => throw new IOException($"Cannot access {DiscordUrl}"));
        var result = await Deliver(handler, config: Configuration(format));
        Assert.False(result.Success);
        Assert.DoesNotContain(DiscordUrl, result.Message);
        Assert.DoesNotContain("test-token", result.Message);
    }
    private sealed class SecretStore : IWebhookSecretStore
    {
        public readonly Dictionary<string, string> Values = [];
        public bool FailWrites;
        public bool KeychainFailure;
        public string Store(string url, string? existingId = null)
        { if (KeychainFailure) throw new WebhookCredentialException(WebhookCredentialOperation.Create, -67030); if (FailWrites) throw new IOException($"Unable to store {url}"); var id = existingId ?? Guid.NewGuid().ToString("N"); Values[id] = url; return id; }
        public string Retrieve(string id) => Values.TryGetValue(id, out var url) ? url : throw new IOException($"Missing secret {DiscordUrl}");
        public void Delete(string id) => Values.Remove(id);
    }

    [Fact]
    public async Task DispatcherResolvesSecureCredentialReferenceWithoutSerializingUrl()
    {
        var secrets = new SecretStore();
        var id = secrets.Store(DiscordUrl);
        var config = Configuration() with { Webhook = new() { Enabled = true, Format = WebhookFormat.Discord, UrlSecretId = id } };
        var serialized = JsonSerializer.Serialize(config);
        Assert.Contains(id, serialized);
        Assert.DoesNotContain(DiscordUrl, serialized);
        var handler = new Handler((request, _) =>
        { Assert.Equal(DiscordUrl + "?wait=true", request.RequestUri!.AbsoluteUri); return Task.FromResult(Response(HttpStatusCode.NoContent)); });
        await using var dispatcher = new AlertActionDispatcher(config, httpHandler: handler, secretStore: secrets);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        Assert.True((await done.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public async Task MissingSecureCredentialFailsWithoutExposingStoreError()
    {
        var config = Configuration() with { Webhook = new() { Enabled = true, Format = WebhookFormat.Discord, UrlSecretId = Guid.NewGuid().ToString("N") } };
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)));
        await using var dispatcher = new AlertActionDispatcher(config, httpHandler: handler, secretStore: new SecretStore());
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.DoesNotContain("test-token", result.Message);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task DiscordIncludesResolvedTargetAndRuleFromExistingContext()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.NoContent)));
        await using var dispatcher = new AlertActionDispatcher(Configuration(), httpHandler: handler,
            webhookContext: _ => new WebhookEventContext("example.org", "High latency"));
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        Assert.True((await done.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var fields = payload.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray().ToArray();
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Target" && field.GetProperty("value").GetString() == "example.org");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Alert rule" && field.GetProperty("value").GetString() == "High latency");
    }

    [Fact]
    public async Task GenericJsonKeepsBearerEnvironmentVariableSupport()
    {
        var variable = "PNGR_WEBHOOK_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "private-bearer");
        try
        {
            var handler = new Handler((request, _) =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("private-bearer", request.Headers.Authorization?.Parameter);
                return Task.FromResult(Response(HttpStatusCode.NoContent));
            });
            var config = Configuration(WebhookFormat.GenericJson) with { Webhook = new()
            { Enabled = true, Url = "http://127.0.0.1/hook", BearerTokenEnvironmentVariable = variable } };
            Assert.True((await Deliver(handler, config: config)).Success);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task DiscordRespectsRetryAfterHttpDate()
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            var response = Response(++count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.NoContent);
            if (count == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(2));
            return Task.FromResult(response);
        });
        var timer = Stopwatch.StartNew();
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task WorkspaceSaveStoresUrlSecurelyAndReturnsOnlyCredentialReference()
    {
        var path = Path.Combine(Path.GetTempPath(), "pngr-webhook-workspace-" + Guid.NewGuid().ToString("N"), "workspace.json");
        try
        {
            var secrets = new SecretStore();
            var store = new WorkspaceStore(path, secrets);
            var secured = await store.SaveAsync(new WorkspaceConfiguration { Actions = Configuration() });
            Assert.Empty(secured.Actions.Webhook.Url);
            Assert.True(Guid.TryParseExact(secured.Actions.Webhook.UrlSecretId, "N", out _));
            Assert.Equal(DiscordUrl, secrets.Retrieve(secured.Actions.Webhook.UrlSecretId));
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("test-token", json);
            Assert.Contains(secured.Actions.Webhook.UrlSecretId, json);
            var restored = await store.LoadAsync();
            Assert.Equal(secured.Actions.Webhook, restored!.Actions.Webhook);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task WorkspaceLoadMigratesLegacyGenericUrlWithoutChangingFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), "pngr-webhook-legacy-" + Guid.NewGuid().ToString("N"), "workspace.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var legacy = new WorkspaceConfiguration { Actions = Configuration(WebhookFormat.GenericJson) };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, WorkspaceStore.JsonOptions));
            var secrets = new SecretStore();
            var restored = await new WorkspaceStore(path, secrets).LoadAsync();
            Assert.Equal(WebhookFormat.GenericJson, restored!.Actions.Webhook.Format);
            Assert.Empty(restored.Actions.Webhook.Url);
            Assert.Equal(DiscordUrl, secrets.Retrieve(restored.Actions.Webhook.UrlSecretId));
            Assert.DoesNotContain("test-token", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceExportsExcludeWebhookCredentialsAndDisableDelivery(bool secured)
    {
        var webhook = secured ? new WebhookAlertConfiguration { Enabled = true, Format = WebhookFormat.Discord, UrlSecretId = Guid.NewGuid().ToString("N") }
            : Configuration().Webhook;
        var workspace = new WorkspaceConfiguration { Actions = Configuration() with { Webhook = webhook } };
        using var stream = new MemoryStream();
        await WorkspaceStore.ExportAsync(stream, workspace);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("test-token", json);
        if (secured) Assert.DoesNotContain(webhook.UrlSecretId, json);
        stream.Position = 0;
        var exported = await WorkspaceStore.ImportAsync(stream);
        Assert.False(exported.Actions.Webhook.Enabled);
        Assert.Empty(exported.Actions.Webhook.Url);
        Assert.Empty(exported.Actions.Webhook.UrlSecretId);
        Assert.True(workspace.Actions.Webhook.Enabled);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"retry_after\":\"later\"}")]
    [InlineData("{\"retry_after\":null}")]
    [InlineData("{\"retry_after\":-1}")]
    public async Task MalformedRateLimitResponseFallsBackToBoundedRetry(string body)
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(++count == 1
            ? Response(HttpStatusCode.TooManyRequests, body) : Response(HttpStatusCode.NoContent)));
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public void WebhookRecordDiagnosticStringRedactsLegacyUrlAndSecretReference()
    {
        var webhook = Configuration().Webhook with { UrlSecretId = Guid.NewGuid().ToString("N") };
        Assert.DoesNotContain("test-token", webhook.ToString());
        Assert.DoesNotContain(webhook.UrlSecretId, webhook.ToString());
        Assert.DoesNotContain("test-token", (Configuration() with { Webhook = webhook }).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLegacyCredentialMigrationDisablesWebhookWithoutOverwritingWorkspace(bool keychainFailure)
    {
        var path = Path.Combine(Path.GetTempPath(), "pngr-webhook-migration-failure-" + Guid.NewGuid().ToString("N"), "workspace.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new WorkspaceConfiguration { Actions = Configuration() }, WorkspaceStore.JsonOptions);
            await File.WriteAllTextAsync(path, json);
            var store = new WorkspaceStore(path, new SecretStore { FailWrites = true, KeychainFailure = keychainFailure });
            var restored = await store.LoadAsync();
            Assert.False(restored!.Actions.Webhook.Enabled);
            Assert.NotNull(store.CredentialWarning);
            if (keychainFailure) Assert.Contains("OSStatus -67030", store.CredentialWarning);
            Assert.DoesNotContain("test-token", store.CredentialWarning);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCredentialSaveLeavesExistingWorkspaceIntactAndRedactsException(bool keychainFailure)
    {
        var path = Path.Combine(Path.GetTempPath(), "pngr-webhook-save-failure-" + Guid.NewGuid().ToString("N"), "workspace.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new WorkspaceConfiguration(), WorkspaceStore.JsonOptions);
            await File.WriteAllTextAsync(path, json);
            var store = new WorkspaceStore(path, new SecretStore { FailWrites = true, KeychainFailure = keychainFailure });
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.SaveAsync(new WorkspaceConfiguration { Actions = Configuration() }));
            if (keychainFailure) Assert.Equal(-67030, Assert.IsType<WebhookCredentialException>(error).OSStatus);
            Assert.DoesNotContain("test-token", error.Message);
            Assert.Null(error.InnerException);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    public async Task ProductionHttpHandlerDoesNotFollowGenericWebhookRedirects(int status)
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var config = Configuration(WebhookFormat.GenericJson) with { Webhook = new()
        { Enabled = true, Url = $"http://127.0.0.1:{port}/original" } };
        await using var dispatcher = new AlertActionDispatcher(config);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        var first = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        first.Response.StatusCode = status;
        first.Response.RedirectLocation = $"http://127.0.0.1:{port}/redirected";
        first.Response.Close();
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Contains(status.ToString(), result.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => listener.GetContextAsync().WaitAsync(TimeSpan.FromMilliseconds(150)));
        listener.Stop();
    }

    [Fact]
    public async Task DiscordRetainsRateLimitCooldownForNextEventAfterActionTimeout()
    {
        var count = 0;
        var timer = Stopwatch.StartNew();
        var requestTimes = new List<TimeSpan>();
        var handler = new Handler((_, _) =>
        {
            requestTimes.Add(timer.Elapsed);
            return Task.FromResult(++count == 1 ? Response(HttpStatusCode.TooManyRequests, "{\"retry_after\":3}") : Response(HttpStatusCode.NoContent));
        });
        await using var dispatcher = new AlertActionDispatcher(Configuration(timeout: 2), httpHandler: handler);
        var first = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => { if (!first.TrySetResult(result)) second.TrySetResult(result); };
        Assert.True(dispatcher.TryEnqueue(Event()));
        Assert.Equal("Action timed out", (await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.True(dispatcher.TryEnqueue(Event("Recovery")));
        Assert.True((await second.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(requestTimes[1] - requestTimes[0] >= TimeSpan.FromMilliseconds(2850));
    }

    [Fact]
    public async Task DisablingWebhookDuringRateLimitWaitStopsRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) =>
        {
            started.TrySetResult();
            return Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{\"retry_after\":0.2}"));
        });
        await using var dispatcher = new AlertActionDispatcher(Configuration(), httpHandler: handler);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.Configure(new());
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.False(dispatcher.TryEnqueue(Event()));
    }

}
