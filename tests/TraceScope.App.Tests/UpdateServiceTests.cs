using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using TraceScope.App.Updates;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class UpdateServiceTests
{
    private const string Release = """
        {"draft":false,"prerelease":false,"tag_name":"v0.7.2","html_url":"https://github.com/ryanonmars/pngr/releases/tag/v0.7.2","body":"Fixes and improvements."}
        """;
    [Fact]
    public void StableReleaseDiscoveryPreservesNotesAndPinsDownloadTag()
    {
        var release = UpdateService.ParseRelease(Release, new Version(0, 7, 1))!;
        Assert.Equal(new Version(0, 7, 2), release.Version);
        Assert.Equal("Fixes and improvements.", release.Notes);
        Assert.Equal("https://github.com/ryanonmars/pngr/releases/download/v0.7.2/", release.DownloadBaseUrl.AbsoluteUri);
        Assert.Null(UpdateService.ParseRelease(Release, new Version(0, 7, 2)));
        Assert.Null(UpdateService.ParseRelease(Release, new Version(0, 8, 0)));
    }
    [Theory]
    [InlineData("0.7.2-beta.1")]
    [InlineData("v0.7")]
    [InlineData("v0.7.2.1")]
    [InlineData("v0.7.2+metadata")]
    public void RejectsNonStableOrMalformedVersions(string tag) => Assert.False(UpdateService.TryStableVersion(tag, out _));
    [Fact]
    public void PrereleasesDraftsAndUntrustedReleaseLocationsAreRejected()
    {
        Assert.Null(UpdateService.ParseRelease(Release.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(0, 7, 1)));
        Assert.Null(UpdateService.ParseRelease(Release.Replace("\"draft\":false", "\"draft\":true"), new Version(0, 7, 1)));
        Assert.Throws<FormatException>(() => UpdateService.ParseRelease(Release.Replace("https://github.com", "http://github.com"), new Version(0, 7, 1)));
        Assert.Throws<FormatException>(() => UpdateService.ParseRelease(Release.Replace("ryanonmars/pngr", "other/pngr"), new Version(0, 7, 1)));
    }
    [Fact]
    public async Task ThrottlePersistsAcrossInstancesAndManualCheckBypassesIt()
    {
        using var data = new TempData();
        var handler = new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release) });
        using var client = new HttpClient(handler);
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        var service = new UpdateService(data.Path, new Version(0, 7, 1), client, () => now);
        Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync(false)).Status);
        var reopened = new UpdateService(data.Path, new Version(0, 7, 1), client, () => now);
        Assert.Equal(UpdateCheckStatus.Skipped, (await reopened.CheckAsync(false)).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await reopened.CheckAsync(true)).Status);
        now += TimeSpan.FromHours(24);
        Assert.Equal(UpdateCheckStatus.Available, (await reopened.CheckAsync(false)).Status);
        Assert.Equal(3, handler.Calls);
        Assert.True(File.Exists(System.IO.Path.Combine(data.Path, "update-check.json")));
        Assert.Single(Directory.GetFiles(data.Path));
    }
    [Fact]
    public async Task OfflineFailureIsGracefulAndThrottledButManualRetryWorks()
    {
        using var data = new TempData();
        var handler = new FakeHttp(_ => throw new HttpRequestException("offline"));
        using var client = new HttpClient(handler);
        var service = new UpdateService(data.Path, new Version(0, 7, 1), client);
        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync(false)).Status);
        Assert.Equal(UpdateCheckStatus.Skipped, (await service.CheckAsync(false)).Status);
        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync(true)).Status);
        Assert.Equal(2, handler.Calls);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("{\"draft\":\"bad\"}")]
    public async Task InvalidServerResponsesDoNotCrashTheApplication(string body)
    {
        using var data = new TempData();
        using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateService(data.Path, new Version(0, 7, 1), client).CheckAsync(true)).Status);
    }
    [Fact]
    public async Task ConcurrentAutomaticChecksMakeOnlyOneRequest()
    {
        using var data = new TempData();
        var handler = new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release) });
        using var client = new HttpClient(handler);
        var service = new UpdateService(data.Path, new Version(0, 7, 1), client);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.CheckAsync(false)));
        Assert.Single(results, r => r.Status == UpdateCheckStatus.Available);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CorruptStateAndBackwardClockDoNotPermanentlyDisableChecks()
    {
        using var data = new TempData();
        var state = System.IO.Path.Combine(data.Path, "update-check.json");
        File.WriteAllText(state, "corrupt");
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        var handler = new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(Release) });
        using var client = new HttpClient(handler);
        var service = new UpdateService(data.Path, new Version(0, 7, 1), client, () => now);
        Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync(false)).Status);
        now -= TimeSpan.FromHours(1);
        Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync(false)).Status);
    }
    [Fact]
    public async Task CachedAvailabilitySurvivesRestartAndOfflineAttemptsButNotAnInstalledUpdate()
    {
        using var data = new TempData();
        var offline = false;
        var handler = new FakeHttp(_ => offline ? throw new HttpRequestException("offline")
            : new(HttpStatusCode.OK) { Content = new StringContent(Release) });
        using var client = new HttpClient(handler);
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        UpdateService Service(Version version) => new(data.Path, version, client, () => now);
        Assert.Equal(UpdateCheckStatus.Available, (await Service(new(0, 7, 1)).CheckAsync(false)).Status);
        var restarted = await Service(new(0, 7, 1)).CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Skipped, restarted.Status);
        Assert.Equal(new Version(0, 7, 2), restarted.Release!.Version);
        Assert.Equal(1, handler.Calls);
        now += UpdateService.AutomaticCheckInterval; offline = true;
        var offlineRestart = await Service(new(0, 7, 1)).CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Failed, offlineRestart.Status);
        Assert.NotNull(offlineRestart.Release);
        Assert.NotNull((await Service(new(0, 7, 1)).CheckAsync(false)).Release);
        Assert.Null((await Service(new(0, 7, 2)).CheckAsync(false)).Release);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task ConfirmedCurrentReleaseClearsCachedAvailability(HttpStatusCode status)
    {
        using var data = new TempData();
        var current = false;
        using var client = new HttpClient(new FakeHttp(_ => current
            ? new(status) { Content = new StringContent(Release.Replace("v0.7.2", "v0.7.1")) }
            : new(HttpStatusCode.OK) { Content = new StringContent(Release) }));
        var service = new UpdateService(data.Path, new(0, 7, 1), client);
        await service.CheckAsync(false); current = true;
        Assert.Equal(UpdateCheckStatus.Current, (await service.CheckAsync(true)).Status);
        Assert.Null((await new UpdateService(data.Path, new(0, 7, 1), client).CheckAsync(false)).Release);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableFeedsRemainThrottledAndPreserveKnownAvailability(HttpStatusCode status)
    {
        using var data = new TempData(); var unavailable = false;
        var handler = new FakeHttp(_ => unavailable ? new(status) : new(HttpStatusCode.OK) { Content = new StringContent(Release) });
        using var client = new HttpClient(handler);
        var service = new UpdateService(data.Path, new(0, 7, 1), client);
        await service.CheckAsync(false); unavailable = true;
        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync(true)).Status);
        var cached = await service.CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Skipped, cached.Status); Assert.NotNull(cached.Release);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task IntegrityCheckRejectsCorruptionWithoutChangingInstallation()
    {
        using var data = new TempData();
        var installation = System.IO.Path.Combine(data.Path, "installed-app");
        File.WriteAllText(installation, "working installation");
        var package = System.IO.Path.Combine(data.Path, "update.nupkg");
        File.WriteAllText(package, "package");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("package")));
        await UpdateService.VerifyFileAsync(package, expected.ToLowerInvariant());
        File.AppendAllText(package, "corruption");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.VerifyFileAsync(package, expected));
        Assert.Equal("working installation", File.ReadAllText(installation));
    }
    [Fact]
    public void ChecksumManifestRejectsDuplicateTraversalAndMalformedHashes()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateService.ParseChecksums(hash + "  pngr.0.7.2-full.nupkg\n")["pngr.0.7.2-full.nupkg"]);
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksums(hash + "  ../app\n"));
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksums(hash + "  a\\b\n"));
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksums(hash + "  app\n" + hash + "  app\n"));
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksums("bad  app\n"));
    }
    [Fact]
    public void NoInstallCanBeScheduledBeforeVerifiedDownload()
    {
        using var data = new TempData();
        Assert.Throws<InvalidOperationException>(() => new UpdateService(data.Path, new Version(0, 7, 1)).PrepareInstall());
    }
    [Fact]
    public async Task HttpDowngradeRedirectIsRejectedAndDoesNotReachTheDestination()
    {
        using var data = new TempData();
        var handler = new FakeHttp(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://github.com/ryanonmars/pngr");
            return response;
        });
        using var client = new HttpClient(handler);
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateService(data.Path, new Version(0, 7, 1), client).CheckAsync(true)).Status);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CheckCancellationIsNotReportedAsASuccessfulCheck()
    {
        using var data = new TempData();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateService(data.Path, new Version(0, 7, 1)).CheckAsync(true, cancellation.Token));
        Assert.Empty(Directory.GetFiles(data.Path));
    }
    [Fact]
    public async Task InterruptedPackageDownloadRemovesPartialFileAndLeavesInstallationUntouched()
    {
        using var data = new TempData();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new InterruptedStream(cancellation)) }));
        var service = new UpdateService(data.Path, new Version(0, 7, 1), client);
        var source = CreateDownloadSource(service);
        var partial = Path.Combine(data.Path, "update.nupkg.partial");
        var installed = Path.Combine(data.Path, "existing-application");
        await File.WriteAllTextAsync(installed, "unchanged");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.DownloadReleaseEntry(new NullVelopackLogger(),
            new VelopackAsset { FileName = "pngr-0.7.2-win-x64-full.nupkg", Size = 100 }, partial, _ => { }, cancellation.Token));
        Assert.False(File.Exists(partial));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(installed));
        Assert.Throws<InvalidOperationException>(() => service.PrepareInstall());
    }

    [Fact]
    public async Task TruncatedPackageDownloadIsRejectedAndCanBeRetried()
    {
        using var data = new TempData();
        using var client = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) }));
        var source = CreateDownloadSource(new UpdateService(data.Path, new Version(0, 7, 1), client));
        var partial = Path.Combine(data.Path, "update.nupkg.partial");
        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadReleaseEntry(new NullVelopackLogger(),
            new VelopackAsset { FileName = "pngr-0.7.2-win-x64-full.nupkg", Size = 5 }, partial, _ => { }));
        Assert.False(File.Exists(partial));
        await source.DownloadReleaseEntry(new NullVelopackLogger(),
            new VelopackAsset { FileName = "pngr-0.7.2-win-x64-full.nupkg", Size = 3 }, partial, _ => { });
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(partial));
    }

    private static IUpdateSource CreateDownloadSource(UpdateService service)
        => (IUpdateSource)typeof(UpdateService).GetNestedType("SecureReleaseSource", BindingFlags.NonPublic)!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
            .Invoke([service, new Uri("https://github.com/ryanonmars/pngr/releases/download/v0.7.2/"), CancellationToken.None]);

    private sealed class InterruptedStream(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3, 4])
    {
        private bool _started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_started) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            _started = true;
            return base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], token);
        }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(respond(request));
        }
    }
    private sealed class TempData : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pngr-update-tests-" + Guid.NewGuid().ToString("N"));
        public TempData() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
