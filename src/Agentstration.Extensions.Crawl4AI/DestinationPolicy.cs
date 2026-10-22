using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public sealed class DestinationPolicy(IOptions<Crawl4AiOptions> options, IDestinationAddressResolver resolver)
{
    private readonly Crawl4AiOptions options = options.Value;

    public async Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken)
    {
        if (value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
            throw Error("crawl4ai_url_invalid", "A credential-free absolute HTTP(S) URL is required.");
        if (!options.AllowedPorts.Contains(uri.Port))
            throw Error("crawl4ai_port_denied", $"Destination port '{uri.Port}' is not allowed.");
        if (!IsAllowedDomain(uri.IdnHost))
            throw Error("crawl4ai_domain_denied", $"Destination domain '{uri.IdnHost}' is not allowed.");
        IReadOnlyList<IPAddress> addresses;
        try { addresses = await resolver.ResolveAsync(uri.DnsSafeHost, cancellationToken); }
        catch (SocketException exception) { throw Error("crawl4ai_dns_failed", "The destination domain could not be resolved.", exception); }
        if (addresses.Count == 0) throw Error("crawl4ai_dns_failed", "The destination domain resolved to no address.");
        if (addresses.Any(address => !IsAllowedAddress(address, options.AllowPrivateAddresses)))
            throw Error("crawl4ai_destination_denied", "The destination resolves to a private, loopback, link-local, or otherwise non-public address.");
        return uri;
    }

    private bool IsAllowedDomain(string host) => options.AllowedDomains.Any(value =>
    {
        var allowed = value.Trim().Trim('.');
        var normalizedHost = host.TrimEnd('.');
        return string.Equals(normalizedHost, allowed, StringComparison.OrdinalIgnoreCase)
            || normalizedHost.EndsWith($".{allowed}", StringComparison.OrdinalIgnoreCase);
    });

    private static bool IsAllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.None)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return allowPrivate || (bytes[0] & 0xfe) != 0xfc;
        return bytes[0] switch
        {
            0 or 127 => false,
            10 => allowPrivate,
            100 when bytes[1] is >= 64 and <= 127 => allowPrivate,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => allowPrivate,
            192 when bytes[1] == 168 => allowPrivate,
            192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false,
            198 when bytes[1] is 18 or 19 => false,
            198 when bytes[1] == 51 && bytes[2] == 100 => false,
            203 when bytes[1] == 0 && bytes[2] == 113 => false,
            >= 224 => false,
            _ => true
        };
    }

    private static Crawl4AiException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}
