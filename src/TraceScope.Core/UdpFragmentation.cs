using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TraceScope.Core;

internal static class UdpFragmentation
{
    // IPv6 routers never fragment; IPV6_DONTFRAG also prevents source-side
    // fragmentation so an oversized configured datagram fails rather than
    // silently changing its on-wire size.
    public static void ConfigureIpv6(Socket socket, bool dontFragment)
    {
        if (OperatingSystem.IsWindows())
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.DontFragment, dontFragment);
        else
        {
            var value = dontFragment ? 1 : 0;
            // IPV6_DONTFRAG is 62 on Darwin and Linux; IPPROTO_IPV6 is 41.
            if (setsockopt(socket.SafeHandle.DangerousGetHandle().ToInt32(), 41, 62,
                    ref value, sizeof(int)) != 0)
                throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    public static void ConfigureIpv6HopLimit(Socket socket, int ttl)
    {
        if (OperatingSystem.IsWindows())
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, ttl);
        else if (setsockopt(socket.SafeHandle.DangerousGetHandle().ToInt32(), 41,
                     OperatingSystem.IsMacOS() ? 4 : 16, ref ttl, sizeof(int)) != 0)
            throw new SocketException(Marshal.GetLastPInvokeError());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int socket, int level, int option, ref int value, int length);
}
