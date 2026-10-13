using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TraceScope.Core;

public sealed record NetworkInterfaceInfo(string Id, string Name, string Description,
    string Type, string Status, IReadOnlyList<string> Addresses, IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers, string MacAddress, long Speed, int? Mtu);

public sealed record LocalNeighbor(string Address, string? MacAddress, string? Interface, string State);

/// <summary>Interface inventory and existing OS neighbor caches. Never scans a subnet.</summary>
public static class NetworkDiscovery
{
    public static IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        var results = new List<NetworkInterfaceInfo>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = network.GetIPProperties();
                int? mtu = null;
                try { mtu = properties.GetIPv4Properties()?.Mtu ?? properties.GetIPv6Properties()?.Mtu; }
                catch (NetworkInformationException) { }
                results.Add(new(network.Id, network.Name, network.Description,
                    network.NetworkInterfaceType.ToString(), network.OperationalStatus.ToString(),
                    properties.UnicastAddresses.Select(a => a.Address.ToString()).ToArray(),
                    properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(a => a != "0.0.0.0" && a != "::").ToArray(),
                    properties.DnsAddresses.Select(a => a.ToString()).ToArray(),
                    network.GetPhysicalAddress().ToString(), network.Speed, mtu));
            }
            catch (NetworkInformationException) { }
        }
        return results.OrderByDescending(i => i.Status == "Up").ThenBy(i => i.Name).ToArray();
    }

    public static string? GetSourceAddress(string interfaceId, AddressFamily family = AddressFamily.InterNetwork)
        => GetInterfaces().FirstOrDefault(i => i.Id == interfaceId || i.Name == interfaceId)?.Addresses
            .FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == family);

    public static async Task<string?> GetLocalGatewayAsync(CancellationToken cancellationToken = default)
    {
        var gateway = GetInterfaces().Where(i => i.Status == "Up")
            .SelectMany(i => i.Gateways).FirstOrDefault(a => IPAddress.TryParse(a, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork);
        if (gateway is not null || !OperatingSystem.IsMacOS()) return gateway;
        var output = await ReadCommandAsync("/sbin/route", ["-n", "get", "default"], cancellationToken);
        var match = Regex.Match(output, @"gateway:\s*(\S+)");
        return match.Success && IPAddress.TryParse(match.Groups[1].Value, out var address) ? address.ToString() : null;
    }

    public static async Task<IReadOnlyList<LocalNeighbor>> LocalNeighborsAsync(CancellationToken cancellationToken = default)
    {
        var executable = OperatingSystem.IsMacOS() ? "/usr/sbin/arp" : OperatingSystem.IsWindows() ? "arp.exe" : "ip";
        string[] arguments = OperatingSystem.IsMacOS() ? ["-an"] : OperatingSystem.IsWindows() ? ["-a"] : ["neigh", "show"];
        return ParseNeighbors(await ReadCommandAsync(executable, arguments, cancellationToken));
    }

    internal static IReadOnlyList<LocalNeighbor> ParseNeighbors(string output)
    {
        var neighbors = new List<LocalNeighbor>();
        string? currentInterface = null;
        foreach (var line in output.Split('\n'))
        {
            var header = Regex.Match(line, @"Interface:\s*(\S+)");
            if (header.Success) { currentInterface = header.Groups[1].Value; continue; }
            var mac = Regex.Match(line, @"\(([^)]+)\)\s+at\s+(\S+).*?\son\s+(\S+)");
            if (mac.Success && IPAddress.TryParse(mac.Groups[1].Value, out var ip))
            {
                var hardware = mac.Groups[2].Value;
                neighbors.Add(new(ip.ToString(), hardware == "(incomplete)" ? null : hardware,
                    mac.Groups[3].Value, hardware == "(incomplete)" ? "Incomplete" : "Cached"));
                continue;
            }
            var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out ip)) continue;
            var hardwareIndex = Array.IndexOf(fields, "lladdr");
            var interfaceIndex = Array.IndexOf(fields, "dev");
            var hardwareAddress = hardwareIndex >= 0 && hardwareIndex + 1 < fields.Length ? fields[hardwareIndex + 1]
                : Regex.IsMatch(fields[1], @"^(?:[0-9a-fA-F]{1,2}[:-]){5}[0-9a-fA-F]{1,2}$") ? fields[1] : null;
            neighbors.Add(new(ip.ToString(), hardwareAddress,
                interfaceIndex >= 0 && interfaceIndex + 1 < fields.Length ? fields[interfaceIndex + 1] : currentInterface,
                fields[^1]));
        }
        return neighbors.DistinctBy(n => (n.Address, n.Interface)).ToArray();
    }

    private static async Task<string> ReadCommandAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new(executable)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return await output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ""; }
        catch (System.ComponentModel.Win32Exception) { return ""; }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException) { }
        }
    }
}
