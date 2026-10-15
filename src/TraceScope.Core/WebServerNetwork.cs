using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TraceScope.Core;

public sealed record WebServerAddress(string InterfaceName, string Address)
{
    public string? InterfaceId { get; init; }
    public bool IsAutomaticCandidate { get; init; }
    public override string ToString() => $"{InterfaceName} — {Address}";
}

public static class WebServerNetwork
{
    // Names/descriptions supplement the OS interface type: virtual Ethernet and VPN
    // adapters can be reported as ordinary Ethernet by their driver.
    private static readonly string[] ExcludedNames =
    ["loopback", "tunnel", "vpn", "utun", "ipsec", "ppp", "tun", "tap", "docker", "container",
        "veth", "virbr", "vmnet", "vbox", "virtual", "hyper-v", "vethernet", "wireguard", "tailscale",
        "zerotier", "hamachi", "bridge", "br-", "awdl", "llw", "p2p", "vnic", "vmware", "vmxnet", "parallels"];

    internal sealed record InterfaceSnapshot(string Id, string Name, string Description,
        NetworkInterfaceType Type, bool IsUp, IReadOnlyList<IPAddress> Addresses);
    internal sealed record DefaultRoute(string? InterfaceId = null, string? Address = null);

    /// <summary>Manual choices retain active private IPv4 and ULA IPv6 support.</summary>
    public static IReadOnlyList<WebServerAddress> GetLanAddresses() => GetLanAddresses(ReadInterfaces());

    internal static IReadOnlyList<WebServerAddress> GetLanAddresses(IReadOnlyList<InterfaceSnapshot> interfaces) =>
        interfaces.Where(n => n.IsUp && n.Type is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.Addresses.Where(IsPrivateAddress).Select(a => new WebServerAddress(FriendlyName(n), a.ToString())
            {
                InterfaceId = n.Id,
                IsAutomaticCandidate = IsAutomaticInterface(n) && a.AddressFamily == AddressFamily.InterNetwork
            }))
            .DistinctBy(a => a.Address).OrderBy(a => a.InterfaceName).ThenBy(a => a.Address).ToArray();

    /// <summary>Resolve only when settings are applied or Pngr starts; never migrate a running listener.</summary>
    public static WebServerAddress? GetAutomaticLanAddress() => GetAutomaticLanAddressAsync().GetAwaiter().GetResult();

