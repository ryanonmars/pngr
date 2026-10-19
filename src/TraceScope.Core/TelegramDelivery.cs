using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

// Never surface server descriptions, request URLs, or inner exceptions: the Bot API
// embeds a credential in the URL, and even an error body may echo that credential.
internal sealed class TelegramDelivery(HttpClient http, IWebhookSecretStore secrets)
{
    private string? _rateLimitKey;
    private DateTimeOffset _notBefore;

    internal static void ValidateBotToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256 ||
            !Regex.IsMatch(token, @"\A[0-9]+:[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("A valid Telegram Bot Token is required.");
    }

    internal static void ValidateChatId(string chatId)
    {
        // Keep identifiers as strings, including negative supergroup IDs. Telegram
        // supports signed 64-bit IDs and public channel usernames as destinations.
        // Collectible channel usernames can be shorter than five characters;
        // Telegram confirms destination existence and permissions on delivery.
        if (string.IsNullOrEmpty(chatId) || !(Regex.IsMatch(chatId, @"\A-?[0-9]+\z", RegexOptions.CultureInvariant) &&
                long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0) &&
            !Regex.IsMatch(chatId, @"\A@[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Telegram Chat ID requires a nonzero numeric ID or a channel username such as @channelname.");
    }

    internal static string Message(MonitoringEvent e, WebhookEventContext? context)
    {
        if (e.Kind == "TelegramConnectionConfirmation")
            return "✅ Pngr Connected\n\nYour private chat is connected. Return to Pngr and click Apply or OK to save notifications.";
        if (e.Kind is "WebhookTest" or "Test")
            return "🧪 Pngr — Telegram Test\n\nYour Telegram notification configuration is working.";
        var title = e.Kind switch
        {
            "Alert" => "🔴 Pngr — Network Alert",
            "Recovery" => "🟢 Pngr — Network Recovered",
            "RouteChange" => "🔵 Pngr — Route Changed",
            _ => "🔵 Pngr — Network Notification"
        };
        var lines = new List<string> { title, "" };
        if (!string.IsNullOrWhiteSpace(context?.Target)) lines.Add("Target: " + Limit(context.Target, 512));
        var rule = context?.AlertRule ?? e.AlertRuleId;
        if (!string.IsNullOrWhiteSpace(rule)) lines.Add("Alert rule: " + Limit(rule, 512));
        // Measurements and thresholds exist in the recorded event message. Do not
        // reconstruct them or infer values that the event did not supply.
        if (!string.IsNullOrWhiteSpace(e.Message)) lines.Add(Limit(e.Message, 2800));
        lines.Add("Session: " + e.SessionId);
        lines.Add("Time: " + e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        return Limit(string.Join('\n', lines), 4096);
    }

    internal static string Payload(MonitoringEvent e, WebhookAlertConfiguration config, WebhookEventContext? context)
        => JsonSerializer.Serialize(new { chat_id = config.TelegramChatId, text = Message(e, context), disable_web_page_preview = true });

    internal async Task SendAsync(MonitoringEvent e, WebhookAlertConfiguration config, WebhookEventContext? context,
        Func<bool> isCurrent, CancellationToken ct)
    {
        ValidateChatId(config.TelegramChatId);
        string token;
        try { token = !string.IsNullOrEmpty(config.TelegramBotToken) ? config.TelegramBotToken : secrets.Retrieve(config.TelegramBotTokenSecretId); }
        catch (WebhookCredentialException error)
        {
            // Only the native numeric status is retained. Never attach the original
            // exception: other stores may include credential data in their errors.
            throw new WebhookDeliveryException($"Telegram Bot Token could not be read from Keychain (OSStatus {error.OSStatus}). No Telegram request was sent. Use Change Bot Token in settings to save a replacement.");
        }
        catch { throw new WebhookDeliveryException("Unable to read the Telegram Bot Token from secure storage. No Telegram request was sent. Use Change Bot Token in settings to save a replacement."); }
        ValidateBotToken(token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (_rateLimitKey != key) { _rateLimitKey = key; _notBefore = default; }
        var body = Payload(e, config, context);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var remaining = _notBefore - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await WaitAsync(remaining, ct);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new WebhookDeliveryException("Webhook configuration changed; delivery stopped.");
            TimeSpan delay = TimeSpan.FromSeconds(attempt + 1);
            string failure;
            try
            {
                using var request = new TelegramRequestMessage()
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Options.Set(TelegramCredentialHandler.BotToken, token);
                request.Headers.UserAgent.ParseAdd("Pngr (+https://github.com/ryanonmars/pngr)");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var result = await ReadResponse(response, ct);
                if (response.IsSuccessStatusCode && result.Ok) return;
                var httpStatus = (int)response.StatusCode;
                var code = httpStatus >= 400 ? httpStatus : result.ErrorCode;
                if (code == 429)
                {
                    delay = RetryDelay(response, result.RetryAfter, delay);
                    _notBefore = delay > DateTimeOffset.MaxValue - DateTimeOffset.UtcNow ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + delay;
                }
                failure = ErrorMessage(code, result.Description) + $" (Telegram HTTP {httpStatus}, API {result.ErrorCode}.)";
                if (code != 429 && code is not (>= 500 and <= 599)) throw new WebhookDeliveryException(failure);
            }
            catch (HttpRequestException) { failure = "Network connection failed. Check internet connectivity and retry."; }
            catch (IOException error) when (error is not WebhookDeliveryException) { failure = "Network connection failed. Check internet connectivity and retry."; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Telegram request timed out. Retry the notification."; }
            catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
            if (attempt == 2) throw new WebhookDeliveryException(failure);
            await WaitAsync(delay, ct);
        }
    }

    private sealed record ApiResult(bool Ok, int ErrorCode = 0, string? Description = null, double? RetryAfter = null);
    private static async Task<ApiResult> ReadResponse(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        // Successful sendMessage results can echo 4096 characters plus JSON escapes.
        var bytes = new byte[65537]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count < bytes.Length)
            try
            {
                using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var code = root.TryGetProperty("error_code", out var codeValue) && codeValue.ValueKind == JsonValueKind.Number && codeValue.TryGetInt32(out var number) ? number : 0;
                    var description = root.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() : null;
                    double? retry = root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("retry_after", out var retryValue) && retryValue.ValueKind == JsonValueKind.Number && retryValue.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
                    return new(ok.GetBoolean(), code, description, retry);
                }
            }
            catch (JsonException) { }
        // Preserve HTTP classification even when the error page is not JSON.
        if (!response.IsSuccessStatusCode) return new(false);
        throw new WebhookDeliveryException("Telegram returned an invalid response. Retry the notification.");
    }

    private static string ErrorMessage(int code, string? description) => code switch
    {
        401 or 404 => "Invalid bot token. Replace the Telegram Bot Token in settings.",
        403 => "Bot does not have permission to send messages. Unblock it or check group/channel permissions.",
        400 when description?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true => "Chat ID not found. Check the Chat ID and start the bot or add it to the destination.",
        400 when description?.Contains("rights", StringComparison.OrdinalIgnoreCase) == true || description?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true => "Bot does not have permission to send messages. Check group/channel permissions.",
        400 => "Telegram rejected the notification. Check the Chat ID and bot access to the destination.",
        429 => "Telegram rate limit reached. Try again after the requested cooldown.",
        >= 500 and <= 599 => "Telegram is temporarily unavailable. Try again later.",
        >= 300 and <= 399 => "Telegram returned a redirect; delivery was stopped to protect the bot token.",
        _ => "Telegram did not confirm delivery. Check the configuration and retry."
    };

    private static TimeSpan RetryDelay(HttpResponseMessage response, double? seconds, TimeSpan fallback)
    {
        var body = seconds is { } value ? TimeSpan.FromSeconds(Math.Min(value, TimeSpan.MaxValue.TotalSeconds / 2)) : fallback;
        var header = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        return header > body ? header : body;
    }

    private static Task WaitAsync(TimeSpan delay, CancellationToken ct)
        => Task.Delay(delay > TimeSpan.FromSeconds(120) ? Timeout.InfiniteTimeSpan : delay, ct);

    private static string Limit(string value, int max)
    {
        if (value.Length <= max) return value;
        var end = max - 1;
        if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end] + "…";
    }
}

