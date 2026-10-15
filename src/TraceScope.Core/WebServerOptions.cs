using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TraceScope.Core;

public sealed record WebServerOptions
{
    public int Port { get; init; } = 8787;
    public bool LanEnabled { get; init; }
    public string? LanAddress { get; init; }
    public bool HttpAcknowledged { get; init; }
    public string? AccessKeyVerifier { get; init; }
    // Record's default ToString would disclose the verifier in diagnostics.
    public override string ToString() => $"Web server: port {Port}, LAN {LanEnabled}";
}

/// <summary>Only a SHA-256 verifier is persisted; a lost key must be regenerated.</summary>
public static class WebAccessKey
{
    public static string Generate() => "pngr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string CreateVerifier(string key)
    {
        if (key.Length != 69 || !key.StartsWith("pngr_", StringComparison.Ordinal)
            || key.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("Invalid generated access key format.", nameof(key));
        return "sha256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
    public static bool IsValidVerifier(string? verifier)
    {
        if (verifier is null || !verifier.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        Span<byte> hash = stackalloc byte[32];
        return Convert.TryFromBase64String(verifier[7..], hash, out var count) && count == 32;
    }
    public static bool Verify(string? key, string? verifier)
    {
        if (key is null || key.Length != 69 || !IsValidVerifier(verifier)) return false;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(verifier![7..]));
    }
}

public sealed record WebServerAddress(string InterfaceName, string Address)
{
    public override string ToString() => $"{InterfaceName} · {Address}";
}

public static class WebServerNetwork
{
    /// <summary>Explicit candidates only: active, assigned private addresses, excluding tunnel interfaces.</summary>
    public static IReadOnlyList<WebServerAddress> GetLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => IsPrivateAddress(a.Address))
                    .Select(a => new WebServerAddress(n.Name, a.Address.ToString())))
                .DistinctBy(a => a.Address).OrderBy(a => a.InterfaceName).ThenBy(a => a.Address).ToArray();
        }
        catch (NetworkInformationException) { return []; }
        catch (PlatformNotSupportedException) { return []; }
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
