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
                var result = ParseHelperResult(await stdout, destination, process.ExitCode, await stderr);
                return result.Responded && result.LatencyMs >= options.Timeout.TotalMilliseconds
                    ? new ProbeResult(null, null, ProbeStatus.Timeout, "TCP response processed after the configured deadline.") { Transmission = ProbeTransmission.Sent }
                    : result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new ProbeResult(null, null, ProbeStatus.Timeout) { Transmission = ProbeTransmission.Sent }; }
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
    internal static ProbeResult ParseHelperResult(string output, IPAddress destination, int exitCode, string error)
    {
        var result = AdvancedProbeTransport.ParseTraceroute(output, destination, exitCode, error);
        var detail = "Bundled TCP socket probe; RTT includes socket-call overhead. " + error.Trim();
        if (result.Status == ProbeStatus.Timeout && error.Contains("TCP_SOCKET_REJECTION ", StringComparison.Ordinal))
        {
            var fields = error.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sent = fields.Length >= 3 && fields[^1] == "1";
            // Darwin ETIMEDOUT is 60. All other retained network socket errors
            // represent explicit failure with an unverified origin.
            var timeout = fields.Length >= 3 && fields[^2] == "60";
            return result with { Status = timeout ? ProbeStatus.Timeout : ProbeStatus.Unreachable,
                Detail = "TCP socket rejection; no independently validated destination response. " + error.Trim(),
                Transmission = sent ? ProbeTransmission.Sent : ProbeTransmission.NotSent,
                ResponseKind = timeout ? ProbeResponseKind.None : ProbeResponseKind.NetworkRejection };
        }
        if (error.Contains("TCP_ICMP_TIME_EXCEEDED", StringComparison.Ordinal))
            return result with { Status = ProbeStatus.TimeExceeded, Detail = detail,
                Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.IcmpTimeExceeded };
        if (error.Contains("TCP_ICMP_UNREACHABLE", StringComparison.Ordinal))
            return result with { Status = ProbeStatus.Unreachable, LatencyMs = null, Detail = detail,
                Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.NetworkRejection };
        if (error.Contains("TCP_CONNECTED", StringComparison.Ordinal))
            return result with { Detail = detail, Transmission = ProbeTransmission.Sent,
                ResponseKind = ProbeResponseKind.TcpConnected };
        if (IPAddress.IsLoopback(destination) && error.Contains("TCP_RESET_LOOPBACK", StringComparison.Ordinal))
            return result with { Detail = detail, Transmission = ProbeTransmission.Sent,
                ResponseKind = ProbeResponseKind.TcpReset };
        // Legacy helpers cannot authenticate a socket-error shortcut. Preserve
        // measured router replies, but never invent destination success.
        if (result.Status == ProbeStatus.Success)
            return new ProbeResult(null, null, ProbeStatus.Unreachable,
                "TCP helper did not provide validated destination-response evidence; rebuild the helper.")
                { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.NetworkRejection };
        return result with { Detail = detail,
            Transmission = result.Status == ProbeStatus.Error ? ProbeTransmission.NotSent : ProbeTransmission.Sent,
            ResponseKind = result.Status == ProbeStatus.TimeExceeded ? ProbeResponseKind.IcmpTimeExceeded : ProbeResponseKind.None };
    }

}
