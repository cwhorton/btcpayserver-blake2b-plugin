#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>
/// Sources chosen by store owners may only be on the public internet: the server must not be
/// usable to reach its own private network. The check happens when connecting, against the
/// resolved addresses, so a host name can't be pointed somewhere private after it was entered.
/// </summary>
public static class NetworkRestrictions
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // carrier-grade NAT
                     (b[0] == 169 && b[1] == 254) ||                 // link-local
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast ||
                     (b[0] & 0xfe) == 0xfc);                         // unique local fc00::/7
        }
        return false;
    }

    public static async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new ChainSourceException($"{host} is on a private network, which store sources may not use");
        return allowed;
    }

    /// <summary>For HttpClient: connect only to public addresses.</summary>
    public static async ValueTask<System.IO.Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, cancellationToken);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
