using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

/// <summary>Independent protocol probes using public sockets and the macOS traceroute utilities.</summary>
public sealed partial class AdvancedProbeTransport : IConfiguredProbeTransport
{
    private readonly SystemProbeTransport _icmp = new();

    public Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, TimeSpan timeout,
        CancellationToken cancellationToken) => _icmp.ProbeAsync(destination, ttl, timeout, cancellationToken);

    public async Task<ProbeResult> ProbeAsync(IPAddress destination, int ttl, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Protocol == ProbeProtocol.Icmp && string.IsNullOrWhiteSpace(options.SourceAddress)
            && string.IsNullOrWhiteSpace(options.InterfaceId))
            return await _icmp.ProbeAsync(destination, ttl, options.Timeout, cancellationToken);

        if (options.Protocol == ProbeProtocol.Udp)
            return await SocketHopProbe.ProbeAsync(destination, ttl, options, cancellationToken);

        if (ttl == 255 && options.Protocol != ProbeProtocol.Icmp)
            return await ProbeEndpointAsync(destination, options, cancellationToken);

        // Darwin permits ICMP datagram reception without BPF access or elevation.
        // Ordinary TCP/UDP sockets send the diagnostic traffic; quoted ICMP
        // protocol, target and port tuples associate a router reply with its probe.
        if (OperatingSystem.IsMacOS() && options.Protocol == ProbeProtocol.Tcp)
            return await MacTcpHopProbe.ProbeAsync(destination, ttl, options, cancellationToken);

        if (OperatingSystem.IsWindows() && options.Protocol == ProbeProtocol.Tcp)
            return await WindowsTcpHopProbe.ProbeAsync(destination, ttl, options, cancellationToken);

        if (!OperatingSystem.IsMacOS())
            return new ProbeResult(null, null, ProbeStatus.Error,
                options.Protocol == ProbeProtocol.Icmp
                    ? "Source-bound ICMP is unavailable on this platform; select automatic interface."
                    : $"{options.Protocol} intermediate-hop capture is unavailable without a privileged packet helper on this platform; endpoint probes remain available.");

        return await ProbeMacHopAsync(destination, ttl, options, cancellationToken);
    }

    private static async Task<ProbeResult> ProbeEndpointAsync(IPAddress destination, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        Socket? socket = null;
        var stopwatch = new Stopwatch();
        try
        {
            socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (!string.IsNullOrWhiteSpace(options.SourceAddress))
            {
                if (!IPAddress.TryParse(options.SourceAddress, out var source)
                    || source.AddressFamily != destination.AddressFamily)
                    return new ProbeResult(null, null, ProbeStatus.Error,
                        "Source address must be a local numeric address matching the destination address family.") { Transmission = ProbeTransmission.NotSent };
                socket.Bind(new IPEndPoint(source, 0));
            }
            else if (!string.IsNullOrWhiteSpace(options.InterfaceId))
                return new ProbeResult(null, null, ProbeStatus.Error,
                    "Select a source address for this interface to bind endpoint probes.") { Transmission = ProbeTransmission.NotSent };

            stopwatch.Start();
            await socket.ConnectAsync(new IPEndPoint(destination, options.DestinationPort), deadline.Token);
            ProbeDeadline.Verify(stopwatch.Elapsed, options.Timeout, deadline.Token);
            return new ProbeResult(destination.ToString(), stopwatch.Elapsed.TotalMilliseconds,
                    ProbeStatus.Success, $"TCP connect port {options.DestinationPort}; RTT includes connect-call overhead.")
                    { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.TcpConnected };
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && IPAddress.IsLoopback(destination))
        {
            return new ProbeResult(destination.ToString(), stopwatch.Elapsed.TotalMilliseconds,
                ProbeStatus.Success, $"TCP RST: port {options.DestinationPort} closed; host responded.")
                { Transmission = ProbeTransmission.Sent, ResponseKind = ProbeResponseKind.TcpReset };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new ProbeResult(null, null, ProbeStatus.Timeout, $"No {options.Protocol} endpoint response within timeout.")
            { Transmission = ProbeTransmission.Sent }; }
        catch (OperationCanceledException) { throw; }
        catch (SocketException ex)
        {
            var status = ex.SocketErrorCode switch
            {
                SocketError.TimedOut => ProbeStatus.Timeout,
                SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown
                    or SocketError.HostDown or SocketError.ConnectionReset or SocketError.ConnectionRefused => ProbeStatus.Unreachable,
                _ => ProbeStatus.Error
            };
            return new ProbeResult(null, null, status, $"TCP socket {ex.SocketErrorCode}: {ex.Message}. A socket rejection without validated origin does not establish a destination response.")
                { Transmission = status == ProbeStatus.Error ? ProbeTransmission.NotSent : ProbeTransmission.Unknown,
                    ResponseKind = status == ProbeStatus.Unreachable ? ProbeResponseKind.NetworkRejection : ProbeResponseKind.None };
        }
        finally { socket?.Dispose(); }
    }

    private static async Task<ProbeResult> ProbeMacHopAsync(IPAddress destination, int ttl,
        ProbeOptions options, CancellationToken cancellationToken)
    {
        var ipv6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        var info = new ProcessStartInfo(ipv6 ? "/usr/sbin/traceroute6" : "/usr/sbin/traceroute")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-n");
        if (options.Protocol == ProbeProtocol.Icmp) info.ArgumentList.Add("-I");
        else
        {
            info.ArgumentList.Add("-e"); // Fixed requested destination port, not incrementing ports.
            if (ipv6) info.ArgumentList.Add(options.Protocol == ProbeProtocol.Tcp ? "-T" : "-U");
            else { info.ArgumentList.Add("-P"); info.ArgumentList.Add(options.Protocol == ProbeProtocol.Tcp ? "tcp" : "udp"); }
            info.ArgumentList.Add("-p"); info.ArgumentList.Add(options.DestinationPort.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(options.SourceAddress))
        { info.ArgumentList.Add("-s"); info.ArgumentList.Add(options.SourceAddress); }
        if (!ipv6 && !string.IsNullOrWhiteSpace(options.InterfaceId))
        { info.ArgumentList.Add("-i"); info.ArgumentList.Add(options.InterfaceId); }
        if (ipv6 && !string.IsNullOrWhiteSpace(options.InterfaceId) && string.IsNullOrWhiteSpace(options.SourceAddress))
            return new ProbeResult(null, null, ProbeStatus.Error, "IPv6 interface selection requires a source address.");
        info.ArgumentList.Add("-q"); info.ArgumentList.Add("1");
        info.ArgumentList.Add("-f"); info.ArgumentList.Add(ttl.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-m"); info.ArgumentList.Add(ttl.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add(Math.Max(1, options.Timeout.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture));
        info.ArgumentList.Add(destination.ToString());
        info.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout + TimeSpan.FromSeconds(1.5));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return ParseTraceroute(await stdout, destination, process.ExitCode, await stderr);
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

    public static ProbeResult ParseTraceroute(string output, IPAddress destination,
        int exitCode = 0, string? error = null)
    {
        var match = HopLine().Match(output);
        if (match.Success && IPAddress.TryParse(match.Groups[1].Value, out var address)
            && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rtt))
        {
            var marker = match.Groups[3].Value;
            var blocked = marker.Length > 1; // A bare ! means the responder's return TTL was low.
            var status = blocked ? ProbeStatus.Unreachable
                : address.Equals(destination) ? ProbeStatus.Success : ProbeStatus.TimeExceeded;
            return new ProbeResult(address.ToString(), blocked ? null : rtt, status,
                marker.Length > 0 ? "traceroute response " + marker : null);
        }
        return exitCode == 0 ? new ProbeResult(null, null, ProbeStatus.Timeout)
            : new ProbeResult(null, null, ProbeStatus.Error, error?.Trim());
    }

    [GeneratedRegex(@"^\s*\d+\s+(\S+)\s+([0-9.]+)\s*ms(?:\s+(!\S*))?", RegexOptions.Multiline)]
    private static partial Regex HopLine();
}
