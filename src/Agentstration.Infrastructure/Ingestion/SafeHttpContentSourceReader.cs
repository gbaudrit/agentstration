using System.Net;
using Agentstration.Application;

namespace Agentstration.Infrastructure.Ingestion;

public sealed class SafeHttpContentSourceReader(IHttpClientFactory httpClientFactory) : IContentSourceReader
{
    public async Task<string> ReadUrlAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Loopback URLs and URL credentials are not allowed.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivate)) throw new InvalidOperationException("Private and unresolved network destinations are not allowed.");

        using var response = await httpClientFactory.CreateClient("ingestion").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidOperationException("Remote content exceeds the 2 MiB limit.");
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
    }
}
