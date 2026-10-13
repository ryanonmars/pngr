using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

[CollectionDefinition("Embedded web lifecycle", DisableParallelization = true)]
public sealed class WebLifecycleCollection;

[Collection("Embedded web lifecycle")]
public sealed class WebLifecycleTests
{
    [Fact]
    public async Task DisposeReleasesListenerAndAnotherServerCanReusePort()
    {
        await using var fixture = new Fixture();
        var first = new LocalWebServer(fixture.Engine);
        Assert.Null(first.Url);
        await first.StartAsync(0);
        var url = first.Url!;
        var port = new Uri(url).Port;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Assert.Equal("[]", await client.GetStringAsync(url + "/api/targets"));
        await first.StartAsync(0); // Repeated enable calls keep the existing single listener.
        Assert.Equal(url, first.Url);
        await Task.WhenAll(first.DisposeAsync().AsTask(), first.DisposeAsync().AsTask());
        Assert.Null(first.Url);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api/targets"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.StartAsync(port));
        await using var second = new LocalWebServer(fixture.Engine);
        await second.StartAsync(port);
        Assert.Equal(url, second.Url);
        Assert.Equal("[]", await client.GetStringAsync(url + "/api/targets"));
        await second.DisposeAsync();
        var replacement = new TcpListener(IPAddress.Loopback, port);
        try { replacement.Start(); }
        finally { replacement.Stop(); }
    }

    [Fact]
    public async Task EmbeddedHostIgnoresExternalEndpointAndLifetimeEnvironment()
    {
        await using var fixture = new Fixture();
        var overrides = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:65431",
            ["Kestrel__Endpoints__Injected__Url"] = "http://0.0.0.0:65432",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = "MissingExternalHostingAssembly"
        };
        var previous = overrides.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in overrides) Environment.SetEnvironmentVariable(key, value);
            await using var server = new LocalWebServer(fixture.Engine);
            await server.StartAsync(0);
            var app = Assert.IsType<WebApplication>(typeof(LocalWebServer).GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server));
            Assert.Equal(Environments.Production, app.Environment.EnvironmentName);
            Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                app.Environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar));
            Assert.DoesNotContain("ConsoleLifetime", app.Services.GetRequiredService<IHostLifetime>().GetType().Name);
            Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            Assert.Equal("127.0.0.1", new Uri(server.Url!).Host);
            Assert.NotEqual(65431, new Uri(server.Url!).Port);
            Assert.NotEqual(65432, new Uri(server.Url!).Port);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Assert.Equal("[]", await client.GetStringAsync(server.Url + "/api/targets"));
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

    [Fact]
    public async Task CancelledOrFailedStartLeavesNoServerAndCanBeRetried()
    {
        await using var fixture = new Fixture();
        await using var server = new LocalWebServer(fixture.Engine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(0, cancellation.Token));
        Assert.Null(server.Url);
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        try { await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(port)); }
        finally { occupied.Stop(); }
        Assert.Null(server.Url);
        await server.StartAsync(port);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Assert.Equal("[]", await client.GetStringAsync(server.Url + "/api/targets"));
    }

    [Fact]
    public async Task MacListenerBelongsToCallingDesktopProcess()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await using var fixture = new Fixture();
        await using var server = new LocalWebServer(fixture.Engine);
        await server.StartAsync(0);
        using var process = new Process { StartInfo = new("/usr/sbin/lsof")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in new[] { "-nP", $"-iTCP:{new Uri(server.Url!).Port}", "-sTCP:LISTEN", "-F", "p" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        var pids = (await output).Split('\n').Where(line => line.StartsWith('p')).Select(line => int.Parse(line[1..])).ToArray();
        Assert.Equal(Environment.ProcessId, Assert.Single(pids));
        Assert.Equal("", await errors);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        public TraceEngine Engine { get; }
        public Fixture()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "TraceScope.slnx"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Cannot locate project test artifacts.");
            _directory = Path.Combine(root.FullName, "artifacts", "tests", "web-lifecycle", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Engine = new TraceEngine(Path.Combine(_directory, "history.db"));
        }
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }
}
