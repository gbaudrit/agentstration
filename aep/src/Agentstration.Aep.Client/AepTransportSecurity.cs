using System.Net;
using System.Net.Sockets;

namespace Agentstration.Aep.Client;

public sealed class AepTransportSecurityOptions
{
    public const string SectionName = "Agentstration:Aep:Transport";
    public ISet<string> AllowedHttpHosts { get; set; } = new HashSet<string>(["localhost", "127.0.0.1", "::1"], StringComparer.OrdinalIgnoreCase);
    public ISet<string> AllowedPrivateNetworkHosts { get; set; } = new HashSet<string>(["localhost", "127.0.0.1", "::1"], StringComparer.OrdinalIgnoreCase);
    public bool BlockPrivateNetworks { get; set; } = true;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);
    public long MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;
    public int MaximumStreamingUpdates { get; set; } = 100_000;
    public int MaximumStreamingLineCharacters { get; set; } = 1024 * 1024;

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("AEP connect timeout must be between zero and one minute.");
        if (PooledConnectionLifetime <= TimeSpan.Zero || PooledConnectionLifetime > TimeSpan.FromHours(1))
            throw new InvalidOperationException("AEP pooled connection lifetime must be between zero and one hour.");
        if (MaximumResponseBytes is < 1024 or > 256L * 1024 * 1024)
            throw new InvalidOperationException("AEP maximum response bytes must be between 1 KiB and 256 MiB.");
        if (MaximumStreamingUpdates is < 1 or > 1_000_000)
            throw new InvalidOperationException("AEP maximum streaming updates must be between 1 and 1,000,000.");
        if (MaximumStreamingLineCharacters is < 1024 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("AEP maximum streaming line characters must be between 1 KiB and 4 MiB.");
        EnsureHostSet(AllowedHttpHosts, nameof(AllowedHttpHosts));
        EnsureHostSet(AllowedPrivateNetworkHosts, nameof(AllowedPrivateNetworkHosts));
    }

    private static void EnsureHostSet(IEnumerable<string> hosts, string property)
    {
        if (hosts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{property} cannot contain an empty host.");
    }
}

public sealed class AepTransportSecurityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class AepTransportSecurity
{
    public static void ValidateEndpoint(Uri endpoint, AepTransportSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
            throw new AepTransportSecurityException("endpoint_invalid", "The AEP endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new AepTransportSecurityException("endpoint_invalid", "The AEP endpoint cannot contain credentials or a fragment.");
        if (endpoint.Scheme == Uri.UriSchemeHttp && !options.AllowedHttpHosts.Contains(endpoint.IdnHost))
            throw new AepTransportSecurityException("https_required", "Remote AEP endpoints require HTTPS.");
        if (IPAddress.TryParse(endpoint.IdnHost, out var address) && !IsAddressAllowed(endpoint.IdnHost, address, options))
            throw new AepTransportSecurityException("endpoint_address_blocked", "The AEP endpoint resolves to a blocked network address.");
    }

    public static Uri ResolveSameOrigin(Uri origin, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!origin.IsAbsoluteUri)
            throw new AepTransportSecurityException("endpoint_invalid", "The registered AEP origin must be absolute.");
        if (!Uri.TryCreate(origin, endpoint, out var resolved)
            || !SameOrigin(origin, resolved)
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || !string.IsNullOrEmpty(resolved.Fragment))
            throw new AepTransportSecurityException("endpoint_origin_mismatch", "The AEP protocol endpoint must remain on the registered origin.");
        return resolved;
    }

    public static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    internal static bool IsAddressAllowed(string host, IPAddress address, AepTransportSecurityOptions options)
    {
        if (IsNeverAllowed(address)) return false;
        if (!options.BlockPrivateNetworks || !IsPrivate(address)) return true;
        return options.AllowedPrivateNetworkHosts.Contains(host);
    }

    private static bool IsNeverAllowed(IPAddress address) =>
        address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || address.Equals(IPAddress.Broadcast)
        || address.IsIPv6Multicast
        || IsIpv4Range(address, 224, 0, 0, 0, 4)
        || IsIpv4Range(address, 169, 254, 0, 0, 16)
        || address.IsIPv6LinkLocal;

    private static bool IsPrivate(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || IsIpv4Range(address, 10, 0, 0, 0, 8)
        || IsIpv4Range(address, 172, 16, 0, 0, 12)
        || IsIpv4Range(address, 192, 168, 0, 0, 16)
        || address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xfe) == 0xfc;

    private static bool IsIpv4Range(IPAddress address, byte a, byte b, byte c, byte d, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        var candidate = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var network = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (candidate & mask) == (network & mask);
    }
}

public static class AepSecureHttpMessageHandler
{
    public static HttpMessageHandler Create(AepTransportSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, options, cancellationToken)
        };
        return new AepTransportGuardHandler(options) { InnerHandler = sockets };
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        AepTransportSecurityOptions options,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(value => AepTransportSecurity.IsAddressAllowed(context.DnsEndPoint.Host, value, options)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("The AEP endpoint resolved only to blocked network addresses.");
        Exception? lastError = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (exception is OperationCanceledException) throw;
                lastError = exception;
            }
        }
        throw new HttpRequestException("The AEP endpoint could not be reached through an allowed network address.", lastError);
    }

    private sealed class AepTransportGuardHandler(AepTransportSecurityOptions options) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null)
                throw new HttpRequestException("The AEP request URI is missing.");
            try { AepTransportSecurity.ValidateEndpoint(request.RequestUri, options); }
            catch (AepTransportSecurityException exception)
            {
                throw new HttpRequestException(exception.Message, exception);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
