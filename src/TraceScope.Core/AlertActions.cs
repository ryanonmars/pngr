using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Threading.Channels;

namespace TraceScope.Core;

public sealed record SmtpAlertConfiguration
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool UseTls { get; init; } = true;
    public string Username { get; init; } = "";
    public string PasswordEnvironmentVariable { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}
public enum WebhookFormat { GenericJson, Discord }
public sealed record WebhookEventContext(string? Target = null, string? AlertRule = null);

public sealed record WebhookAlertConfiguration
{
    public bool Enabled { get; init; }
    // Url is retained only to import legacy configurations. Saves migrate it to secure storage.
    public string Url { get; init; } = "";
    public string UrlSecretId { get; init; } = "";
    public WebhookFormat Format { get; init; } = WebhookFormat.GenericJson;
    public override string ToString() => $"WebhookAlertConfiguration {{ Enabled = {Enabled}, Format = {Format}, Url = [redacted] }}";
    public string BearerTokenEnvironmentVariable { get; init; } = "";
}
public sealed record ScriptAlertConfiguration
{
    public bool Enabled { get; init; }
    public string Executable { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
}
public sealed record AlertActionConfiguration
{
    public bool NativeNotificationsEnabled { get; init; }
    public bool IncludeRecovery { get; init; } = true;
    public int MinimumNotificationSpacingSeconds { get; init; } = 30;
    public int MinimumActionSpacingSeconds { get; init; } = 30;
    public int ActionTimeoutSeconds { get; init; } = 15;
    public SmtpAlertConfiguration Smtp { get; init; } = new();
    public WebhookAlertConfiguration Webhook { get; init; } = new();
    public ScriptAlertConfiguration Script { get; init; } = new();

    public void Validate()
    {
        if (MinimumNotificationSpacingSeconds is < 0 or > 86400 || MinimumActionSpacingSeconds is < 0 or > 86400 || ActionTimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("Alert spacing must be 0–86400 seconds; action timeout 1–120 seconds.");
        if (Smtp is null || Webhook is null || Script is null || Script.Arguments is null || Script.Arguments.Any(a => a is null))
            throw new InvalidDataException("Alert action configuration cannot contain null settings.");
        if (Smtp.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Smtp.Host) || Smtp.Port is < 1 or > 65535) throw new InvalidDataException("SMTP requires a host and valid port.");
            try { _ = new MailAddress(Smtp.From); _ = new MailAddress(Smtp.To); }
            catch (Exception error) when (error is ArgumentException or FormatException) { throw new InvalidDataException("SMTP sender and recipient must be email addresses."); }
        }
        WebhookDelivery.ValidateConfiguration(Webhook);
        if (Script.Enabled && (string.IsNullOrEmpty(Script.Executable) || !Path.IsPathFullyQualified(Script.Executable) || Script.Arguments.Length > 128 ||
            !string.IsNullOrEmpty(Script.WorkingDirectory) && !Path.IsPathFullyQualified(Script.WorkingDirectory)))
            throw new InvalidDataException("Script action requires an absolute executable path and working directory.");
        foreach (var name in new[] { Smtp.PasswordEnvironmentVariable, Webhook.BearerTokenEnvironmentVariable })
            if (name is null || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')) || name.Length > 128)
                throw new InvalidDataException("Secret references must be environment variable names.");
    }
}
public sealed record NotificationResult(bool Success, string Message);
public interface IDesktopNotifier : IAsyncDisposable
{
    Task<NotificationResult> RequestPermissionAsync(CancellationToken cancellationToken = default);
    Task<NotificationResult> SendAsync(string title, string message, CancellationToken cancellationToken = default);
}
public sealed record AlertActionResult(Guid EventId, string Action, bool Success, string Message);

/// <summary>Opt-in actions remain in the desktop process. Bounded queue, serial execution, independent
/// notification/action spacing, strict timeouts, and process-tree cancellation prevent action storms.</summary>
public sealed class AlertActionDispatcher : IAsyncDisposable
{
    private sealed record Work(MonitoringEvent Event, AlertActionConfiguration Configuration, WebhookEventContext? Context);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly IDesktopNotifier _notifier;
    private readonly HttpClient _http;
    private readonly WebhookDelivery _webhooks;
    private readonly Func<MonitoringEvent, WebhookEventContext?>? _webhookContext;
    private readonly Task _worker;
    private readonly Dictionary<(Guid Session, string Rule, string Kind), DateTimeOffset> _lastActions = [];
    private DateTimeOffset? _lastNotification;
    private AlertActionConfiguration _configuration;
    private int _disposed;
    public event Action<AlertActionResult>? ActionCompleted;

