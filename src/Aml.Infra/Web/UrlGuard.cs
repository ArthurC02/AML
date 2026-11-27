using System.Net;
using System.Net.Sockets;
using Aml.Core.Pages;

namespace Aml.Infra.Web;

/// <summary>
/// The URL guard at the fetch boundary (security-and-privacy.md#external-lookups): a received URL is someone else's input,
/// so before every request, robots.txt and redirect hops included, it must meet <see cref="PageUrl.Broken"/> and its host
/// must resolve to public addresses only. Every address the name resolves to is checked, not one; the connection itself
/// goes only to an address checked at connect time (<see cref="Handler"/>), so a name that re-resolves between the check
/// and the connect (DNS rebinding) is refused too.
/// </summary>
public static class UrlGuard
{
    /// <summary>The problem code that stops the request (URL_NOT_ALLOWED, or UNREACHABLE when the name resolves to nothing), or null.</summary>
    public static async Task<string?> RefusalAsync(Uri url, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        if (PageUrl.Broken(url) is not null) return PageProblems.UrlNotAllowed;
        var addresses = await AddressesAsync(url.DnsSafeHost, resolve, ct).ConfigureAwait(false);
        return addresses.Length == 0 ? PageProblems.Unreachable : addresses.All(IsPublic) ? null : PageProblems.UrlNotAllowed;
    }

    private static async Task<IPAddress[]> AddressesAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct) =>
        IPAddress.TryParse(host, out var literal) ? [literal] : await resolve(host, ct).ConfigureAwait(false);

    /// <summary>
    /// Not loopback, private, link-local, unique-local, carrier-grade NAT, multicast, reserved or unspecified, in IPv4 or
    /// IPv6; an IPv4 address carried in IPv6 (mapped, NAT64, 6to4) is judged as that IPv4 address; Teredo, SIIT, local-use
    /// NAT64, the discard prefix and the IPv6 documentation prefixes are refused.
    /// ponytail: the reserved ranges of RFC 6890 that a bank network may use; a bank whose internal hosts sit on public
    /// addresses needs those ranges added here.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 or >= 224                // this network, private, loopback, multicast and reserved, broadcast
                || b[0] == 100 && (b[1] & 0xC0) == 64                // 100.64.0.0/10 carrier-grade NAT
                || b[0] == 169 && b[1] == 254                        // link-local
                || b[0] == 172 && (b[1] & 0xF0) == 16                // 172.16.0.0/12
                || b[0] == 192 && b[1] == 168                        // 192.168.0.0/16
                || b[0] == 192 && b[1] == 0 && b[2] == 0             // 192.0.0.0/24 protocol assignments
                || b[0] == 198 && (b[1] & 0xFE) == 18);              // 198.18.0.0/15 benchmarking
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (b[0] == 0x00 && b.AsSpan(1, 11).IndexOfAnyExcept((byte)0) < 0) return false; // ::/96: unspecified, loopback, IPv4-compatible
        if (b[0] == 0xFF) return false;                                                    // multicast
        if (b[0] == 0xFE && (b[1] & 0x80) == 0x80) return false;                           // fe80::/10 link-local, fec0::/10 site-local
        if ((b[0] & 0xFE) == 0xFC) return false;                                           // fc00::/7 unique-local
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;          // 2001::/32 Teredo
        if (b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0) return false; // ::ffff:0:0/96 SIIT
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01) return false;          // 64:ff9b:1::/48 local NAT64
        if (b[0] == 0x01 && b[1] == 0x00 && b.AsSpan(2, 6).IndexOfAnyExcept((byte)0) < 0) return false;                         // 100::/64 discard
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8 || b[0] == 0x3F && (b[1] & 0xF0) == 0xF0) return false; // 2001:db8::/32, 3fff::/20 documentation
        if (b.AsSpan(0, 12).SequenceEqual(Nat64)) return IsPublic(new IPAddress(b.AsSpan(12, 4)));
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b.AsSpan(2, 4)));    // 2002::/16 6to4
        return true;
    }

    private static readonly byte[] Nat64 = [0x00, 0x64, 0xFF, 0x9B, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// The production handler: no automatic redirects (the fetcher follows them itself, through the guard), and a connect
    /// step that resolves the host again and connects only when every address is public.
    /// ponytail: no proxy, since the connect check would then see the proxy's address; a bank egress proxy needs its own
    /// allow rules, and this handler a check of the target before the CONNECT.
    /// </summary>
    public static SocketsHttpHandler Handler(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false, // nothing needs a site's cookies; none is kept from one page to the next
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await AddressesAsync(context.DnsEndPoint.Host, resolve, ct).ConfigureAwait(false);
            if (addresses.Length == 0 || !addresses.All(IsPublic)) throw new AddressRefusedException();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>The connect step found a host address that is not public (<see cref="UrlGuard.Handler"/>).</summary>
public sealed class AddressRefusedException : Exception
{
    public AddressRefusedException() : base("The host resolves to an address that is not public.") { }

    public AddressRefusedException(string message) : base(message) { }

    public AddressRefusedException(string message, Exception inner) : base(message, inner) { }
}
