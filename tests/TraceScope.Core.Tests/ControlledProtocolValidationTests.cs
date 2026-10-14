using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraceScope.Core;
using Xunit;
using Xunit.Abstractions;

namespace TraceScope.Core.Tests;

public sealed class ControlledProtocolValidationTests(ITestOutputHelper output)
{
    [ControlledNetworkFact]
    public async Task RealMacProtocolsAndTraceroutePreserveValidatedMeasurements()
    {
        var directory = Path.Combine(RepositoryRoot(), "artifacts", "tests", "v07-network");
        Directory.CreateDirectory(directory);
        var serializer = new JsonSerializerOptions { WriteIndented = true };
        serializer.Converters.Add(new JsonStringEnumConverter());
        var baseline = new ProbeOptions { DestinationPort = 33434, UdpEndPort = 33625,
            UdpPacketSize = 56, Timeout = TimeSpan.FromSeconds(1), Interval = TimeSpan.FromSeconds(2.5),
            MaxHops = 12, Concurrency = 3, ResolveHostnames = false, HistoryLimit = 100 };
        var cases = new[]
        {
            (Name: "icmp", Options: baseline with { Protocol = ProbeProtocol.Icmp, Concurrency = 6 }, Rounds: 4),
            (Name: "tcp443", Options: baseline with { Protocol = ProbeProtocol.Tcp, DestinationPort = 443, Concurrency = 6 }, Rounds: 4),
            (Name: "udp-fixed-c3", Options: baseline with { MaxHops = 30, Protocol = ProbeProtocol.Udp }, Rounds: 8),
            (Name: "udp-range-c3", Options: baseline with { MaxHops = 30, Protocol = ProbeProtocol.Udp, UdpPortMode = UdpPortMode.Range }, Rounds: 8),
            (Name: "udp-fixed-c6", Options: baseline with { MaxHops = 30, Protocol = ProbeProtocol.Udp, Concurrency = 6 }, Rounds: 4)
        };
        foreach (var testCase in cases)
        {
            var name = testCase.Name + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var transport = new RecordingTransport();
            await using var engine = new TraceEngine(Path.Combine(directory, name + ".db"), testCase.Options, transport);
            var snapshots = new List<RoundSnapshot>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.SnapshotUpdated += (_, snapshot) =>
            {
                snapshots.Add(snapshot);
                if (snapshot.Round >= testCase.Rounds)
                {
                    engine.Pause(snapshot.SessionId);
                    completed.TrySetResult();
                }
            };
            var session = await engine.StartTargetAsync("1.1.1.1");
            try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(70)); }
            finally { await engine.StopAsync(session.Id); }
            Assert.Equal(SessionState.Stopped, session.State);
            var last = Assert.IsType<RoundSnapshot>(session.LatestSnapshot);
            var stored = await engine.Storage.LoadSamplesAsync(session.Id);
            var destinationSamples = stored.Where(s => s.Hop == 0).ToArray();
            Assert.Equal(testCase.Rounds, destinationSamples.Length);
            var reconstructed = new HopStatistics();
            foreach (var sample in destinationSamples)
                reconstructed.Add(sample.Timestamp, new ProbeResult(sample.Address, sample.LatencyMs, sample.Status, sample.Detail)
                { ResponseKind = sample.ResponseKind, Transmission = sample.Transmission }, 100);
            var historic = reconstructed.Snapshot(0, "1.1.1.1", null, true);
            Assert.Equal(last.Destination.Sent, historic.Sent);
            Assert.Equal(last.Destination.Received, historic.Received);
            Assert.Equal(last.Destination.LossPercent, historic.LossPercent);
            Assert.Equal(last.Destination.AverageMs, historic.AverageMs);
            if (testCase.Options.Protocol == ProbeProtocol.Udp)
            {
                var audited = await engine.Storage.LoadProbeMeasurementsAsync(session.Id);
                Assert.Equal(transport.Probes.Count, audited.Count);
                Assert.Equal(testCase.Options, (await engine.Storage.LoadMeasurementOptionsAsync(session.Id))[0]);
                Assert.All(audited, p => Assert.InRange(p.Hop, 1, 255));
                Assert.All(transport.Probes.Where(p => p.Result.Status == ProbeStatus.Success), p =>
                {
                    Assert.Equal("1.1.1.1", p.Result.Address);
                    Assert.Contains(p.Result.ResponseKind, new[] { ProbeResponseKind.UdpPortUnreachable, ProbeResponseKind.UdpReply });
                    Assert.Equal(ProbeTransmission.Sent, p.Result.Transmission);
                });
                Assert.All(destinationSamples.Where(s => s.Status == ProbeStatus.Success), s =>
                    Assert.Contains(s.ResponseKind, new[] { ProbeResponseKind.UdpPortUnreachable, ProbeResponseKind.UdpReply }));
                if (testCase.Options.UdpPortMode == UdpPortMode.Fixed)
                    Assert.All(transport.Probes, p => Assert.Equal(33434, p.Port));
                else
                {
                    Assert.All(transport.Probes, p => Assert.InRange(p.Port, 33434, 33625));
                    Assert.True(transport.Probes.Select(p => p.Port).Distinct().Count() > 1);
                }
            }
            var report = new { Name = testCase.Name, Configuration = testCase.Options, Session = session.Id,
                StartedAt = session.StartedAt, FinishedAt = DateTimeOffset.UtcNow,
                Destination = last.Destination, HistoricalDestination = historic,
                Rounds = snapshots, RawProbes = transport.Probes.OrderBy(p => p.StartedAt).ToArray(),
                StoredSamples = stored };
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(report, serializer));
            await engine.Storage.ExportCsvAsync(session.Id, Path.Combine(directory, name + ".csv"));
            output.WriteLine($"{testCase.Name}: destination {last.Destination.Received}/{last.Destination.Sent}, loss {last.Destination.LossPercent:F1}%, avg {last.Destination.AverageMs:F3} ms; "
                + $"round durations {string.Join(",", snapshots.Select(s => s.RoundDuration.TotalMilliseconds.ToString("F0")))} ms; "
                + $"response kinds {string.Join(",", destinationSamples.Select(s => s.ResponseKind))}");
        }
        for (var mode = 0; mode < 2; mode++)
            for (var run = 1; run <= 3; run++)
            {
                var result = await TracerouteAsync(mode == 0);
                var name = $"traceroute-{(mode == 0 ? "fixed" : "incrementing")}-{run}";
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(result, serializer));
                output.WriteLine(name + ": " + result.Stdout.Replace('\n', ' ').Trim());
            }
    }

    private static async Task<TracerouteRun> TracerouteAsync(bool fixedPort)
    {
        var arguments = new List<string> { "-n", "-m", "12", "-w", "1", "-q", "1", "-p", "33434" };
        if (fixedPort) arguments.Add("-e");
        arguments.Add("1.1.1.1"); arguments.Add("56");
        var info = new ProcessStartInfo("/usr/sbin/traceroute")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = info };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = DateTimeOffset.UtcNow;
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new TracerouteRun(started, DateTimeOffset.UtcNow, string.Join(" ", arguments),
                process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "TraceScope.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("TraceScope repository root missing.");
    }

    private sealed record TracerouteRun(DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
        string Arguments, int ExitCode, string Stdout, string Stderr);
    private sealed record RecordedProbe(DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
        int Ttl, int Port, int PacketSize, ProbeResult Result);
    private sealed class RecordingTransport : IConfiguredProbeTransport
    {
        private readonly AdvancedProbeTransport _inner = new();
        public ConcurrentQueue<RecordedProbe> Probes { get; } = new();
        public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
            => _inner.ProbeAsync(destination, ttl, timeout, cancellationToken);
        public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await _inner.ProbeAsync(destination, ttl, options, cancellationToken);
            Probes.Enqueue(new RecordedProbe(started, DateTimeOffset.UtcNow, ttl, options.DestinationPort, options.UdpPacketSize, result));
            return result;
        }
    }
    private sealed class ControlledNetworkFactAttribute : FactAttribute
    {
        public ControlledNetworkFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("TRACESCOPE_CONTROLLED_NETWORK_TESTS") != "1")
                Skip = "Opt in to bounded macOS public-network measurement with TRACESCOPE_CONTROLLED_NETWORK_TESTS=1.";
        }
    }
}
