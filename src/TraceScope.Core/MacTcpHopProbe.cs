using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace TraceScope.Core;

/// <summary>Runs one bundled, unprivileged native TCP probe; no installation or service.</summary>
internal static class MacTcpHopProbe
{
    private static readonly SemaphoreSlim Slots = new(16);

    internal static string? FindHelper()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tracescope-tcp-hop");
        if (File.Exists(bundled)) return bundled;
        // Source checkouts keep generated binaries in the existing artifacts folder.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "TraceScope.slnx"))) continue;
            var developer = Path.Combine(directory.FullName, "artifacts", "native", "tracescope-tcp-hop");
            return File.Exists(developer) ? developer : null;
        }
        return null;
    }

    internal static async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl,
        ProbeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var helper = FindHelper();
        if (helper is null) return new ProbeResult(null, null, ProbeStatus.Error,
            "The bundled TCP hop probe is missing; rebuild the macOS application package.");
        if (!string.IsNullOrWhiteSpace(options.InterfaceId) && string.IsNullOrWhiteSpace(options.SourceAddress))
            return new ProbeResult(null, null, ProbeStatus.Error, "Select a local source address to bind this interface.");
        if (!string.IsNullOrWhiteSpace(options.SourceAddress)
            && (!IPAddress.TryParse(options.SourceAddress, out var source) || source.AddressFamily != destination.AddressFamily))
            return new ProbeResult(null, null, ProbeStatus.Error, "Source address must match the destination address family.");
        await Slots.WaitAsync(cancellationToken);
        try
        {
            var info = new ProcessStartInfo(helper)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { destination.ToString(), ttl.ToString(CultureInfo.InvariantCulture),
                options.DestinationPort.ToString(CultureInfo.InvariantCulture),
                Math.Clamp((int)Math.Ceiling(options.Timeout.TotalMilliseconds), 100, 30_000).ToString(CultureInfo.InvariantCulture),
                options.SourceAddress ?? "-", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Timeout + TimeSpan.FromSeconds(1.5));
            try
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                return AdvancedProbeTransport.ParseTraceroute(await stdout, destination, process.ExitCode, await stderr)
                    with { Detail = process.ExitCode == 0 ? "Bundled unprivileged TCP socket probe; measured response includes socket-call overhead."
                        : (await stderr).Trim() };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new ProbeResult(null, null, ProbeStatus.Timeout); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { return new ProbeResult(null, null, ProbeStatus.Error, ex.Message); }
            finally
            {
                try
                {
                    if (process.Id != 0 && !process.HasExited)
                    { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                }
                catch (InvalidOperationException) { }
            }
        }
        finally { Slots.Release(); }
    }
}
