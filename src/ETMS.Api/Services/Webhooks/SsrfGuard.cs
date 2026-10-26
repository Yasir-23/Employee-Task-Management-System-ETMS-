using System.Net;
using System.Net.Sockets;

namespace ETMS.Api.Services.Webhooks;

/// <summary>
/// SDD §10.2: keeps outbound webhooks away from internal networks. URLs are checked at registration, and the
/// resolved address is checked again at connect time so a DNS record changed later (rebinding) cannot bypass it.
/// </summary>
public static class SsrfGuard
{
    /// <summary>Returns an error message when <paramref name="url"/> is not an allowed receiver, otherwise null.</summary>
    public static string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Url must be an absolute https URL.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Url must not contain credentials.";
        }

        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "Url must not point to localhost.";
        }

        if (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address))
        {
            return "Url must not point to a loopback, link-local, or private address.";
        }

        return null;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC); // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 0                                  // 0.0.0.0/8
            || b[0] == 10                                   // 10.0.0.0/8
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 carrier-grade NAT
            || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local (cloud metadata)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
            || b[0] >= 224);                                // multicast, reserved, broadcast
    }

    /// <summary>SocketsHttpHandler.ConnectCallback that refuses to connect when the host resolves to a non-public address.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || !addresses.All(IsPublicAddress))
        {
            throw new HttpRequestException($"Webhook host '{context.DnsEndPoint.Host}' resolves to a non-public address.");
        }

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