    public AlertActionDispatcher(AlertActionConfiguration? configuration = null, IDesktopNotifier? notifier = null,
        HttpMessageHandler? httpHandler = null, IWebhookSecretStore? secretStore = null,
        Func<MonitoringEvent, WebhookEventContext?>? webhookContext = null)
    {
        _configuration = configuration ?? new(); _configuration.Validate();
        _configuration = _configuration with { Script = _configuration.Script with { Arguments = _configuration.Script.Arguments.ToArray() } };
        _notifier = notifier ?? DesktopNotifier.Create();
        _http = new HttpClient(httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _webhooks = new WebhookDelivery(_http, secretStore ?? WebhookSecretStore.Default);
        _webhookContext = webhookContext;
        _worker = ProcessQueueAsync();
    }
    public void Configure(AlertActionConfiguration configuration)
    {
        configuration.Validate();
        // Own an immutable snapshot, including argument array supplied by the caller.
        Volatile.Write(ref _configuration, configuration with { Script = configuration.Script with { Arguments = configuration.Script.Arguments.ToArray() } });
    }
    public Task<NotificationResult> RequestNotificationPermissionAsync(CancellationToken cancellationToken = default)
        => _notifier.RequestPermissionAsync(cancellationToken);
    public Task<NotificationResult> SendTestNotificationAsync(CancellationToken cancellationToken = default)
        => Volatile.Read(ref _configuration).NativeNotificationsEnabled
            ? _notifier.SendAsync("Pngr — notification test", "Native desktop notifications are enabled.", cancellationToken)
            : Task.FromResult(new NotificationResult(false, "Enable native notifications before sending a test."));
    public bool Enqueue(MonitoringEvent monitoringEvent) => TryEnqueue(monitoringEvent);
    public bool TryEnqueue(MonitoringEvent monitoringEvent)
    {
        if (Volatile.Read(ref _disposed) != 0 || monitoringEvent.Kind is not ("Alert" or "Recovery" or "RouteChange")) return false;
        var config = Volatile.Read(ref _configuration);
        if (monitoringEvent.Kind == "Recovery" && !config.IncludeRecovery) return false;
        if (!config.NativeNotificationsEnabled && !config.Smtp.Enabled && !config.Webhook.Enabled && !config.Script.Enabled) return false;
        WebhookEventContext? context = null;
        if (config.Webhook.Format == WebhookFormat.Discord)
            try { context = _webhookContext?.Invoke(monitoringEvent); } catch { /* Optional context must not prevent delivery. */ }
        return _queue.Writer.TryWrite(new(monitoringEvent, config, context));
    }
    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                var e = work.Event; var config = work.Configuration; var now = DateTimeOffset.UtcNow;
                // Settings changed while queued: disabled actions must not run after the user switches them off.
                var current = Volatile.Read(ref _configuration);
                if (!ReferenceEquals(config, current)) continue;
                if (e.Kind == "Recovery" && !current.IncludeRecovery) continue;
                if (config.NativeNotificationsEnabled && current.NativeNotificationsEnabled &&
                    (_lastNotification is null || now - _lastNotification >= TimeSpan.FromSeconds(config.MinimumNotificationSpacingSeconds)))
                {
                    _lastNotification = now;
                    await ExecuteAsync(e, "Native notification", config.ActionTimeoutSeconds,
                        async ct => { var result = await _notifier.SendAsync(e.Kind == "Recovery" ? "Pngr — recovered" : "Pngr — network alert", e.Message, ct);
                            if (!result.Success) throw new IOException(result.Message); });
                }
                if (!ReferenceEquals(config, Volatile.Read(ref _configuration))) continue;
                var key = (e.SessionId, e.AlertRuleId ?? "", e.Kind);
                if (_lastActions.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(config.MinimumActionSpacingSeconds)) continue;
                _lastActions[key] = now;
                if (_lastActions.Count > 4096)
                    foreach (var stale in _lastActions.Where(x => now - x.Value > TimeSpan.FromDays(1)).Select(x => x.Key).Take(2048).ToArray()) _lastActions.Remove(stale);
                if (_lastActions.Count > 8192) _lastActions.Clear();
                if (config.Smtp.Enabled && ReferenceEquals(config, Volatile.Read(ref _configuration))) await ExecuteAsync(e, "SMTP", config.ActionTimeoutSeconds, ct => SendEmailAsync(e, config.Smtp, ct));
                if (config.Webhook.Enabled && ReferenceEquals(config, Volatile.Read(ref _configuration))) await ExecuteAsync(e, "Webhook", config.ActionTimeoutSeconds, ct => _webhooks.SendAsync(e, config.Webhook, work.Context, () => ReferenceEquals(config, Volatile.Read(ref _configuration)), ct));
                if (config.Script.Enabled && ReferenceEquals(config, Volatile.Read(ref _configuration))) await ExecuteAsync(e, "Script", config.ActionTimeoutSeconds, ct => RunScriptAsync(e, config.Script, ct));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ExecuteAsync(MonitoringEvent e, string name, int timeout, Func<CancellationToken, Task> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try { await action(cts.Token); Report(new(e.Id, name, true, "Completed")); }
        catch (OperationCanceledException) { if (!_stop.IsCancellationRequested) Report(new(e.Id, name, false, "Action timed out")); }
        catch (Exception error)
        {
            var message = name == "Webhook" ? error is WebhookDeliveryException or WebhookCredentialException ? error.Message : "Webhook delivery failed; check the URL credential and connection."
                : error is HttpRequestException ? "HTTP request failed" : error.Message;
            Report(new(e.Id, name, false, message));
        }
    }
    private void Report(AlertActionResult result) { try { ActionCompleted?.Invoke(result); } catch { /* Observers cannot break delivery/shutdown. */ } }
    private static string Secret(string environmentVariable)
    {
        if (string.IsNullOrEmpty(environmentVariable)) return "";
        return Environment.GetEnvironmentVariable(environmentVariable) ?? throw new InvalidOperationException($"Secret environment variable {environmentVariable} is not configured.");
    }
    private static async Task SendEmailAsync(MonitoringEvent e, SmtpAlertConfiguration config, CancellationToken ct)
    {
        using var client = new SmtpClient(config.Host, config.Port) { EnableSsl = config.UseTls, UseDefaultCredentials = false };
        if (!string.IsNullOrEmpty(config.Username)) client.Credentials = new NetworkCredential(config.Username, Secret(config.PasswordEnvironmentVariable));
        using var message = new MailMessage(config.From, config.To) { Subject = $"Pngr — {e.Kind}", Body = $"{e.Timestamp:O}\n{e.Message}\nSession: {e.SessionId}" };
        await client.SendMailAsync(message, ct);
    }
    private static async Task RunScriptAsync(MonitoringEvent e, ScriptAlertConfiguration config, CancellationToken ct)
    {
        // A moved/deleted executable must fail this optional action, not prevent workspace startup.
        if (!File.Exists(config.Executable)) throw new IOException("Configured alert executable is unavailable.");
        if (!string.IsNullOrEmpty(config.WorkingDirectory) && !Directory.Exists(config.WorkingDirectory))
            throw new IOException("Configured alert working directory is unavailable.");
        var start = new ProcessStartInfo(config.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!string.IsNullOrEmpty(config.WorkingDirectory)) start.WorkingDirectory = config.WorkingDirectory;
        foreach (var arg in config.Arguments) start.ArgumentList.Add(arg);
        start.Environment["TRACESCOPE_EVENT_JSON"] = JsonSerializer.Serialize(e);
        start.Environment["TRACESCOPE_EVENT_KIND"] = e.Kind;
        start.Environment["TRACESCOPE_EVENT_MESSAGE"] = e.Message;
        using var process = Process.Start(start) ?? throw new IOException("Action process did not start.");
        // Drain instead of retaining arbitrary output in memory; executable output is not needed for alerts.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        try
        {
            await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException($"Script exited with code {process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); _stop.Cancel();
        try { await _worker; } finally { await _notifier.DisposeAsync(); _http.Dispose(); _stop.Dispose(); }
    }
}
