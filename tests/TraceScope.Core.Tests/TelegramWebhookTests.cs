using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class TelegramWebhookTests
{
    // Synthetic credentials only. These never contact Telegram.
    private const string Token = "123456789:synthetic-token_do-not-log";
    private const string Replacement = "987654321:replacement-token_do-not-log";
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456789/synthetic-discord";
    private const string Chat = "-1001234567890";
    private static MonitoringEvent Event(string kind = "Alert", string? message = null, string? rule = "latency-rule") =>
        new(Guid.NewGuid(), Guid.Parse("b9b158a9-9459-4f58-b42c-04a3f4e48ec6"),
            DateTimeOffset.Parse("2026-10-14T12:34:56+00:00"), kind,
            message ?? "Average latency 245 ms exceeds 200 ms", rule);
    private static AlertActionConfiguration Config(int timeout = 8) => new()
    {
        ActionTimeoutSeconds = timeout, MinimumActionSpacingSeconds = 0,
        Webhook = new() { Enabled = true, Format = WebhookFormat.Telegram, TelegramChatId = Chat, TelegramBotToken = Token }
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return await respond(request, ct);
        }
    }
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"ok\":true,\"result\":{\"message_id\":1}}") =>
        new(status) { Content = new StringContent(body) };
    private static async Task<AlertActionResult> Deliver(Handler handler, MonitoringEvent? e = null,
        AlertActionConfiguration? config = null, IWebhookSecretStore? secrets = null, WebhookEventContext? context = null)
    {
        await using var dispatcher = new AlertActionDispatcher(config ?? Config(), httpHandler: handler,
            secretStore: secrets, webhookContext: _ => context);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(e ?? Event()));
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
    private sealed class Secrets : IWebhookSecretStore
    {
        public readonly Dictionary<string, string> Values = [];
        public bool FailWrites;
        public string Store(string value, string? existingId = null)
        {
            if (FailWrites) throw new IOException("Sensitive failure " + value);
            var id = existingId ?? Guid.NewGuid().ToString("N");
            Values[id] = value;
            return id;
        }
        public string Retrieve(string id) => Values.TryGetValue(id, out var value) ? value : throw new IOException("Sensitive failure " + Token);
        public void Delete(string id) => Values.Remove(id);
    }
    private sealed class TemporaryWorkspace : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pngr-telegram-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "workspace.json");
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    private sealed class HttpEvents : EventListener
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Net.Http") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.Payload is { } values)
                Messages?.Enqueue(e.EventName + ": " + string.Join(" ", values.Select(value => value?.ToString())));
        }
    }

    [Fact]
    public async Task HttpClientDiagnosticsNeverObserveCredentialBearingUrl()
    {
        using var events = new HttpEvents();
        var handler = new Handler((request, _) =>
        {
            Assert.Contains(Token, request.RequestUri!.AbsoluteUri);
            Assert.DoesNotContain(Token, request.ToString());
            return Task.FromResult(Response());
        });
        Assert.True((await Deliver(handler)).Success);
        Assert.Contains(events.Messages, message => message.Contains("RequestStart"));
        Assert.DoesNotContain(events.Messages, message => message.Contains(Token));
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("4294967296")]
    [InlineData("-123456789")]
    [InlineData("-1001234567890")]
    [InlineData("@network_alerts")]
    [InlineData("@news")]
    [InlineData("@abc")]
    public void TelegramAcceptsDestinationWithoutChangingItsValue(string chat)
    {
        var config = Config() with { Webhook = Config().Webhook with { TelegramChatId = chat } };
        config.Validate();
        var restored = JsonSerializer.Deserialize<AlertActionConfiguration>(JsonSerializer.Serialize(config))!;
        Assert.Equal(WebhookFormat.Telegram, restored.Webhook.Format);
        Assert.Equal(chat, restored.Webhook.TelegramChatId);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a destination")]
    [InlineData("https://evil.example")]
    [InlineData("@bad/name")]
    [InlineData("123.45")]
    public void TelegramRequiresValidChatId(string chat)
    {
        var config = Config() with { Webhook = Config().Webhook with { TelegramChatId = chat } };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("123456:secret/redirect")]
    [InlineData("123456:secret?query")]
    [InlineData("123456:secret#fragment")]
    [InlineData("123456:secret\nheader")]
    public void TelegramRequiresTokenAndRejectsUrlInjectionWithoutRevealingInput(string token)
    {
        var config = Config() with { Webhook = Config().Webhook with { TelegramBotToken = token } };
        var error = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TelegramSavedReferenceSatisfiesRequiredToken()
    {
        var config = Config() with { Webhook = Config().Webhook with
            { TelegramBotToken = "", TelegramBotTokenSecretId = Guid.NewGuid().ToString("N") } };
        config.Validate();
    }

    [Fact]
    public async Task RequestUsesOnlyOfficialHttpsEndpointAndCorrectJson()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.telegram.org/bot" + Token + "/sendMessage", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Response());
        });
        Assert.True((await Deliver(handler)).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(Chat, payload.RootElement.GetProperty("chat_id").GetString());
        Assert.True(payload.RootElement.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("parse_mode", out _));
        Assert.DoesNotContain(Token, handler.Bodies[0]);
    }

    [Theory]
    [InlineData("Alert", "🔴 Pngr — Network Alert")]
    [InlineData("Recovery", "🟢 Pngr — Network Recovered")]
    [InlineData("RouteChange", "🔵 Pngr — Route Changed")]
    public async Task FormatsExistingEventsWithAvailableContext(string kind, string heading)
    {
        var e = Event(kind);
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        Assert.True((await Deliver(handler, e, context: new("example.org", "High latency"))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var text = payload.RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith(heading, text);
        Assert.Contains(e.Message, text);
        Assert.Contains("example.org", text);
        Assert.Contains("High latency", text);
        Assert.Contains(e.SessionId.ToString(), text);
        Assert.Contains("2026", text);
    }

    [Fact]
    public async Task MissingOptionalContextDoesNotInventMeasurements()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        Assert.True((await Deliver(handler, Event("RouteChange", "Route changed", null))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var text = payload.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Route changed", text);
        Assert.DoesNotContain("Target:", text);
        Assert.DoesNotContain("Current:", text);
        Assert.DoesNotContain("Threshold:", text);
        Assert.DoesNotContain("null", text);
    }

    [Fact]
    public async Task PlainTextPreservesUnicodeAndMarkupLiterally()
    {
        const string message = "<b>網路 & café</b> _*[]()~`>#+-=|{}.! 🛰️";
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        Assert.True((await Deliver(handler, Event(message: message), context: new("<target>", "a&b"))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Contains(message, payload.RootElement.GetProperty("text").GetString());
        Assert.Contains("<target>", payload.RootElement.GetProperty("text").GetString());
        Assert.False(payload.RootElement.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task VeryLongUnicodeContentIsBoundedAndDoesNotSplitSurrogatePairs()
    {
        var message = string.Concat(Enumerable.Repeat("🌍網路", 3000));
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        Assert.True((await Deliver(handler, Event(message: message), context: new(new string('t', 5000), new string('r', 5000)))).Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var text = payload.RootElement.GetProperty("text").GetString()!;
        Assert.InRange(text.Length, 1, 4096);
        Assert.DoesNotContain('\ufffd', text);
        Assert.Contains("Network Alert", text);
        for (var i = 0; i < text.Length; i++)
            if (char.IsHighSurrogate(text[i])) Assert.True(++i < text.Length && char.IsLowSurrogate(text[i]));
            else Assert.False(char.IsLowSurrogate(text[i]));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.OK, 400)]
    [InlineData(HttpStatusCode.OK, 401)]
    [InlineData(HttpStatusCode.OK, 403)]
    public async Task PermanentHttpAndApiErrorsDoNotRetryOrExposeResponse(HttpStatusCode status, int code)
    {
        var body = JsonSerializer.Serialize(new { ok = false, error_code = code, description = "Sensitive " + Token });
        var handler = new Handler((_, _) => Task.FromResult(Response(status, body)));
        var result = await Deliver(handler);
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Token, result.Message);
        Assert.DoesNotContain("Sensitive", result.Message);
        Assert.Contains(code == 401 ? "token" : code == 403 ? "permission" : "Chat", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"ok\":\"true\"}")]
    [InlineData("{\"ok\":false}")]
    public async Task SuccessRequiresValidTelegramConfirmation(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(body: body)));
        Assert.False((await Deliver(handler)).Success);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RedirectIsAnImmediateCredentialSafeFailure()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(HttpStatusCode.Redirect, Token);
            response.Headers.Location = new Uri("https://untrusted.example/" + Token);
            return Task.FromResult(response);
        });
        var result = await Deliver(handler);
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Token, result.Message);
        Assert.DoesNotContain("untrusted", result.Message);
    }

    [Fact]
    public async Task TemporaryServerErrorsRetryAtMostThreeTimes()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.InternalServerError)));
        Assert.False((await Deliver(handler)).Success);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct());
    }

    [Fact]
    public async Task ApiLevelTemporaryErrorsRetryAndRecover()
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(++count < 3
            ? Response(body: "{\"ok\":false,\"error_code\":500}") : Response()));
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.OK)]
    public async Task RespectsTelegramRetryAfterOnHttpAndApiRateLimits(HttpStatusCode status)
    {
        var count = 0;
        var handler = new Handler((_, _) => Task.FromResult(++count == 1
            ? Response(status, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":1}}") : Response()));
        var timer = Stopwatch.StartNew();
        Assert.True((await Deliver(handler)).Success);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task LongRateLimitWaitIsBoundedByActionTimeout()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.TooManyRequests,
            "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":3600}}")));
        var timer = Stopwatch.StartNew();
        var result = await Deliver(handler, config: Config(timeout: 1));
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkFailureRetriesBoundedlyAndRedactsExceptionUrl()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("DNS failed for https://api.telegram.org/bot" + Token + "/sendMessage"));
        var result = await Deliver(handler);
        Assert.False(result.Success);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.DoesNotContain(Token, result.Message);
        Assert.DoesNotContain("api.telegram.org", result.Message);
    }

    [Fact]
    public async Task TimeoutCancelsTransportWithoutRevealingCredentials()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response(); });
        var result = await Deliver(handler, config: Config(timeout: 1));
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Token, result.Message);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecureTokenReferenceIsResolvedOnlyForDelivery()
    {
        var secrets = new Secrets();
        var id = secrets.Store(Token);
        var config = Config() with { Webhook = Config().Webhook with { TelegramBotToken = "", TelegramBotTokenSecretId = id } };
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://api.telegram.org/bot" + Token + "/sendMessage", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response());
        });
        Assert.True((await Deliver(handler, config: config, secrets: secrets)).Success);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(config));
    }

    [Fact]
    public async Task MissingSecureTokenDoesNotExposeStoreExceptionOrSendHttp()
    {
        var config = Config() with { Webhook = Config().Webhook with
            { TelegramBotToken = "", TelegramBotTokenSecretId = Guid.NewGuid().ToString("N") } };
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        var result = await Deliver(handler, config: config, secrets: new Secrets());
        Assert.False(result.Success);
        Assert.Empty(handler.Bodies);
        Assert.DoesNotContain(Token, result.Message);
    }

    [Fact]
    public async Task SettingsSaveAndLoadKeepOnlyTokenReferenceAndPreserveOtherFormats()
    {
        using var temporary = new TemporaryWorkspace();
        var secrets = new Secrets();
        var store = new WorkspaceStore(temporary.Path, secrets);
        var config = new WorkspaceConfiguration { Actions = Config() with { Webhook = Config().Webhook with
            { Url = DiscordUrl, BearerTokenEnvironmentVariable = "PNGR_EXISTING_BEARER" } } };
        var saved = await store.SaveAsync(config);
        Assert.Empty(saved.Actions.Webhook.TelegramBotToken);
        Assert.Equal(Token, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
        Assert.Equal(DiscordUrl, secrets.Retrieve(saved.Actions.Webhook.UrlSecretId));
        Assert.Equal(Chat, saved.Actions.Webhook.TelegramChatId);
        Assert.Equal("PNGR_EXISTING_BEARER", saved.Actions.Webhook.BearerTokenEnvironmentVariable);
        var json = await File.ReadAllTextAsync(temporary.Path);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(DiscordUrl, json);
        Assert.Equal(saved.Actions.Webhook, (await store.LoadAsync())!.Actions.Webhook);
        foreach (var format in new[] { WebhookFormat.Discord, WebhookFormat.GenericJson, WebhookFormat.Telegram })
        {
            saved = await store.SaveAsync(saved with { Actions = saved.Actions with { Webhook = saved.Actions.Webhook with { Format = format } } });
            Assert.Equal(Token, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
            Assert.Equal(DiscordUrl, secrets.Retrieve(saved.Actions.Webhook.UrlSecretId));
            Assert.Equal("PNGR_EXISTING_BEARER", saved.Actions.Webhook.BearerTokenEnvironmentVariable);
        }
    }

    [Fact]
    public async Task PairedDestinationPersistsAndDisconnectKeepsTokenAndOtherFormats()
    {
        using var temporary = new TemporaryWorkspace();
        var secrets = new Secrets();
        var store = new WorkspaceStore(temporary.Path, secrets);
        var saved = await store.SaveAsync(new() { Actions = Config() with { Webhook = Config().Webhook with
            { TelegramBotUsername = "PngrTestBot", TelegramChatId = "1234567890123", Url = DiscordUrl } } });
        var restored = (await store.LoadAsync())!;
        Assert.Equal("PngrTestBot", restored.Actions.Webhook.TelegramBotUsername);
        Assert.Equal("1234567890123", restored.Actions.Webhook.TelegramChatId);
        var tokenId = saved.Actions.Webhook.TelegramBotTokenSecretId;
        var disconnected = await store.SaveAsync(restored with { Actions = restored.Actions with { Webhook = restored.Actions.Webhook with
            { Enabled = false, TelegramChatId = "", TelegramBotUsername = "" } } });
        Assert.False(disconnected.Actions.Webhook.Enabled);
        Assert.Empty(disconnected.Actions.Webhook.TelegramChatId);
        Assert.Equal(tokenId, disconnected.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(Token, secrets.Retrieve(tokenId));
        Assert.Equal(saved.Actions.Webhook.UrlSecretId, disconnected.Actions.Webhook.UrlSecretId);
        Assert.Equal(DiscordUrl, secrets.Retrieve(saved.Actions.Webhook.UrlSecretId));
        Assert.DoesNotContain(Token, await File.ReadAllTextAsync(temporary.Path));
    }

    [Fact]
    public async Task ReplacingTokenPreservesOldCredentialAndUrl()
    {
        using var temporary = new TemporaryWorkspace();
        var secrets = new Secrets();
        var store = new WorkspaceStore(temporary.Path, secrets);
        var initial = await store.SaveAsync(new() { Actions = Config() with { Webhook = Config().Webhook with { Url = DiscordUrl } } });
        var oldId = initial.Actions.Webhook.TelegramBotTokenSecretId;
        var saved = await store.SaveAsync(initial with { Actions = initial.Actions with
            { Webhook = initial.Actions.Webhook with { TelegramBotToken = Replacement } } });
        Assert.NotEqual(oldId, saved.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(Token, secrets.Retrieve(oldId));
        Assert.Equal(Replacement, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
        Assert.Equal(initial.Actions.Webhook.UrlSecretId, saved.Actions.Webhook.UrlSecretId);
        Assert.DoesNotContain(Replacement, await File.ReadAllTextAsync(temporary.Path));
    }

    [Fact]
    public async Task SecureStorageFailurePreservesSavedSettingsAndRedactsToken()
    {
        using var temporary = new TemporaryWorkspace();
        var secrets = new Secrets();
        var store = new WorkspaceStore(temporary.Path, secrets);
        var initial = await store.SaveAsync(new() { Actions = Config() });
        var previous = await File.ReadAllTextAsync(temporary.Path);
        secrets.FailWrites = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(initial with { Actions = initial.Actions with
            { Webhook = initial.Actions.Webhook with { TelegramBotToken = Replacement } } }));
        Assert.DoesNotContain(Replacement, error.ToString());
        Assert.Equal(previous, await File.ReadAllTextAsync(temporary.Path));
        Assert.Equal(Token, secrets.Retrieve(initial.Actions.Webhook.TelegramBotTokenSecretId));
    }

    [Fact]
    public async Task WorkspaceExportOmitsRawAndReferencedTelegramCredentials()
    {
        using var stream = new MemoryStream();
        var config = new WorkspaceConfiguration { Actions = Config() with { Webhook = Config().Webhook with
            { TelegramBotTokenSecretId = Guid.NewGuid().ToString("N") } } };
        await WorkspaceStore.ExportAsync(stream, config);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(config.Actions.Webhook.TelegramBotTokenSecretId, json);
        stream.Position = 0;
        var imported = await WorkspaceStore.ImportAsync(stream);
        Assert.False(imported.Actions.Webhook.Enabled);
        Assert.Empty(imported.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(Chat, imported.Actions.Webhook.TelegramChatId);
    }

    [Fact]
    public async Task TestUsesUnsavedValuesWithoutReplacingStoredCredential()
    {
        var secrets = new Secrets();
        var id = secrets.Store(Token);
        var saved = Config() with { Webhook = Config().Webhook with { TelegramBotToken = "", TelegramBotTokenSecretId = id } };
        var staged = saved.Webhook with { TelegramBotToken = Replacement, TelegramChatId = "@new_channel" };
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://api.telegram.org/bot" + Replacement + "/sendMessage", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response());
        });
        await using var dispatcher = new AlertActionDispatcher(saved, httpHandler: handler, secretStore: secrets);
        Assert.True((await dispatcher.SendTestWebhookAsync(staged)).Success);
        Assert.Equal(Token, secrets.Retrieve(id));
        Assert.Single(secrets.Values);
        Assert.Equal(Chat, saved.Webhook.TelegramChatId);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("@new_channel", payload.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains("🧪 Pngr — Telegram Test", payload.RootElement.GetProperty("text").GetString());
        Assert.Contains("Your Telegram notification configuration is working.", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DisablingDeliveryStopsRetriesAlreadyInProgress()
    {
        var config = Config();
        AlertActionDispatcher? dispatcher = null;
        var handler = new Handler((_, _) =>
        {
            dispatcher!.Configure(config with { Webhook = config.Webhook with { Enabled = false } });
            return Task.FromResult(Response(HttpStatusCode.InternalServerError));
        });
        await using var owned = dispatcher = new AlertActionDispatcher(config, httpHandler: handler);
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => done.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event()));
        Assert.False((await done.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RecoveryFilteringRemainsSharedWithOtherActions()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response()));
        await using var dispatcher = new AlertActionDispatcher(Config() with { IncludeRecovery = false }, httpHandler: handler);
        Assert.False(dispatcher.TryEnqueue(Event("Recovery")));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task RateLimitCooldownAlsoProtectsFollowingQueuedEvent()
    {
        var count = 0;
        var timer = Stopwatch.StartNew();
        TimeSpan secondRequest = default;
        var handler = new Handler((_, _) =>
        {
            if (++count == 1) return Task.FromResult(Response(HttpStatusCode.TooManyRequests,
                "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":2}}"));
            secondRequest = timer.Elapsed;
            return Task.FromResult(Response());
        });
        await using var dispatcher = new AlertActionDispatcher(Config(timeout: 1), httpHandler: handler);
        var first = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        dispatcher.ActionCompleted += result => { if (Interlocked.Increment(ref completed) == 1) first.TrySetResult(result); else second.TrySetResult(result); };
        Assert.True(dispatcher.TryEnqueue(Event()));
        Assert.False((await first.Task.WaitAsync(TimeSpan.FromSeconds(4))).Success);
        // A longer deadline lets the next action observe the remaining server-directed wait.
        dispatcher.Configure(Config(timeout: 5));
        Assert.True(dispatcher.TryEnqueue(Event("RouteChange")));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.True(secondRequest >= TimeSpan.FromMilliseconds(1800));
    }

    [Fact]
    public async Task WorkspaceWriteFailureRollsBackOnlyNewCredential()
    {
        using var temporary = new TemporaryWorkspace();
        var secrets = new Secrets();
        var store = new WorkspaceStore(temporary.Path, secrets);
        var initial = await store.SaveAsync(new() { Actions = Config() });
        var json = await File.ReadAllTextAsync(temporary.Path);
        var savedId = initial.Actions.Webhook.TelegramBotTokenSecretId;
        Directory.CreateDirectory(temporary.Path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(initial with { Actions = initial.Actions with
            { Webhook = initial.Actions.Webhook with { TelegramBotToken = Replacement } } }));
        Assert.Single(secrets.Values);
        Assert.Equal(Token, secrets.Retrieve(savedId));
        Assert.Equal(json, await File.ReadAllTextAsync(temporary.Path));
    }

    [Fact]
    public async Task RepeatedTestsRespectPreviousRateLimitWithoutSaving()
    {
        var attempts = 0;
        var handler = new Handler((_, _) => Task.FromResult(++attempts == 1
            ? Response(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":3}}")
            : Response()));
        await using var dispatcher = new AlertActionDispatcher(Config(), httpHandler: handler);
        var timer = Stopwatch.StartNew();
        Assert.False((await dispatcher.SendTestWebhookAsync(Config().Webhook, 1)).Success);
        Assert.True((await dispatcher.SendTestWebhookAsync(Config().Webhook, 5)).Success);
        Assert.Equal(2, attempts);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(2850));
    }

    [Fact]
    public async Task TestDoesNotReportSuccessForTelegramApiFailure()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(body: "{\"ok\":false,\"error_code\":401}")));
        await using var dispatcher = new AlertActionDispatcher(Config(), httpHandler: handler);
        var result = await dispatcher.SendTestWebhookAsync(Config().Webhook);
        Assert.False(result.Success);
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Token, result.Message);
    }
}