    public static async Task<WebServerAddress?> GetAutomaticLanAddressAsync(CancellationToken cancellationToken = default)
    {
        DefaultRoute? route = null;
        if (OperatingSystem.IsMacOS())
            route = ParseMacDefaultRoute(await ReadCommandAsync("/sbin/route", ["-n", "get", "-inet", "default"], cancellationToken).ConfigureAwait(false));
        else if (OperatingSystem.IsWindows())
            route = ParseWindowsDefaultRoute(await ReadCommandAsync(Path.Combine(Environment.SystemDirectory, "route.exe"),
                ["PRINT", "-4", "0.0.0.0"], cancellationToken).ConfigureAwait(false));
        else if (OperatingSystem.IsLinux())
        {
            try { route = ParseLinuxDefaultRoute(await File.ReadAllTextAsync("/proc/net/route", cancellationToken).ConfigureAwait(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return SelectAutomaticLanAddress(ReadInterfaces(), route);
    }

    internal static WebServerAddress? SelectAutomaticLanAddress(IReadOnlyList<InterfaceSnapshot> interfaces, DefaultRoute? route)
    {
        if (route is null || route.InterfaceId is null && route.Address is null) return null;
        var matching = interfaces.Where(n => (route.InterfaceId is null || n.Id == route.InterfaceId || n.Name == route.InterfaceId)
            && (route.Address is null || n.Addresses.Any(a => a.ToString() == route.Address))).ToArray();
        // A missing/ambiguous default route or an excluded default adapter must not
        // cause us to fall back to another, possibly unintended, network.
        if (matching.Length != 1 || !IsAutomaticInterface(matching[0])) return null;
        var candidates = GetLanAddresses(matching).Where(a => a.IsAutomaticCandidate
            && (route.Address is null || a.Address == route.Address)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsAutomaticInterface(InterfaceSnapshot network) => network.IsUp
        && network.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
        && !ExcludedNames.Any(prefix => network.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || network.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase));

    private static string FriendlyName(InterfaceSnapshot network)
    {
        var kind = network.Type == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" :
            network.Type == NetworkInterfaceType.Ethernet ? "Ethernet" : "Other interface";
        // Windows supplies user-facing adapter names. Unix typically supplies en0/eth0.
        var name = network.Name;
        var lowLevel = name.StartsWith("en", StringComparison.Ordinal) && name.Skip(2).All(char.IsDigit)
            || name.StartsWith("eth", StringComparison.Ordinal) && name.Skip(3).All(char.IsDigit)
            || name.StartsWith("wlan", StringComparison.Ordinal) && name.Skip(4).All(char.IsDigit);
        return lowLevel || string.Equals(name, kind, StringComparison.OrdinalIgnoreCase) ? kind : $"{kind} ({name})";
    }

    private static IReadOnlyList<InterfaceSnapshot> ReadInterfaces()
    {
        try
        {
            var results = new List<InterfaceSnapshot>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    results.Add(new(network.Id, network.Name, network.Description, network.NetworkInterfaceType,
                        network.OperationalStatus == OperationalStatus.Up,
                        network.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray()));
                }
                catch (NetworkInformationException) { } // One disappearing adapter must not hide the others.
            }
            return results;
        }
        catch (NetworkInformationException) { return []; }
        catch (PlatformNotSupportedException) { return []; }
    }

    private static async Task<string> ReadCommandAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return "";
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : "";
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return "";
        }
        catch (Win32Exception) { return ""; }
        catch (IOException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    internal static DefaultRoute? ParseMacDefaultRoute(string output)
    {
        var names = output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("interface:", StringComparison.Ordinal))
            .Select(line => line["interface:".Length..].Trim()).Where(name => name.Length > 0 && !name.Any(char.IsWhiteSpace)).Distinct().ToArray();
        return names.Length == 1 ? new(InterfaceId: names[0]) : null;
    }

    internal static DefaultRoute? ParseWindowsDefaultRoute(string output)
    {
        // Parse numeric active rows only, independent of localized table headings.
        // The displayed metric combines the route and interface metrics. Persistent
        // route rows have four columns and must not participate in selection.
        var routes = new List<(string Address, int Metric)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5 && parts[0] == "0.0.0.0" && parts[1] == "0.0.0.0"
                && IPAddress.TryParse(parts[3], out var address) && address.AddressFamily == AddressFamily.InterNetwork
                && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var metric))
                routes.Add((address.ToString(), metric));
        }
        if (routes.Count == 0) return null;
        var best = routes.Where(r => r.Metric == routes.Min(r => r.Metric)).Select(r => r.Address).Distinct().ToArray();
        return best.Length == 1 ? new(Address: best[0]) : null;
    }

    internal static DefaultRoute? ParseLinuxDefaultRoute(string output)
    {
        var routes = new List<(string Interface, int Metric)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 8 && parts[1] == "00000000" && parts[7] == "00000000"
                && int.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) && (flags & 1) != 0
                && int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var metric))
                routes.Add((parts[0], metric));
        }
        if (routes.Count == 0) return null;
        var best = routes.Where(r => r.Metric == routes.Min(r => r.Metric)).Select(r => r.Interface).Distinct().ToArray();
        return best.Length == 1 ? new(InterfaceId: best[0]) : null;
    }

    internal static IPAddress ValidateLanAddress(WebServerOptions options, IReadOnlyList<WebServerAddress> available)
    {
        if (!options.HttpAcknowledged) throw new InvalidOperationException("Acknowledge unencrypted LAN HTTP before enabling access.");
        if (!WebAccessKey.IsValidVerifier(options.AccessKeyVerifier)) throw new InvalidOperationException("Generate an access key before enabling LAN access.");
        if (!IPAddress.TryParse(options.LanAddress, out var selected) || !IsPrivateAddress(selected)
            || !available.Any(a => IPAddress.TryParse(a.Address, out var assigned) && assigned.Equals(selected)))
            throw new InvalidOperationException("The selected private network address is unavailable. Refresh interfaces and select an active address.");
        return selected;
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}