// HttpClient's EventSource records a request URL before entering the handler chain.
// Keep that outer URL credential-free, and only materialize the Bot API URL inside
// the transport. Restore it before HttpClient can record completion or errors.
// The underlying SocketsHttpHandler must set AllowAutoRedirect=false and
// ActivityHeadersPropagator=null (which omits its DiagnosticsHandler in .NET 10).
internal sealed class TelegramCredentialHandler(HttpMessageHandler inner, HttpMessageHandler? telegramTransport = null) : DelegatingHandler(inner)
{
    private readonly TelegramTransportHandler? _telegramTransport = telegramTransport is null ? null : new(telegramTransport);
    internal static readonly Uri SafeEndpoint = new("https://api.telegram.org/sendMessage");
    internal static readonly HttpRequestOptionsKey<string> BotToken = new("Pngr.Telegram.BotToken");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Options.TryGetValue(BotToken, out var token)) return await base.SendAsync(request, ct);
        var safeUri = request.RequestUri;
        var method = safeUri?.AbsolutePath.TrimStart('/');
        if (safeUri is null || safeUri.Scheme != "https" || safeUri.Host != "api.telegram.org" ||
            !safeUri.IsDefaultPort || safeUri.UserInfo.Length != 0 || safeUri.Fragment.Length != 0 ||
            !(method == "sendMessage" && request.Method == HttpMethod.Post && safeUri.Query.Length == 0 ||
              method is "getMe" or "getWebhookInfo" && request.Method == HttpMethod.Get && safeUri.Query.Length == 0 ||
              method == "getUpdates" && request.Method == HttpMethod.Get && safeUri.Query == "?timeout=20&limit=100"))
            throw new WebhookDeliveryException("Invalid Telegram request destination.");
        TelegramDelivery.ValidateBotToken(token);
        try
        {
            request.RequestUri = new Uri($"https://api.telegram.org/bot{token}/{method}{safeUri.Query}");
            return _telegramTransport is null ? await base.SendAsync(request, ct) : await _telegramTransport.SendTransportAsync(request, ct);
        }
        catch (HttpRequestException) { throw new HttpRequestException("Network connection failed."); }
        catch (IOException) { throw new IOException("Network connection failed."); }
        catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
        catch { throw new WebhookDeliveryException("Telegram delivery failed. Check the configuration and connection."); }
        finally
        {
            request.RequestUri = safeUri;
            ((IDictionary<string, object?>)request.Options).Remove(BotToken.Key);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _telegramTransport?.Dispose();
        base.Dispose(disposing);
    }

    // Calling the protected handler pipeline directly avoids introducing a second
    // HttpMessageInvoker EventSource scope around the credential-bearing URL.
    private sealed class TelegramTransportHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal Task<HttpResponseMessage> SendTransportAsync(HttpRequestMessage request, CancellationToken ct)
            => base.SendAsync(request, ct);
    }
}

// Low-level System.Net.Http internal diagnostic traces stringify the request.
// Never let that representation include the credential-bearing transport URL.
internal sealed class TelegramRequestMessage : HttpRequestMessage
{
    internal TelegramRequestMessage() : base(HttpMethod.Post, TelegramCredentialHandler.SafeEndpoint) { }
    internal TelegramRequestMessage(string method) : base(HttpMethod.Get, new Uri("https://api.telegram.org/" + method)) { }
    public override string ToString() => "Pngr Telegram request [credential redacted]";
}
