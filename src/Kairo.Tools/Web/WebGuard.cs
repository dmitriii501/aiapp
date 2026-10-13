using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Kairo.Tools.Web;

/// <summary>
/// Keeps the web tools on the public internet. A page the agent fetches can come from anywhere (or be steered by a prompt
/// injection), so it must not reach the user's machine or network: loopback, private ranges, link-local addresses
/// (cloud metadata lives at 169.254.169.254) and the like are refused, both before the request and at connection time.
/// </summary>
public static class WebGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
            return false;

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0                                  // "this network"
                || b[0] == 10                                   // private
                || (b[0] == 100 && b[1] is >= 64 and <= 127)    // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                 // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)     // private
                || (b[0] == 192 && b[1] == 168)                 // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)      // protocol assignments
                || (b[0] == 198 && b[1] is 18 or 19)            // benchmarking
                || b[0] >= 224);                                // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                        // unique local fc00::/7
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)); // documentation
        }

        return false;
    }

    /// <summary>Checks a URL before any request: http(s) only, no credentials, and a host that is not obviously local.</summary>
    /// <returns>Null when the URL may be requested, otherwise the reason (for the model).</returns>
    public static string? Reject(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            return $"Only http and https addresses can be fetched, not '{uri.Scheme}'.";
        if (uri.UserInfo.Length > 0)
            return "Addresses with a user name or password are not fetched.";

        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return $"'{host}' is a local address. The web tools only reach the public internet; use Shell for local services.";
        if (!host.Contains('.') && !host.Contains(':'))
            return $"'{host}' is not a public host name.";
        if (IPAddress.TryParse(host, out var ip) && !IsPublic(ip))
            return $"'{host}' is not a public address. The web tools only reach the public internet.";
        return null;
    }

    /// <summary>
    /// The real client: no automatic redirects (the tool decides), no proxy (the check below must see the real destination),
    /// and addresses are checked when connecting, so a name that resolves to a private address, or changes between a check
    /// and the connection, cannot get through.
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Blocked: '{host}' does not resolve to a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
