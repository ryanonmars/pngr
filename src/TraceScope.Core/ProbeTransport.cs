using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

/// <summary>Uses the system's ICMP implementation; no privileged helper is installed.</summary>
public sealed partial class SystemProbeTransport : IProbeTransport
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // On macOS use the same native packet RTT for hops and the independent
        // endpoint. The managed raw-Ping timer includes additional socket/call overhead.
        if (OperatingSystem.IsMacOS())
            return await ProbeMacHopAsync(destination, ttl, timeout, cancellationToken);
        try
        {
            using var ping = new Ping();
            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(destination, timeout, Payload,
                new PingOptions(ttl, false), cancellationToken);
            stopwatch.Stop();
            return NormalizePingReply(reply.Status, reply.Address, reply.RoundtripTime, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException
                                   or InvalidOperationException or NotSupportedException)
        { return new ProbeResult(null, null, ProbeStatus.Error, ex.Message); }
    }

    internal static ProbeResult NormalizePingReply(IPStatus replyStatus, IPAddress? replyAddress,
        long roundtripTime, TimeSpan elapsed)
    {
        var status = replyStatus switch
        {
            IPStatus.Success => ProbeStatus.Success,
            IPStatus.TtlExpired => ProbeStatus.TimeExceeded,
            IPStatus.TimedOut => ProbeStatus.Timeout,
            IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
                or IPStatus.DestinationUnreachable or IPStatus.DestinationPortUnreachable
                or IPStatus.DestinationProtocolUnreachable or IPStatus.PacketTooBig or IPStatus.BadRoute
                or IPStatus.DestinationScopeMismatch => ProbeStatus.Unreachable,
            _ => ProbeStatus.Error
        };
        var address = replyAddress is null || replyAddress.Equals(IPAddress.Any)
            || replyAddress.Equals(IPAddress.IPv6Any) ? null : replyAddress.ToString();
        // .NET deliberately zeroes RTT on non-success Windows replies, including TTL expiry.
        // Wall-clock response timing retains a usable estimate; includes managed-call overhead.
        double? latency = status switch
        {
            ProbeStatus.Success => roundtripTime,
            ProbeStatus.TimeExceeded => elapsed.TotalMilliseconds,
            _ => null
        };
        return new ProbeResult(address, latency, status, status == ProbeStatus.TimeExceeded
            ? "TTL expired; estimated RTT includes .NET call overhead" : replyStatus.ToString())
            { ResponseKind = status == ProbeStatus.Success ? ProbeResponseKind.IcmpEchoReply
                : status == ProbeStatus.TimeExceeded ? ProbeResponseKind.IcmpTimeExceeded
                : status == ProbeStatus.Unreachable ? ProbeResponseKind.NetworkRejection : ProbeResponseKind.None,
                Transmission = status == ProbeStatus.Error ? ProbeTransmission.NotSent : ProbeTransmission.Unknown };
    }

    private static async Task<ProbeResult> ProbeMacHopAsync(IPAddress destination, int ttl,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // traceroute's own socket receives ICMP time-exceeded packets with measured RTT.
        // One process sends exactly one probe at the requested TTL; no shell interpolation.
        var ipv6 = destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var info = new ProcessStartInfo(ipv6 ? "/usr/sbin/traceroute6" : "/usr/sbin/traceroute")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // traceroute6 also supports -I; its default is UDP, not ICMP.
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("-q"); info.ArgumentList.Add("1");
        info.ArgumentList.Add("-f"); info.ArgumentList.Add(ttl.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-m"); info.ArgumentList.Add(ttl.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add(Math.Max(1, timeout.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture));
        info.ArgumentList.Add(destination.ToString());
        info.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(1.5));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            var result = ParseMacTraceroute(output, destination.ToString(), process.ExitCode, error);
            return result.Responded && result.LatencyMs >= timeout.TotalMilliseconds
                ? new ProbeResult(null, null, ProbeStatus.Timeout, "ICMP reply RTT exceeded the configured deadline.") { Transmission = ProbeTransmission.Sent }
                : result;
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
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException) { }
        }
    }

    public static ProbeResult ParseMacTraceroute(string output, string destination,
        int exitCode = 0, string? error = null)
    {
        var match = HopLine().Match(output);
        if (match.Success && IPAddress.TryParse(match.Groups[1].Value, out var address)
            && double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rtt))
        {
            // A bare ! is a low return TTL, not an ICMP unreachable rejection.
            var status = match.Groups[3].Value.Length > 1 ? ProbeStatus.Unreachable
                : address.ToString() == destination ? ProbeStatus.Success : ProbeStatus.TimeExceeded;
            return new ProbeResult(address.ToString(), status == ProbeStatus.Unreachable ? null : rtt, status,
                "macOS native ICMP packet RTT; utility startup is excluded. Utility-default packet size.")
            { ResponseKind = status == ProbeStatus.Success ? ProbeResponseKind.IcmpEchoReply
                : status == ProbeStatus.TimeExceeded ? ProbeResponseKind.IcmpTimeExceeded : ProbeResponseKind.NetworkRejection,
                Transmission = ProbeTransmission.Sent };
        }
        return exitCode == 0 ? new ProbeResult(null, null, ProbeStatus.Timeout)
            : new ProbeResult(null, null, ProbeStatus.Error, error?.Trim());
    }

    [GeneratedRegex(@"^\s*\d+\s+(\S+)\s+([0-9.]+)\s*ms(?:\s+(!\S+))?", RegexOptions.Multiline)]
    private static partial Regex HopLine();
}
