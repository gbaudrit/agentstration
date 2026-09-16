using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Security;

public sealed class BffWorkloadSigningHandler(
    IOptionsMonitor<BffWorkloadClientOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var configuration = options.CurrentValue;
        if (!configuration.Enabled) throw new InvalidOperationException("BFF workload authentication is disabled.");

        var content = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentHash = BffWorkloadAuthentication.HashContent(content);
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = BffWorkloadAuthentication.CreateNonce();
        var pathAndQuery = request.RequestUri is { IsAbsoluteUri: true } absolute
            ? absolute.PathAndQuery
            : request.RequestUri?.OriginalString ?? "/";
        if (!pathAndQuery.StartsWith('/')) pathAndQuery = "/" + pathAndQuery;

        var key = await ReadKeyAsync(configuration.SharedKeyFile, cancellationToken);
        try
        {
            var canonical = BffWorkloadAuthentication.Canonicalize(
                request.Method.Method,
                pathAndQuery,
                configuration.TargetInstanceId,
                timestamp,
                nonce,
                contentHash);
            Set(request, BffWorkloadAuthentication.WorkloadHeader, configuration.WorkloadId);
            Set(request, BffWorkloadAuthentication.CredentialHeader, configuration.CredentialId);
            Set(request, BffWorkloadAuthentication.InstanceHeader, configuration.TargetInstanceId);
            Set(request, BffWorkloadAuthentication.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            Set(request, BffWorkloadAuthentication.NonceHeader, nonce);
            Set(request, BffWorkloadAuthentication.ContentHashHeader, contentHash);
            Set(request, BffWorkloadAuthentication.SignatureHeader, BffWorkloadAuthentication.Sign(key, canonical));
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> ReadKeyAsync(string configuredPath, CancellationToken cancellationToken)
    {
        var path = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        var file = new FileInfo(path);
        if (file.Length is < 32 or > 4096)
            throw new InvalidOperationException("The BFF workload credential file is invalid.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            var length = bytes.Length;
            while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n') length--;
            if (length < 32 || bytes.AsSpan(0, length).Contains((byte)'\r') || bytes.AsSpan(0, length).Contains((byte)'\n'))
                throw new InvalidOperationException("The BFF workload credential file is invalid.");
            return bytes.AsSpan(0, length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
