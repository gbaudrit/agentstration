using System.Net;
using System.Net.Sockets;

namespace Agentstration.Extensions.Foundry;

public static class FoundrySecureTransport
{
    public static HttpMessageHandler Create(FoundryExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) => ConnectAllowedAsync(context, options, cancellationToken)
        };
    }

    public static bool IsAddressAllowed(IPAddress address, string host, FoundryExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(options);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast || address.IsIPv6LinkLocal
            || InRange(address, 169, 254, 0, 0, 16) || InRange(address, 224, 0, 0, 0, 4)
            || InRange(address, 240, 0, 0, 0, 4)) return false;
        if (IPAddress.IsLoopback(address) || InRange(address, 10, 0, 0, 0, 8)
            || InRange(address, 172, 16, 0, 0, 12) || InRange(address, 192, 168, 0, 0, 16)
            || address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xfe) == 0xfc)
            return options.AllowedPrivateHosts.Contains(host);
        return true;
    }

    private static async ValueTask<Stream> ConnectAllowedAsync(SocketsHttpConnectionContext context, FoundryExtensionOptions options, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(address => IsAddressAllowed(address, host, options)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("The Foundry endpoint resolved only to blocked network addresses.");
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
        throw new HttpRequestException("The Foundry endpoint could not be reached through an allowed network address.", lastError);
    }

    private static bool InRange(IPAddress address, byte a, byte b, byte c, byte d, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var network = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
        return (value & (uint.MaxValue << (32 - prefixLength))) == network;
    }
}
