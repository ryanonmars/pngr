using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace TraceScope.App.Updates;

public enum UpdateCheckStatus { Skipped, Current, Available, Failed }
public sealed record UpdateRelease(Version Version, string Notes, Uri ReleasePage, Uri DownloadBaseUrl);
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release = null, string? Message = null);
public interface IAppUpdateService
{
    string? InstallationSupport { get; }
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);
    Task DownloadAsync(UpdateRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    void PrepareInstall();
}

/// <summary>Discovers stable releases independently of installation layout; only Velopack installs may self-update.</summary>
public sealed class UpdateService : IAppUpdateService
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private static readonly Uri Latest = new("https://api.github.com/repos/ryanonmars/pngr/releases/latest");
    private readonly HttpClient _http;
    private readonly string _statePath;
    private readonly Version _current;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VerifiedUpdateManager? _manager;
    private UpdateInfo? _downloaded;
    public UpdateService(string dataDirectory, Version currentVersion, HttpClient? httpClient = null, Func<DateTimeOffset>? clock = null)
    {
        _statePath = Path.Combine(dataDirectory, "update-check.json");
        _current = currentVersion;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    }
    public string? InstallationSupport
    {
        get
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
                return "Automatic installation is supported on macOS and Windows only.";
            try
            {
                var manager = new VerifiedUpdateManager("https://github.com/ryanonmars/pngr/releases/latest/download", Channel);
                if (!manager.IsInstalled) return "Install Pngr using the DMG or Windows installer before updating in the app.";
                if (OperatingSystem.IsWindows() && manager.IsPortable) return "Install Pngr using Pngr-Setup.exe to enable updates.";
                if (OperatingSystem.IsMacOS())
                {
                    var location = manager.InstallationRoot;
                    var userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") + Path.DirectorySeparatorChar;
                    if (location is null || !(location.StartsWith("/Applications/", StringComparison.Ordinal) || location.StartsWith(userApps, StringComparison.Ordinal)))
                        return "Move Pngr into Applications before updating; apps on a disk image or in Downloads cannot be updated here.";
                    // No privilege escalation: the user must own a writable installation and its parent.
                    var parent = Directory.GetParent(location)?.FullName;
                    if (parent is null || !WritableDirectory(parent) || !WritableDirectory(location))
                        return "This installation is not writable. Reinstall Pngr in your own Applications folder to enable updates.";
                }
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return "Automatic installation is unavailable for this installation. Use the release installer."; }
        }
    }
    private static string Channel => OperatingSystem.IsWindows() ? "win-x64" : "osx-universal";
    private static bool WritableDirectory(string directory)
    {
        var probe = Path.Combine(directory, ".pngr-write-test-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CheckState? state = null;
        try
        {
            state = ReadCheckState();
            var now = _clock();
            if (!manual && state is { } saved && saved.LastAttemptUtc <= now && now - saved.LastAttemptUtc < AutomaticCheckInterval)
                return new(UpdateCheckStatus.Skipped, CachedRelease(saved));
            // Preserve known availability across offline restarts without bypassing the existing cadence.
            WriteCheckState(new(now, state?.AvailableReleaseJson));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await GetAsync(Latest, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                WriteCheckState(new(now));
                return new(UpdateCheckStatus.Current);
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json, _current);
            WriteCheckState(new(now, release is null ? null : json));
            return release is null ? new(UpdateCheckStatus.Current) : new(UpdateCheckStatus.Available, release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or OperationCanceledException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return new(UpdateCheckStatus.Failed, state is null ? null : CachedRelease(state), "Could not check for updates. Check your internet connection and try again later."); }
        finally { _gate.Release(); }
    }
    public static UpdateRelease? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryStableVersion(tag, out var version) || version <= current) return null;
        var page = new Uri(root.GetProperty("html_url").GetString()!);
        RequireTrusted(page, api: false);
        if (page.AbsolutePath != $"/ryanonmars/pngr/releases/tag/{tag}") throw new FormatException("Unexpected release location.");
        var download = new Uri($"https://github.com/ryanonmars/pngr/releases/download/{tag}/");
        return new(version, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", page, download);
    }
    public static bool TryStableVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        if (value.Split('.').Length != 3 || value.Any(c => !char.IsAsciiDigit(c) && c != '.')) return false;
        return Version.TryParse(value, out version!);
    }
    public async Task DownloadAsync(UpdateRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _downloaded = null;
            if (InstallationSupport is { } reason) throw new InvalidOperationException(reason);
            RequireTrusted(release.DownloadBaseUrl, api: false);
            if (release.DownloadBaseUrl.AbsolutePath != $"/ryanonmars/pngr/releases/download/v{release.Version}/")
                throw new InvalidDataException("Unexpected update release path.");
            using var manifestResponse = await GetAsync(new Uri(release.DownloadBaseUrl, "SHA256SUMS"), cancellationToken).ConfigureAwait(false);
            manifestResponse.EnsureSuccessStatusCode();
            var manifest = ParseChecksums(await manifestResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            _manager = new VerifiedUpdateManager(new SecureReleaseSource(this, release.DownloadBaseUrl, cancellationToken), Channel);
            var info = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("No installable update package was published for this platform.");
            var asset = info.TargetFullRelease;
            if (asset.PackageId != "pngr" || asset.Version.ToString() != release.Version.ToString() ||
                Path.GetFileName(asset.FileName) != asset.FileName || !asset.FileName.EndsWith("-full.nupkg", StringComparison.Ordinal) ||
                !manifest.TryGetValue(asset.FileName, out var expected) || !expected.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package and published SHA-256 checksum do not agree.");
            await _manager.DownloadUpdatesAsync(info, percent => progress?.Report(percent), cancellationToken).ConfigureAwait(false);
            // Reverify even cached packages: Velopack may reuse an existing complete download without hashing it again.
            var packagePath = _manager.PackagePath(asset);
            try { await VerifyFileAsync(packagePath, expected, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException)
            {
                // A corrupted cached download must not poison all later retries. This is only the update cache.
                File.Delete(packagePath);
                throw;
            }
            _downloaded = info;
        }
        finally { _gate.Release(); }
    }
    public void PrepareInstall()
    {
        if (_manager is null || _downloaded is null) throw new InvalidOperationException("Download and verify an update first.");
        // The helper waits for normal application exit; the caller performs its existing monitoring/history shutdown. Never terminate here.
        _manager.WaitExitThenApplyUpdates(_downloaded.TargetFullRelease, silent: false, restart: true,
            restartArgs: ["--data-dir", Path.GetDirectoryName(Path.GetFullPath(_statePath))!]);
    }
    public static Dictionary<string, string> ParseChecksums(string manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (entry.Length != 2 || entry[0].Length != 64 || !entry[0].All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid SHA-256 manifest.");
            var name = entry[1].TrimStart('*');
            if (name != Path.GetFileName(name) || name.Contains('\\') || !result.TryAdd(name, entry[0])) throw new InvalidDataException("Invalid or duplicate asset name.");
        }
        return result;
    }
    public static async Task VerifyFileAsync(string path, string expected, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update integrity check failed. The current installation has not been changed.");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        RequireTrusted(uri, uri.Host == "api.github.com");
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("pngr-update-check/0.7");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) { response.Dispose(); throw new HttpRequestException("Insecure update redirect rejected."); }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (next.Scheme != "https" || next.UserInfo.Length != 0 || !next.IsDefaultPort ||
                    !(next.Host is "github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new HttpRequestException("Untrusted update redirect rejected.");
                uri = next;
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many update redirects.");
    }
    private static void RequireTrusted(Uri uri, bool api)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Host != (api ? "api.github.com" : "github.com"))
            throw new FormatException("Untrusted update location.");
    }
    private CheckState? ReadCheckState()
    {
        try { return JsonSerializer.Deserialize<CheckState>(File.ReadAllText(_statePath)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private UpdateRelease? CachedRelease(CheckState state)
    {
        try { return state.AvailableReleaseJson is { } json ? ParseRelease(json, _current) : null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
    private void WriteCheckState(CheckState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, _statePath, true);
    }
    private sealed record CheckState(DateTimeOffset LastAttemptUtc, string? AvailableReleaseJson = null);
    private sealed class SecureReleaseSource(UpdateService service, Uri baseUri, CancellationToken checkToken) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            using var response = await service.GetAsync(new Uri(baseUri, $"releases.{channel}.json"), checkToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return VelopackAssetFeed.FromJson(await response.Content.ReadAsStringAsync(checkToken).ConfigureAwait(false));
        }
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset asset, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            if (Path.GetFileName(asset.FileName) != asset.FileName || asset.FileName.Contains('\\')) throw new InvalidDataException("Invalid update asset name.");
            try
            {
                using var response = await service.GetAsync(new Uri(baseUri, asset.FileName), cancelToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
                await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancelToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
                    received += read;
                    if (asset.Size > 0) progress((int)Math.Min(99, received * 100 / asset.Size));
                }
                if (received != asset.Size) throw new InvalidDataException("Incomplete update download.");
            }
            catch
            {
                try { File.Delete(localFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }
    private sealed class VerifiedUpdateManager : UpdateManager
    {
        public VerifiedUpdateManager(string source, string channel) : base(source, Options(channel)) { }
        public VerifiedUpdateManager(IUpdateSource source, string channel) : base(source, Options(channel)) { }
        private static UpdateOptions Options(string channel) => new() { ExplicitChannel = channel, AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 };

        public string? InstallationRoot => Locator.RootAppDir;
        public string PackagePath(VelopackAsset asset) => Path.Combine(Locator.PackagesDir ?? throw new InvalidOperationException("No update cache."), asset.FileName);
    }
}
