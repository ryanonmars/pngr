using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

// Only messages authored here may reach the action log; server bodies and exception
// details can contain credential URLs and must never be included in diagnostics.
internal sealed class WebhookDeliveryException(string message) : IOException(message);

internal sealed class WebhookDelivery(HttpClient http, IWebhookSecretStore secrets)
{
    private readonly TelegramDelivery _telegram = new(http, secrets);
    private string? _rateLimitKey;
    private DateTimeOffset _notBefore;

    internal static void ValidateConfiguration(WebhookAlertConfiguration config)
    {
        if (!Enum.IsDefined(config.Format) || config.Url is null || config.UrlSecretId is null)
            throw new InvalidDataException("Invalid webhook configuration or format.");
        if (config.UrlSecretId.Length > 0 && !Guid.TryParseExact(config.UrlSecretId, "N", out _))
            throw new InvalidDataException("Invalid webhook credential reference.");
        if (config.TelegramChatId is null || config.TelegramBotTokenSecretId is null || config.TelegramBotToken is null)
            throw new InvalidDataException("Invalid Telegram configuration.");
        if (config.TelegramBotTokenSecretId.Length > 0 && !Guid.TryParseExact(config.TelegramBotTokenSecretId, "N", out _))
            throw new InvalidDataException("Invalid Telegram credential reference.");
        if (config.Format == WebhookFormat.Telegram)
        {
            if (config.Enabled)
            {
                TelegramDelivery.ValidateChatId(config.TelegramChatId);
                if (config.TelegramBotToken.Length > 0) TelegramDelivery.ValidateBotToken(config.TelegramBotToken);
                else if (config.TelegramBotTokenSecretId.Length == 0) throw new InvalidDataException("Telegram Bot Token is required.");
            }
            return;
        }
        if (config.Enabled && config.UrlSecretId.Length == 0) ValidateUrl(config.Url, config.Format);
    }

    internal static Uri ValidateUrl(string url, WebhookFormat format)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Webhook requires an HTTP(S) URL without embedded credentials.");
        if (format == WebhookFormat.Discord && (uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.Host is not ("discord.com" or "canary.discord.com" or "ptb.discord.com") || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !Regex.IsMatch(uri.AbsolutePath, @"^/api/(?:v[0-9]+/)?webhooks/[0-9]{1,20}/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Discord requires a complete HTTPS Discord webhook URL, without query parameters or a fragment.");
        return uri;
    }

    internal async Task SendAsync(MonitoringEvent e, WebhookAlertConfiguration config, WebhookEventContext? context,
        Func<bool> isCurrent, CancellationToken ct)
    {
        if (config.Format == WebhookFormat.Telegram)
        {
            await _telegram.SendAsync(e, config, context, isCurrent, ct);
            return;
        }
        var url = config.UrlSecretId.Length > 0 ? secrets.Retrieve(config.UrlSecretId) : config.Url;
        var uri = ValidateUrl(url, config.Format);
        var discord = config.Format == WebhookFormat.Discord;
        var body = discord ? DiscordPayload(e, context) : JsonSerializer.Serialize(e);
        if (discord)
        {
            // Ask Discord to confirm creation instead of accepting an unconfirmed fire-and-forget response.
            uri = new UriBuilder(uri) { Query = "wait=true" }.Uri;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            if (_rateLimitKey != key) { _rateLimitKey = key; _notBefore = default; }
        }
        var attempts = discord ? 3 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var remaining = _notBefore - DateTimeOffset.UtcNow;
            if (discord && remaining > TimeSpan.Zero) await WaitAsync(remaining, ct);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) throw new WebhookDeliveryException("Webhook configuration changed; delivery stopped.");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.UserAgent.ParseAdd("Pngr (+https://github.com/ryanonmars/pngr)");
            if (!discord && !string.IsNullOrEmpty(config.BearerTokenEnvironmentVariable))
                request.Headers.Authorization = new("Bearer", Environment.GetEnvironmentVariable(config.BearerTokenEnvironmentVariable)
                    ?? throw new WebhookDeliveryException("Webhook bearer-token environment variable is not configured."));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return;
            var status = (int)response.StatusCode;
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status is >= 500 and <= 599;
            var delay = TimeSpan.FromSeconds(attempt + 1);
            if (discord && transient)
            {
                delay = await RetryDelay(response, delay, ct);
                // Retain cooldown for subsequent queued events, even if this action times out.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    _notBefore = delay > DateTimeOffset.MaxValue - DateTimeOffset.UtcNow ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + delay;
            }
            if (!discord || !transient || attempt == attempts - 1)
                throw new WebhookDeliveryException($"Webhook returned HTTP {status}.");
            await WaitAsync(delay, ct);
        }
    }

    private static Task WaitAsync(TimeSpan delay, CancellationToken ct)
        // The maximum configured action deadline is 120 seconds. Longer waits cannot
        // complete within it; never shorten Retry-After and send prematurely.
        => Task.Delay(delay > TimeSpan.FromSeconds(120) ? Timeout.InfiniteTimeSpan : delay, ct);

    private static async Task<TimeSpan> RetryDelay(HttpResponseMessage response, TimeSpan fallback, CancellationToken ct)
    {
        TimeSpan? header = null;
        // Discord can return fractional seconds, in addition to ordinary HTTP-date values.
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var value = values.FirstOrDefault();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                header = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
            else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                header = date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        }
        TimeSpan? body = null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Bound response buffering; diagnostics deliberately never include its content.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8193]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count < buffer.Length)
                try
                {
                    using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
                    if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("retry_after", out var retry) && retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                        body = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
                }
                catch (JsonException) { }
        }
        // Never retry earlier than either valid instruction. Large waits remain subject
        // to the action deadline; do not shorten server-directed waits.
        return header is { } h && body is { } b ? (h > b ? h : b) : header ?? body ?? fallback;
    }

    internal static string DiscordPayload(MonitoringEvent e, WebhookEventContext? context)
    {
        var fields = new List<(string Name, string Value)> { ("Event type", e.Kind), ("Session", e.SessionId.ToString()) };
        if (!string.IsNullOrWhiteSpace(context?.Target)) fields.Add(("Target", Limit(context.Target, 1024)));
        var rule = context?.AlertRule ?? e.AlertRuleId;
        if (!string.IsNullOrWhiteSpace(rule)) fields.Add(("Alert rule", Limit(rule, 1024)));
        // Maximum combined embed text is 6000. Leave room for fields, title and labels.
        var descriptionLimit = Math.Min(4096, 5800 - fields.Sum(f => f.Value.Length));
        return JsonSerializer.Serialize(new
        {
            allowed_mentions = new { parse = Array.Empty<string>(), replied_user = false },
            embeds = new[] { new { title = $"Pngr — {e.Kind}", description = Limit(e.Message ?? "", descriptionLimit),
                color = e.Kind switch { "Alert" => 0xed4245, "Recovery" => 0x57f287, "RouteChange" => 0x3498db, _ => 0x3498db },
                timestamp = e.Timestamp.ToUniversalTime().ToString("O"), fields = fields.Select(f => new { name = f.Name, value = f.Value, inline = true }).ToArray() } }
        });
    }
    private static string Limit(string value, int max)
    {
        if (value.Length <= max) return value;
        // Avoid splitting a UTF-16 surrogate pair when truncating recorded text.
        var end = max - 1;
        if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end] + "…";
    }
}
