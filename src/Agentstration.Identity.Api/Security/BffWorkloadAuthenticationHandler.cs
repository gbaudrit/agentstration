using System.Security.Claims;
using System.Text;
using Agentstration.Identity.Contracts;
using Agentstration.Security.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Identity.Api.Security;

public sealed class BffWorkloadAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IOptionsMonitor<BffWorkloadTrustOptions> trustOptions,
    IBffWorkloadReplayCache replayCache,
    ISecurityAuditWriter audit,
    IHostEnvironment environment,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = trustOptions.CurrentValue;
        if (!options.Enabled) return AuthenticateResult.Fail("BFF workload trust is disabled.");

        var workloadId = Header(BffWorkloadAuthentication.WorkloadHeader);
        var credentialId = Header(BffWorkloadAuthentication.CredentialHeader);
        var targetInstance = Header(BffWorkloadAuthentication.InstanceHeader);
        var timestampValue = Header(BffWorkloadAuthentication.TimestampHeader);
        var nonce = Header(BffWorkloadAuthentication.NonceHeader);
        var suppliedHash = Header(BffWorkloadAuthentication.ContentHashHeader);
        var signature = Header(BffWorkloadAuthentication.SignatureHeader);
        if (workloadId is null || credentialId is null || targetInstance is null || timestampValue is null ||
            nonce is null || suppliedHash is null || signature is null ||
            !long.TryParse(timestampValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timestamp))
            return await FailAsync("missing-or-invalid-headers");

        if (!string.Equals(targetInstance, options.InstanceId, StringComparison.Ordinal))
            return await FailAsync("instance-mismatch");
        var now = timeProvider.GetUtcNow();
        DateTimeOffset requestTime;
        try { requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return await FailAsync("timestamp-outside-window"); }
        var window = TimeSpan.FromSeconds(options.ReplayWindowSeconds);
        if ((now - requestTime).Duration() > window) return await FailAsync("timestamp-outside-window");

        var credential = options.Credentials.SingleOrDefault(value =>
            string.Equals(value.WorkloadId, workloadId, StringComparison.Ordinal) &&
            string.Equals(value.CredentialId, credentialId, StringComparison.Ordinal));
        if (credential is null || credential.Revoked) return await FailAsync("credential-unavailable");
        if ((credential.NotBefore is { } notBefore && now < notBefore) ||
            (credential.ExpiresAt is { } expiresAt && now >= expiresAt))
            return await FailAsync("credential-inactive");

        byte[] body;
        try { body = await ReadBodyAsync(options.MaximumBodyBytes, Context.RequestAborted); }
        catch (InvalidOperationException) { return await FailAsync("body-too-large"); }
        var actualHash = BffWorkloadAuthentication.HashContent(body);
        if (!CryptographicEquals(actualHash, suppliedHash)) return await FailAsync("content-hash-mismatch");

        byte[] key;
        try { key = await ReadKeyAsync(credential.SharedKeyFile, Context.RequestAborted); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogError("The configured BFF workload credential file could not be read.");
            return await FailAsync("credential-unavailable");
        }

        var canonical = BffWorkloadAuthentication.Canonicalize(
            Request.Method,
            $"{Request.PathBase}{Request.Path}{Request.QueryString}",
            targetInstance,
            timestamp,
            nonce,
            actualHash);
        if (!BffWorkloadAuthentication.Verify(key, canonical, signature)) return await FailAsync("signature-invalid");
        if (!replayCache.TryUse($"{workloadId}:{credentialId}", nonce, requestTime.Add(window)))
            return await FailAsync("replay-detected");

        var claims = new[]
        {
            new Claim(BffWorkloadAuthentication.WorkloadClaim, workloadId),
            new Claim(BffWorkloadAuthentication.CredentialClaim, credentialId)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        await audit.WriteAsync(new(
            SecurityAuditActions.BffWorkloadAuthenticated,
            ReasonCode: CredentialReference(workloadId, credentialId)), Context.RequestAborted);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private string? Header(string name)
    {
        var values = Request.Headers[name];
        return values.Count == 1 && values[0] is { Length: > 0 and <= 256 } value ? value : null;
    }

    private async Task<AuthenticateResult> FailAsync(string reason)
    {
        await audit.WriteAsync(new(
            SecurityAuditActions.BffWorkloadAuthenticationFailed,
            SecurityAuditOutcome.Failed,
            ReasonCode: reason), Context.RequestAborted);
        return AuthenticateResult.Fail("Invalid BFF workload authentication.");
    }

    private async Task<byte[]> ReadBodyAsync(int maximumBodyBytes, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > maximumBodyBytes) throw new InvalidOperationException("The request body is too large.");
        Request.EnableBuffering(bufferThreshold: Math.Min(maximumBodyBytes, 65_536), bufferLimit: maximumBodyBytes);
        await using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        Request.Body.Position = 0;
        if (buffer.Length > maximumBodyBytes) throw new InvalidOperationException("The request body is too large.");
        return buffer.ToArray();
    }

    private async Task<byte[]> ReadKeyAsync(string configuredPath, CancellationToken cancellationToken)
    {
        var path = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        var file = new FileInfo(path);
        if (file.Length is < 32 or > 4096) throw new InvalidOperationException("The credential file is invalid.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            var length = bytes.Length;
            while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n') length--;
            if (length < 32 || bytes.AsSpan(0, length).Contains((byte)'\r') || bytes.AsSpan(0, length).Contains((byte)'\n'))
                throw new InvalidOperationException("The credential file is invalid.");
            return bytes.AsSpan(0, length).ToArray();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static string CredentialReference(string workloadId, string credentialId) =>
        $"workload:{workloadId};credential:{credentialId}";
}
