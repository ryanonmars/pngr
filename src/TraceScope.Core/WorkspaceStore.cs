using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TraceScope.Core;

public sealed record WorkspaceTarget(string Target, ProbeOptions Options, string Group = "Default", bool Paused = false,
    int TcpPort = 443, int UdpPort = 33434, string? ProfileName = null,
    Guid Id = default, bool Closed = false, bool Stopped = false, string? DisplayName = null,
    Guid? SessionId = null, string? ResolvedAddress = null);
public sealed record MonitoringProfile(string Name, ProbeOptions Options);
/// <summary>Desktop layout only: dimensions in DIPs, position in physical pixels.</summary>
public sealed record MainWindowLayout
{
    public double Width { get; init; } = 1200;
    public double Height { get; init; } = 800;
    public int? X { get; init; }
    public int? Y { get; init; }
    public double Scaling { get; init; } = 1;
    public bool Maximized { get; init; }
}

public sealed record WorkspaceConfiguration
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Default workspace";
    public bool RestoreOnStartup { get; init; } = true;
    public int RetentionDays { get; init; } = 30;
    public int AggregationSeconds { get; init; } = 5;
    public int WebPort { get; init; } = 8787;
    public bool WebEnabled { get; init; }
    public bool WebLanEnabled { get; init; }
    public string? WebLanAddress { get; init; }
    // Missing mode in older settings means manual when an address was already selected.
    public bool? WebLanAutomatic { get; init; }
    [JsonIgnore] public bool WebUsesAutomaticInterface => WebLanAutomatic ?? string.IsNullOrEmpty(WebLanAddress);
    public bool WebHttpAcknowledged { get; init; }
    public string? WebAccessKeyVerifier { get; init; }
    public bool DesktopNotifications { get; init; }
    public ProbeOptions DefaultProbeOptions { get; init; } = new();
    public bool UseDefaultProbeOptionsForNewTargets { get; init; }
    public ProbeOptions? LastUsedProbeOptions { get; init; }
    public int LastTcpPort { get; init; } = 443;
    public int LastUdpPort { get; init; } = 33434;
    public string? LastUsedProfile { get; init; }
    public DisplayConfiguration Display { get; init; } = new();
    public MainWindowLayout? WindowLayout { get; init; }
    public HistoryLayoutPreferences HistoryLayout { get; init; } = new();
    public IReadOnlyList<WorkspaceTarget> Targets { get; init; } = [];
    // Optional additive catalog; legacy workspaces derive groups from their targets.
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<MonitoringProfile> Profiles { get; init; } = [];
    public IReadOnlyList<AlertRule> Alerts { get; init; } = [];
    public IReadOnlyList<SummaryDefinition> SummaryViews { get; init; } = [];
    public AlertActionConfiguration Actions { get; init; } = new();

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Unsupported Pngr configuration version.");
        if (RetentionDays is < 1 or > 3650) throw new InvalidDataException("Retention must be between 1 and 3650 days.");
        if (AggregationSeconds is < 1 or > 3600) throw new InvalidDataException("Aggregation must be between 1 and 3600 seconds.");
        if (WebPort is < 1024 or > 65535) throw new InvalidDataException("Web port must be between 1024 and 65535.");
        if (WebAccessKeyVerifier is not null && !WebAccessKey.IsValidVerifier(WebAccessKeyVerifier))
            throw new InvalidDataException("Invalid web access key verifier.");
        var validWebAddress = System.Net.IPAddress.TryParse(WebLanAddress, out var webAddress) && WebServerNetwork.IsPrivateAddress(webAddress);
        if (WebLanEnabled && (!WebHttpAcknowledged || !WebAccessKey.IsValidVerifier(WebAccessKeyVerifier)
            || !validWebAddress && !(WebUsesAutomaticInterface && WebLanAddress is null)))
            throw new InvalidDataException("LAN access requires a private interface address, a generated key and acknowledgement of unencrypted HTTP.");
        if (DefaultProbeOptions is null || Display is null) throw new InvalidDataException("Monitoring and display settings are required.");
        DefaultProbeOptions.Validate();
        LastUsedProbeOptions?.Validate();
        if (LastTcpPort is < 1 or > 65535 || LastUdpPort is < 1 or > 65535)
            throw new InvalidDataException("Invalid remembered destination port.");
        Display.Validate();
        if (HistoryLayout is null) throw new InvalidDataException("History layout is required.");
        HistoryLayout.Validate();
        if (Groups is null || Groups.Any(g => string.IsNullOrWhiteSpace(g) || g.Length > 80 || g.Any(char.IsControl))
            || Groups.Distinct(StringComparer.Ordinal).Count() != Groups.Count)
            throw new InvalidDataException("Invalid group catalog.");
        if (Targets is null || Profiles is null || Alerts is null || SummaryViews is null || Actions is null)
            throw new InvalidDataException("Targets, profiles and alerts must be lists.");
        if (Targets.Count > 500 || Profiles.Count > 100 || Alerts.Count > 200)
            throw new InvalidDataException("Configuration exceeds the supported target/profile/alert count.");
        if (Targets.Where(t => t is not null && t.Id != Guid.Empty).Select(t => t.Id).Distinct().Count()
            != Targets.Count(t => t is not null && t.Id != Guid.Empty))
            throw new InvalidDataException("Saved target identifiers must be unique.");
        foreach (var target in Targets)
        {
            if (target is null || target.Options is null || target.Group is null || string.IsNullOrWhiteSpace(target.Target) || target.Target.Length > 253 || target.Group.Length > 80)
                throw new InvalidDataException("Invalid target or group.");
            if (target.DisplayName?.Length > 80 || target.DisplayName?.Any(char.IsControl) == true)
                throw new InvalidDataException("Invalid target display name.");
            target.Options.Validate();
            if (target.TcpPort is < 1 or > 65535 || target.UdpPort is < 1 or > 65535)
                throw new InvalidDataException("Invalid target destination port.");
        }
        foreach (var profile in Profiles)
        {
            if (profile is null || profile.Options is null || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
                throw new InvalidDataException("Invalid profile.");
            profile.Options.Validate();
        }
        foreach (var alert in Alerts)
            if (alert is null || !Enum.IsDefined(alert.Condition) || string.IsNullOrWhiteSpace(alert.Id) || string.IsNullOrWhiteSpace(alert.Name)
                || !double.IsFinite(alert.Threshold) || alert.Threshold < 0 || alert.EvaluationPeriod < TimeSpan.Zero
                || alert.Cooldown < TimeSpan.Zero || alert.RecoveryPeriod < TimeSpan.Zero || alert.Target?.Length > 253 || alert.Group?.Length > 80)
                throw new InvalidDataException("Invalid alert rule.");
        if (SummaryViews.Count > 100 || SummaryViews.Any(s => s is null))
            throw new InvalidDataException("Invalid summary views.");
        foreach (var summary in SummaryViews) summary.Validate();
        if (SummaryViews.Select(s => s.Id).Distinct().Count() != SummaryViews.Count)
            throw new InvalidDataException("Summary identifiers must be unique.");
        Actions.Validate();
    }
}

