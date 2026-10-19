using System.Net;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class TelegramPairingTests
{
    // Synthetic credentials and HTTP handlers only; no network or credential store.
    private const string Token = "123456789:synthetic-pairing-token_do-not-log";
    private const string Username = "PngrPairingBot";
    private static WebhookAlertConfiguration Config() => new()
    {
        Enabled = true, Format = WebhookFormat.Telegram, TelegramBotToken = Token
    };
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<string> Methods = [];
        public readonly List<string> Queries = [];
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.RequestUri!.AbsolutePath.Split('/').Last());
            Queries.Add(request.RequestUri.Query);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("api.telegram.org", request.RequestUri.Host);
            Assert.DoesNotContain(Token, request.ToString());
            return await respond(request, ct);
        }
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };
    private static HttpResponseMessage Setup(string method) => method switch
    {
        "getMe" => Reply("{\"ok\":true,\"result\":{\"id\":123456789,\"is_bot\":true,\"username\":\"" + Username + "\"}}"),
        "getWebhookInfo" => Reply("{\"ok\":true,\"result\":{\"url\":\"\"}}"),
        "sendMessage" => Reply("{\"ok\":true,\"result\":{\"message_id\":8}}"),
        _ => throw new InvalidOperationException("Unexpected mock method")
    };
    private static string Method(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.Split('/').Last();
    private static string Code(TelegramPairingPrompt prompt) => Uri.UnescapeDataString(new Uri(prompt.DeepLink).Query.Split("start=")[1]);
    private static object Update(string code, Clock clock, long chatId = 4294967296, string type = "private", long id = 7) =>
        new { update_id = id, message = new { message_id = 99, date = clock.Now.ToUnixTimeSeconds(),
            from = new { id = 333 }, chat = new { id = chatId, type }, text = "/start " + code } };
    private static HttpResponseMessage Updates(params object[] updates) => Reply(JsonSerializer.Serialize(new { ok = true, result = updates }));

    [Fact]
    public async Task PrivatePairingUsesActualChatIdAndConfirmsBeforeReturning()
    {
        var clock = new Clock();
        string code = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update(code, clock)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var result = await client.PairAsync(Config(), prompt =>
        {
            Assert.Equal(Username, prompt.BotUsername);
            Assert.Equal("t.me", new Uri(prompt.DeepLink).Host);
            Assert.DoesNotContain(Token, prompt.DeepLink);
            code = Code(prompt);
            Assert.InRange(code.Length, 22, 64);
            Assert.Matches("^[A-Za-z0-9_-]+$", code);
            Assert.InRange(prompt.ExpiresAt - clock.Now, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
            Assert.DoesNotContain(code, prompt.ToString());
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("4294967296", result.ChatId);
        Assert.Equal(Username, result.BotUsername);
        Assert.Equal(new[] { "getMe", "getWebhookInfo", "getUpdates", "sendMessage" }, handler.Methods);
        using var body = JsonDocument.Parse(handler.Bodies.Last());
        Assert.Equal("4294967296", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains("Pngr Connected", body.RootElement.GetProperty("text").GetString());
        Assert.Contains("Apply or OK", body.RootElement.GetProperty("text").GetString());
        Assert.All(handler.Queries, query => { Assert.DoesNotContain("offset", query); Assert.DoesNotContain("allowed_updates", query); });
        Assert.DoesNotContain(handler.Methods, method => method is "deleteWebhook" or "setWebhook");
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error_code\":401,\"description\":\"revoked\"}", 401)]
    [InlineData("{\"ok\":false,\"error_code\":404}", 404)]
    [InlineData("not-json", 200)]
    [InlineData("{\"ok\":true,\"result\":{\"is_bot\":true,\"id\":1}}", 200)]
    [InlineData("{\"ok\":true,\"result\":{\"is_bot\":false,\"username\":\"person\",\"id\":1}}", 200)]
    public async Task InvalidRevokedOrMalformedBotIdentityCannotStartPolling(string body, int status)
    {
        var ready = false;
        var handler = new Handler((_, _) => Task.FromResult(Reply(body, (HttpStatusCode)status)));
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ =>
        { ready = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(ready);
        Assert.DoesNotContain("getUpdates", handler.Methods);
        Assert.DoesNotContain(Token, error.Message);
    }

    [Fact]
    public async Task ExistingWebhookIsPreservedAndPollingDoesNotStart()
    {
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getWebhookInfo"
            ? Reply("{\"ok\":true,\"result\":{\"url\":\"https://existing.example/sensitive\"}}") : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("webhook", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "getMe", "getWebhookInfo" }, handler.Methods);
        Assert.DoesNotContain("existing.example", error.Message);
    }

    [Fact]
    public async Task WrongCodesUnrelatedChatsAndGroupsDoNotPairButMatchingPrivateChatDoes()
    {
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update("wrong-code", clock, 111), Update(code, clock, -100123, "supergroup", 8),
                Update(code, clock, -100124, "channel", 9), Update(code, clock, 777, id: 10)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var result = await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("777", result.ChatId);
        Assert.Single(handler.Methods, method => method == "sendMessage");
    }

    [Fact]
    public async Task DuplicateUpdatesDoNotDuplicateConfirmation()
    {
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update(code, clock), Update(code, clock)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, handler.Methods.Count(method => method == "getUpdates"));
        Assert.Equal(1, handler.Methods.Count(method => method == "sendMessage"));
    }

    [Fact]
    public async Task ExpiredCodeCannotPairEvenWhenMatchingMessageArrives()
    {
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) =>
        {
            if (Method(request) != "getUpdates") return Task.FromResult(Setup(Method(request)));
            clock.Now += TimeSpan.FromMinutes(6);
            return Task.FromResult(Updates(Update(code, clock)));
        });
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), prompt =>
        { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sendMessage", handler.Methods);
    }

    [Fact]
    public async Task EveryAttemptUsesFreshCodeAndRejectsOldCode()
    {
        var clock = new Clock(); string code = "", oldCode = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update(oldCode, clock, 111), Update(code, clock, 222, id: 8)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        oldCode = code;
        var result = await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        Assert.NotEqual(oldCode, code);
        Assert.Equal("222", result.ChatId);
    }

    [Fact]
    public async Task CancellationStopsActiveLongPollWithoutConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, ct) =>
        {
            if (Method(request) != "getUpdates") return Setup(Method(request));
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Updates();
        });
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var task = client.PairAsync(Config(), _ => Task.CompletedTask, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(handler.Methods, method => method == "getUpdates");
        Assert.DoesNotContain("sendMessage", handler.Methods);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    public async Task PermanentPollingFailuresStopWithoutTakingOverBot(int status)
    {
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Reply(JsonSerializer.Serialize(new { ok = false, error_code = status, description = Token }), (HttpStatusCode)status)
            : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.DoesNotContain(Token, error.Message);
        Assert.Equal(1, handler.Methods.Count(method => method == "getUpdates"));
        Assert.DoesNotContain("deleteWebhook", handler.Methods);
        if (status == 409) Assert.Contains("another", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedConfirmationNeverReportsConnectionSuccess()
    {
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) switch
        {
            "getUpdates" => Updates(Update(code, clock)),
            "sendMessage" => Reply("{\"ok\":false,\"error_code\":403}", HttpStatusCode.Forbidden),
            _ => Setup(Method(request))
        }));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), prompt =>
        { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None));
        Assert.DoesNotContain(Token, error.Message);
        Assert.Equal(1, handler.Methods.Count(method => method == "sendMessage"));
    }

    [Fact]
    public async Task EmptyResultsContinueUntilMatchingMessageWithoutOffsetOrOverlappingRequests()
    {
        var clock = new Clock(); string code = ""; var polls = 0; var active = 0;
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                return Method(request) == "getUpdates" ? ++polls == 1 ? Updates() : Updates(Update(code, clock)) : Setup(Method(request));
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, polls);
        Assert.All(handler.Queries, query => Assert.DoesNotContain("offset", query));
    }

    [Fact]
    public async Task RateLimitWaitIsRespectedThenPairingRecovers()
    {
        var clock = new Clock(); string code = ""; var polls = 0;
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? ++polls == 1 ? Reply("{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":1}}", HttpStatusCode.TooManyRequests)
                : Updates(Update(code, clock)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, polls);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("server")]
    public async Task TransientFailuresAreBoundedAndSecretsAreRedacted(string kind)
    {
        var handler = new Handler((_, _) => kind switch
        {
            "network" => throw new HttpRequestException("https://api.telegram.org/bot" + Token + "/getMe"),
            "timeout" => throw new TaskCanceledException("sensitive " + Token),
            _ => Task.FromResult(Reply("sensitive " + Token, HttpStatusCode.InternalServerError))
        });
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.InRange(handler.Methods.Count, 1, 3);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain("api.telegram.org", error.ToString());
        Assert.DoesNotContain("getUpdates", handler.Methods);
    }
    [Fact]
    public async Task DisposingPairingClientCancelsPendingPoll()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, ct) =>
        {
            if (Method(request) != "getUpdates") return Setup(Method(request));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Updates();
        });
        var client = new TelegramPairingClient(httpHandler: handler);
        var pairing = client.PairAsync(Config(), _ => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing);
        Assert.DoesNotContain("sendMessage", handler.Methods);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"ok\":true,\"result\":{}}")]
    [InlineData("{\"ok\":true}")]
    public async Task MalformedUpdatesFailWithoutConfirmation(string body)
    {
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Reply(body) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler);
        await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask));
        Assert.DoesNotContain("sendMessage", handler.Methods);
    }

    [Fact]
    public async Task RedirectDoesNotReachUntrustedDestination()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Reply("", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://untrusted.example/" + Token);
            return Task.FromResult(response);
        });
        await using var client = new TelegramPairingClient(httpHandler: handler);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask));
        Assert.Single(handler.Methods);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.DoesNotContain("untrusted.example", error.ToString());
    }

    [Fact]
    public async Task FullUnacknowledgedBacklogStopsRatherThanDiscardingUpdates()
    {
        var clock = new Clock();
        var unrelated = Enumerable.Range(0, 100).Select(index => Update("wrong-code", clock, id: index)).ToArray();
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(unrelated) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var error = await Assert.ThrowsAsync<TelegramPairingException>(() => client.PairAsync(Config(), _ => Task.CompletedTask));
        Assert.Contains("pending", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Methods, method => method == "getUpdates");
        Assert.All(handler.Queries, query => Assert.DoesNotContain("offset", query));
        Assert.DoesNotContain("sendMessage", handler.Methods);
    }

    private sealed class HttpEvents : System.Diagnostics.Tracing.EventListener
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "System.Net.Http") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.Payload is { } values)
                Messages?.Enqueue(e.EventName + ": " + string.Join(" ", values.Select(value => value?.ToString())));
        }
    }

    [Fact]
    public async Task PairingHttpDiagnosticsNeverIncludeTokenOrPairingCode()
    {
        using var events = new HttpEvents();
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update(code, clock)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; });
        Assert.Contains(events.Messages, message => message.Contains("RequestStart"));
        Assert.DoesNotContain(events.Messages, message => message.Contains(Token) || message.Contains(code));
    }

    [Fact]
    public async Task StaleMessagesAndInvalidChatIdentifiersAreIgnored()
    {
        var clock = new Clock(); string code = "";
        var handler = new Handler((request, _) =>
        {
            if (Method(request) != "getUpdates") return Task.FromResult(Setup(Method(request)));
            var earlier = new Clock { Now = clock.Now.AddMinutes(-1) };
            var future = new Clock { Now = clock.Now.AddMinutes(10) };
            return Task.FromResult(Updates(Update(code, earlier, 111), Update(code, future, 222),
                Update(code, clock, -44), Update(code, clock, 0), Update(code, clock, 888, id: 12)));
        });
        await using var client = new TelegramPairingClient(httpHandler: handler, timeProvider: clock);
        var result = await client.PairAsync(Config(), prompt => { code = Code(prompt); return Task.CompletedTask; });
        Assert.Equal("888", result.ChatId);
    }

    private sealed class BlockingSecrets : IWebhookSecretStore, IDisposable
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReaderThread;
        public string Store(string value, string? existingId = null) => throw new InvalidOperationException("No writes permitted");
        public void Delete(string id) => throw new InvalidOperationException("No deletion permitted");
        public string Retrieve(string id)
        {
            ReaderThread = Environment.CurrentManagedThreadId;
            Entered.TrySetResult();
            try { Release.Wait(TimeSpan.FromSeconds(20)); return Token; }
            finally { Exited.TrySetResult(); }
        }
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task SavedCredentialReconnectReturnsToCallerAndCancelsWhileStoreIsBlocked()
    {
        using var store = new BlockingSecrets();
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((_, _) => throw new InvalidOperationException("No HTTP before credential access completes"));
        await using var client = new TelegramPairingClient(secretStore: store, httpHandler: handler);
        var callerThread = 0;
        // Dedicated caller models the desktop. The old synchronous read blocks
        // this initiation task; the test can still release it safely on failure.
        var initiation = Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            return client.PairAsync(Config() with { TelegramBotToken = "", TelegramBotTokenSecretId = "saved-reference" },
                _ => Task.CompletedTask, cancellation.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var pairing = await initiation.WaitAsync(TimeSpan.FromSeconds(5));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(callerThread, store.ReaderThread);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(store.Exited.Task.IsCompleted); // Cancellation did not wait on native code.
            Assert.Empty(handler.Methods);
        }
        finally
        {
            cancellation.Cancel();
            store.Release.Set();
            await store.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ReadOnlySecrets : IWebhookSecretStore
    {
        public int Reads;
        public string Retrieve(string id)
        {
            Assert.Equal("saved-reference", id);
            Reads++;
            return Token;
        }
        public string Store(string value, string? existingId = null) => throw new InvalidOperationException("Pairing must not save credentials");
        public void Delete(string id) => throw new InvalidOperationException("Pairing must not delete credentials");
    }

    [Fact]
    public async Task SavedTokenIsReadWithoutPersistingOrMutatingTheDraft()
    {
        var clock = new Clock(); string code = "";
        var secrets = new ReadOnlySecrets();
        var draft = Config() with { TelegramBotToken = "", TelegramBotTokenSecretId = "saved-reference", TelegramChatId = "old-destination" };
        var handler = new Handler((request, _) => Task.FromResult(Method(request) == "getUpdates"
            ? Updates(Update(code, clock)) : Setup(Method(request))));
        await using var client = new TelegramPairingClient(secretStore: secrets, httpHandler: handler, timeProvider: clock);
        var result = await client.PairAsync(draft, prompt => { code = Code(prompt); return Task.CompletedTask; });
        Assert.Equal(1, secrets.Reads);
        Assert.Equal("4294967296", result.ChatId);
        Assert.Equal("old-destination", draft.TelegramChatId);
        Assert.Equal("saved-reference", draft.TelegramBotTokenSecretId);
        Assert.Empty(draft.TelegramBotToken);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(draft));
    }

}
