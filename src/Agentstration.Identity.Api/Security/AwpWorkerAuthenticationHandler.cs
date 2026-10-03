using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Security.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Identity.Api.Security;

public sealed class AwpWorkerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IOptionsMonitor<AwpWorkerTrustOptions> trustOptions,
    IAwpWorkerReplayCache replayCache,
    AwpWorkerIdentityService identities,
    ISecurityAuditWriter audit,
    IHostEnvironment environment,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = trustOptions.CurrentValue;
        if (!options.Enabled) return AuthenticateResult.Fail("AWP Worker trust is disabled.");

        if (!Guid.TryParse(Header(AwpWorkerAuthentication.WorkerHeader), out var workerId)
            || workerId == Guid.Empty
            || !Guid.TryParse(Header(AwpWorkerAuthentication.SessionHeader), out var sessionId)
            || sessionId == Guid.Empty
            || !Guid.TryParse(Header(AwpWorkerAuthentication.CredentialHeader), out var credentialId)
            || credentialId == Guid.Empty
            || Header(AwpWorkerAuthentication.InstanceHeader) is not { } targetInstance
            || Header(AwpWorkerAuthentication.TimestampHeader) is not { } timestampValue
            || Header(AwpWorkerAuthentication.NonceHeader) is not { } nonce
            || Header(AwpWorkerAuthentication.ContentHashHeader) is not { } suppliedHash
            || Header(AwpWorkerAuthentication.SignatureHeader) is not { } signature
            || !long.TryParse(timestampValue, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var timestamp))
            return await FailAsync("missing-or-invalid-headers", workerId);

        if (!string.Equals(targetInstance, options.InstanceId, StringComparison.Ordinal))
            return await FailAsync("instance-mismatch", workerId);
        DateTimeOffset requestTime;
        try { requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return await FailAsync("timestamp-outside-window", workerId); }
        var window = TimeSpan.FromSeconds(options.ReplayWindowSeconds);
        if ((timeProvider.GetUtcNow() - requestTime).Duration() > window)
            return await FailAsync("timestamp-outside-window", workerId);

        byte[] body;
        try { body = await ReadBodyAsync(options.MaximumBodyBytes, Context.RequestAborted); }
        catch (InvalidOperationException) { return await FailAsync("body-too-large", workerId); }
        var actualHash = AwpWorkerAuthentication.HashContent(body);
        if (!CryptographicEquals(actualHash, suppliedHash)) return await FailAsync("content-hash-mismatch", workerId);

        byte[]? key = null;
        try
        {
            var configured = options.Credentials.SingleOrDefault(value => value.WorkerId == workerId && value.CredentialId == credentialId);
            if (configured is not null)
            {
                var now = timeProvider.GetUtcNow();
                if (configured.Revoked || (configured.NotBefore is { } notBefore && now < notBefore)
                    || (configured.ExpiresAt is { } expiresAt && now >= expiresAt))
                    return await FailAsync("credential-inactive", workerId);
                key = await ReadKeyAsync(configured.SharedKeyFile, Context.RequestAborted);
            }
            else
            {
                using var material = await identities.GetCredentialAsync(workerId, credentialId, Context.RequestAborted);
                if (material is null) return await FailAsync("credential-unavailable", workerId);
                key = material.Secret.AccessValue().ToArray();
            }

            var canonical = AwpWorkerAuthentication.Canonicalize(
                Request.Method,
                $"{Request.PathBase}{Request.Path}{Request.QueryString}",
                targetInstance,
                workerId,
                sessionId,
                timestamp,
                nonce,
                actualHash);
            if (!AwpWorkerAuthentication.Verify(key, canonical, signature))
                return await FailAsync("signature-invalid", workerId);
            if (!replayCache.TryUse(credentialId, nonce, requestTime.Add(window)))
                return await FailAsync("replay-detected", workerId);
            if (!Request.Path.Equals("/api/awp/v1/session", StringComparison.Ordinal)
                && !await identities.IsSessionActiveAsync(workerId, sessionId, Context.RequestAborted))
                return await FailAsync("session-superseded", workerId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogError("An AWP Worker credential could not be read.");
            return await FailAsync("credential-unavailable", workerId);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }

        var claims = new[]
        {
            new Claim(AwpWorkerAuthentication.WorkerClaim, workerId.ToString("D")),
            new Claim(AwpWorkerAuthentication.SessionClaim, sessionId.ToString("D")),
            new Claim(AwpWorkerAuthentication.CredentialClaim, credentialId.ToString("D")),
            new Claim(AwpWorkerAuthentication.ProtocolClaim, AwpWorkerAuthentication.ProtocolVersion)
        };
        await audit.WriteAsync(new(SecurityAuditActions.AwpWorkerAuthenticated, TargetAccountId: workerId,
            ReasonCode: $"credential:{credentialId:D}"), Context.RequestAborted);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    private string? Header(string name)
    {
        var values = Request.Headers[name];
        return values.Count == 1 && values[0] is { Length: > 0 and <= 256 } value ? value : null;
    }

    private async Task<AuthenticateResult> FailAsync(string reason, Guid workerId)
    {
        await audit.WriteAsync(new(SecurityAuditActions.AwpWorkerAuthenticationFailed,
            SecurityAuditOutcome.Failed, TargetAccountId: workerId == Guid.Empty ? null : workerId, ReasonCode: reason),
            Context.RequestAborted);
        return AuthenticateResult.Fail("Invalid AWP Worker authentication.");
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
        var length = bytes.Length;
        while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n') length--;
        if (length < 32 || bytes.AsSpan(0, length).Contains((byte)'\r') || bytes.AsSpan(0, length).Contains((byte)'\n'))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException("The credential file is invalid.");
        }
        if (length == bytes.Length) return bytes;
        var result = bytes.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