public sealed class WorkspaceStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly IWebhookSecretStore _secrets;
    public string? CredentialWarning { get; private set; }
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public WorkspaceStore(string path, IWebhookSecretStore? secretStore = null)
    {
        _secrets = secretStore ?? WebhookSecretStore.Default;
        Path = System.IO.Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(Path, _ => new SemaphoreSlim(1, 1));
    }
    public string Path { get; }

    public async Task<WorkspaceConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Windows cannot reliably replace an open destination during consecutive
        // saves. Use the existing per-path save gate until this reader closes.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(Path)) return null;
            WorkspaceConfiguration configuration;
            await using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                configuration = await ImportAsync(stream, cancellationToken);
            CredentialWarning = null;
            if (configuration.Actions.Webhook.Url.Length == 0) return configuration;
            try
            {
                var secured = ProtectWebhook(configuration);
                await WriteAsync(secured, cancellationToken);
                return secured;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Credential access must not prevent targets/history from restoring.
                // Preserve the original file so migration can be retried after access is restored.
                CredentialWarning = "Webhook credential migration failed; webhook delivery is disabled. " +
                    (error is WebhookCredentialException ? error.Message : "Check secure-store access and save the webhook again.");
                return configuration with { Actions = configuration.Actions with
                { Webhook = configuration.Actions.Webhook with { Enabled = false } } };
            }
        }
        finally { _gate.Release(); }
    }

    public static async Task<WorkspaceConfiguration> ImportAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        const int limit = 4 * 1024 * 1024;
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (buffered.Length + count > limit) throw new InvalidDataException("Configuration exceeds 4 MiB.");
            await buffered.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        buffered.Position = 0;
        var configuration = await JsonSerializer.DeserializeAsync<WorkspaceConfiguration>(buffered, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    public async Task<WorkspaceConfiguration> SaveAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Native secure-store APIs are synchronous and may wait for macOS.
            // Never run creation/readback on the caller's desktop UI thread. Keep
            // the save gate until this worker and the durable write/rollback finish;
            // cancellation must not abandon a worker owning a new credential.
            var secured = await Task.Run(() => ProtectWebhook(configuration), cancellationToken).ConfigureAwait(false);
            try { await WriteAsync(secured, cancellationToken).ConfigureAwait(false); }
            catch
            {
                // Remove only newly created draft credentials. The previous saved settings remain usable.
                await Task.Run(() =>
                {
                    foreach (var id in new[] { secured.Actions.Webhook.UrlSecretId, secured.Actions.Webhook.TelegramBotTokenSecretId }
                        .Where(id => id.Length > 0 && id != configuration.Actions.Webhook.UrlSecretId && id != configuration.Actions.Webhook.TelegramBotTokenSecretId))
                        try { _secrets.Delete(id); } catch { }
                }).ConfigureAwait(false);
                throw;
            }
            // Retain superseded credentials. Accessing an old ad-hoc-authorized
            // Keychain item just to clean it up can prompt again; replacement must
            // never depend on its availability or silently change its permissions.
            CredentialWarning = null;
            return secured;
        }
        finally { _gate.Release(); }
    }

    private WorkspaceConfiguration ProtectWebhook(WorkspaceConfiguration configuration)
    {
        var webhook = configuration.Actions.Webhook;
        try
        {
            // Existing references are settings, not a request to unlock credentials.
            // Resolve them only for explicit pairing or delivery; saving an unrelated
            // rule or preference must not decrypt or rewrite a stored credential.
            var secured = webhook;
            if (webhook.TelegramBotToken.Length > 0)
            {
                TelegramDelivery.ValidateBotToken(webhook.TelegramBotToken);
                secured = secured with { TelegramBotToken = "", TelegramBotTokenSecretId = StoreVerifiedTelegramToken(webhook.TelegramBotToken) };
            }
            try
            {
                if (webhook.Url.Length > 0)
                    secured = secured with { Url = "", UrlSecretId = _secrets.Store(webhook.Url) };
                return configuration with { Actions = configuration.Actions with { Webhook = secured } };
            }
            catch
            {
                if (secured.TelegramBotTokenSecretId != webhook.TelegramBotTokenSecretId)
                    try { _secrets.Delete(secured.TelegramBotTokenSecretId); } catch { }
                throw;
            }
        }
        catch (WebhookCredentialException) { throw; }
        catch { throw new InvalidOperationException("Webhook credential could not be stored securely. Check access to the secure credential store."); }
    }

    private string StoreVerifiedTelegramToken(string token)
    {
        // Own the new identifier before writing, so even a partially failed create
        // can be rolled back without touching the saved credential reference.
        var id = Guid.NewGuid().ToString("N");
        try
        {
            if (_secrets.Store(token, id) != id)
                throw new InvalidOperationException("Secure credential storage returned an unexpected reference.");
            var actual = Encoding.UTF8.GetBytes(_secrets.Retrieve(id));
            var expected = Encoding.UTF8.GetBytes(token);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new InvalidOperationException("Secure credential verification failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
            return id;
        }
        catch
        {
            // This identifier belongs exclusively to this attempt, never to an
            // existing Discord or Telegram credential.
            try { _secrets.Delete(id); } catch { }
            throw;
        }
    }

    private async Task WriteAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken)
    {
        var temporary = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task ExportAsync(Stream stream, WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        // Sharing a workspace must not share authentication state or enable remote access on import.
        var exported = configuration with { WebLanEnabled = false, WebLanAddress = null, WebLanAutomatic = null,
            WebHttpAcknowledged = false, WebAccessKeyVerifier = null, Actions = configuration.Actions with
        { Webhook = configuration.Actions.Webhook with { Enabled = false, Url = "", UrlSecretId = "", TelegramBotToken = "", TelegramBotTokenSecretId = "" } } };
        return JsonSerializer.SerializeAsync(stream, exported, JsonOptions, cancellationToken);
    }
}
