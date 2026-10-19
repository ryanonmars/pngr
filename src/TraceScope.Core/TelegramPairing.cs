using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

/// <summary>A temporary, user-initiated connection prompt. The link contains no bot credential.</summary>
public sealed record TelegramPairingPrompt(string BotUsername, string DeepLink, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "TelegramPairingPrompt { temporary connection link redacted }";
}

public sealed record TelegramPairingResult(string ChatId, string BotUsername);

/// <summary>Only locally authored, credential-free explanations may reach the settings UI.</summary>
public sealed class TelegramPairingException(string message) : IOException(message);

/// <summary>
/// Short-lived private-chat discovery, never a background Telegram consumer. In
/// particular, no offset is supplied: unrelated updates are never acknowledged.
/// </summary>
public sealed class TelegramPairingClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly IWebhookSecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private int _disposed;

    public TelegramPairingClient(IWebhookSecretStore? secretStore = null, HttpMessageHandler? httpHandler = null,
        TimeProvider? timeProvider = null)
    {
        _secrets = secretStore ?? WebhookSecretStore.Default;
        _time = timeProvider ?? TimeProvider.System;
        _http = new HttpClient(new TelegramCredentialHandler(
            httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false },
            httpHandler is null ? new SocketsHttpHandler
            { AllowAutoRedirect = false, ActivityHeadersPropagator = null, UseProxy = false } : null))
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Returns a verified destination in memory. Does not write settings or credentials.</summary>
    public async Task<TelegramPairingResult> PairAsync(WebhookAlertConfiguration draft,
        Func<TelegramPairingPrompt, Task> onReady, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new TelegramPairingException("Telegram pairing is already active. Cancel it before trying again.");
        using var expiry = new CancellationTokenSource(TimeSpan.FromMinutes(5), _time);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, expiry.Token);
        var started = _time.GetUtcNow();
        var expires = started.AddMinutes(5);
        try
        {
            string token;
            try
            {
                // A native store read may wait on the OS. Reconnect must return to
                // the desktop immediately, and cancellation/expiry must not wait
                // for the synchronous store. The macOS reader itself has a bounded
                // child lifetime; never cache the token or relax Keychain access.
                token = draft.TelegramBotToken.Length > 0 ? draft.TelegramBotToken :
                    await Task.Run(() => _secrets.Retrieve(draft.TelegramBotTokenSecretId), session.Token)
                        .WaitAsync(TimeSpan.FromSeconds(10), session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { throw; }
            catch (TimeoutException) { throw new TelegramPairingException("Secure credential access timed out. Try reconnecting or replace the Bot Token."); }
            catch { throw new TelegramPairingException("Unable to read the Telegram Bot Token. Replace it in settings."); }
            TelegramDelivery.ValidateBotToken(token);
            var me = await GetAsync("getMe", token, session.Token);
            if (me.ValueKind != JsonValueKind.Object || !me.TryGetProperty("id", out var botId) ||
                botId.ValueKind != JsonValueKind.Number || !botId.TryGetInt64(out var id) || id <= 0 ||
                !me.TryGetProperty("is_bot", out var isBot) || isBot.ValueKind != JsonValueKind.True ||
                !me.TryGetProperty("username", out var usernameValue) || usernameValue.ValueKind != JsonValueKind.String ||
                usernameValue.GetString() is not { } username ||
                !Regex.IsMatch(username, @"\A[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant))
                throw new TelegramPairingException("Telegram did not return a valid bot username. Check the bot through BotFather.");
            var webhook = await GetAsync("getWebhookInfo", token, session.Token);
            if (webhook.ValueKind != JsonValueKind.Object || !webhook.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                throw new TelegramPairingException("Telegram returned invalid webhook information. Try again later.");
            if (!string.IsNullOrEmpty(url.GetString()))
                throw new TelegramPairingException("This bot has an existing webhook integration. Pngr has not changed it. Use Advanced → Manual Chat ID or create a dedicated bot.");
            CheckExpiration(expires, session.Token);
            var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var prompt = new TelegramPairingPrompt(username, $"https://t.me/{username}?start={code}", expires);
            await onReady(prompt).WaitAsync(session.Token);
            while (true)
            {
                CheckExpiration(expires, session.Token);
                var updates = await GetAsync("getUpdates?timeout=20&limit=100", token, session.Token);
                CheckExpiration(expires, session.Token);
                if (updates.ValueKind != JsonValueKind.Array)
                    throw new TelegramPairingException("Telegram returned invalid updates. Try connecting again.");
                foreach (var update in updates.EnumerateArray())
                {
                    var chatId = Match(update, code, username, started, _time.GetUtcNow());
                    if (chatId is null) continue;
                    // The confirmation uses the same protected sendMessage path as alerts.
                    using var confirmationDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    confirmationDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var destination = draft with { TelegramBotToken = token, TelegramChatId = chatId };
                    await new TelegramDelivery(_http, _secrets).SendAsync(
                        new MonitoringEvent(Guid.NewGuid(), Guid.Empty, _time.GetUtcNow(), "TelegramConnectionConfirmation", "", null),
                        destination, null, () => !session.IsCancellationRequested && _time.GetUtcNow() < expires,
                        confirmationDeadline.Token);
                    CheckExpiration(expires, session.Token);
                    return new(chatId, username);
                }
                // Without acknowledgment, a full queue may hide the matching message.
                // Never discard that backlog merely to make pairing easier.
                if (updates.GetArrayLength() >= 100)
                    throw new TelegramPairingException("This bot has too many pending updates for safe pairing. Use Advanced → Manual Chat ID or create a dedicated bot. No updates were discarded.");
                // Existing unrelated updates can make long polling return immediately.
                await Task.Delay(TimeSpan.FromSeconds(1), _time, session.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested &&
            (expiry.IsCancellationRequested || _time.GetUtcNow() >= expires))
        { throw new TelegramPairingException("Telegram pairing expired. Click Connect Telegram to try again."); }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { throw new TelegramPairingException("Telegram request timed out. Try connecting again."); }
        catch (WebhookDeliveryException error) { throw new TelegramPairingException(error.Message); }
        catch (TelegramPairingException) { throw; }
        catch (InvalidDataException) { throw; }
        catch { throw new TelegramPairingException("Unable to connect Telegram. Check internet connectivity and try again."); }
        finally { Volatile.Write(ref _active, 0); }
    }

    private void CheckExpiration(DateTimeOffset expires, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_time.GetUtcNow() >= expires)
            throw new TelegramPairingException("Telegram pairing expired. Click Connect Telegram to try again.");
    }

    private static string? Match(JsonElement update, string code, string username, DateTimeOffset started, DateTimeOffset now)
    {
        if (update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            !(text.GetString() == "/start " + code || text.GetString() == "/start@" + username + " " + code) ||
            !message.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Number || !date.TryGetInt64(out var timestamp) ||
            timestamp < started.ToUnixTimeSeconds() || timestamp > now.AddSeconds(30).ToUnixTimeSeconds() ||
            !message.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object ||
            !chat.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "private" ||
            !chat.TryGetProperty("id", out var chatId) || chatId.ValueKind != JsonValueKind.Number || !chatId.TryGetInt64(out var id) || id <= 0)
            return null;
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<JsonElement> GetAsync(string method, string token, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var delay = TimeSpan.FromSeconds(attempt + 1);
            string failure;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var request = new TelegramRequestMessage(method);
                request.Options.Set(TelegramCredentialHandler.BotToken, token);
                request.Headers.UserAgent.ParseAdd("Pngr (+https://github.com/ryanonmars/pngr)");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                using var body = await ReadResponseAsync(response, timeout.Token);
                var root = body?.RootElement ?? default;
                var valid = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
                if (response.IsSuccessStatusCode && valid && root.GetProperty("ok").GetBoolean() && root.TryGetProperty("result", out var result))
                    return result.Clone();
                var status = (int)response.StatusCode;
                if (status < 400 && valid && root.TryGetProperty("error_code", out var errorCode) && errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var apiCode)) status = apiCode;
                failure = status switch
                {
                    401 or 404 => "Invalid or revoked bot token. Replace the Telegram Bot Token in settings.",
                    409 => "This bot is in use by another Telegram integration. Use Advanced → Manual Chat ID or create a dedicated bot. Existing polling consumers cannot always be detected.",
                    429 => "Telegram rate limit reached. Wait before connecting again.",
                    >= 500 and <= 599 => "Telegram is temporarily unavailable. Try connecting again later.",
                    >= 300 and <= 399 => "Telegram returned a redirect. Connection stopped to protect the bot token.",
                    _ => "Telegram returned an invalid response. Check the bot configuration and try again."
                };
                if (status != 429 && status is not (>= 500 and <= 599)) throw new TelegramPairingException(failure);
                if (status == 429)
                {
                    if (valid && root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("retry_after", out var retry) && retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                        delay = TimeSpan.FromSeconds(Math.Min(seconds, 86400));
                    var header = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - _time.GetUtcNow() : TimeSpan.Zero);
                    if (header > delay) delay = header;
                }
            }
            catch (HttpRequestException) { failure = "Network connection failed. Check internet connectivity and try again."; }
            catch (IOException error) when (error is not TelegramPairingException) { failure = "Network connection failed. Check internet connectivity and try again."; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Telegram request timed out. Try connecting again."; }
            if (attempt == 2) throw new TelegramPairingException(failure);
            await Task.Delay(delay, _time, ct);
        }
        throw new TelegramPairingException("Unable to connect Telegram.");
    }

    private static async Task<JsonDocument?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // A getUpdates batch can contain 100 messages; cap buffering independently
        // of server-provided content length and never include its body in errors.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[2 * 1024 * 1024 + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count == buffer.Length) return null;
        try { return JsonDocument.Parse(buffer.AsMemory(0, count)); }
        catch (JsonException) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _http.Dispose();
            // Cancellation registrations may still be unwinding; no OS wait handle is created.
        }
        return ValueTask.CompletedTask;
    }
}
