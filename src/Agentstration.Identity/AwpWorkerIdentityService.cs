using System.Security.Cryptography;
using System.Text;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Security.Contracts;

namespace Agentstration.Identity;

public sealed class AwpWorkerIdentityException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record AwpWorkerCredentialMaterial(Guid CredentialId, SecretValue Secret) : IDisposable
{
    public void Dispose() => Secret.Dispose();
}

public sealed class AwpWorkerIdentityService(
    IResourceStore store,
    IEnumerable<ISecretVaultProvider> vaultProviders,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);
    public const int MaximumPairingAttempts = 5;
    private const string Kind = "AwpWorkerIdentity";
    private const string VaultName = "awp-worker-credentials";

    public async Task<AwpWorkerEnrollmentView> AnnounceAsync(AnnounceAwpWorkerRequest request, CancellationToken cancellationToken)
    {
        ValidateAnnouncement(request);
        using var scope = RequestScopes().PushSystem();
        var address = Address(request.WorkerId);
        var existing = await store.GetExactAsync<AwpWorkerIdentityResource>(address, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.Definition.DisplayName, request.DisplayName.Trim(), StringComparison.Ordinal)
                || !string.Equals(existing.Value.Definition.ProtocolVersion, request.ProtocolVersion, StringComparison.Ordinal))
                throw new AwpWorkerIdentityException("worker_mismatch", "The Worker identity is already bound to different metadata.", 409);
            return View(existing.Value);
        }

        var resource = new AwpWorkerIdentityResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = Kind,
            Metadata = new ResourceMetadata { Name = request.WorkerId.ToString("N") },
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Status = Succeeded(),
            Definition = new()
            {
                WorkerId = request.WorkerId,
                DisplayName = request.DisplayName.Trim(),
                ProtocolVersion = request.ProtocolVersion,
                State = AwpWorkerEnrollmentState.Pending,
                AnnouncedAt = timeProvider.GetUtcNow()
            }
        };
        try { _ = await store.PutExactAsync(ResourceScopeRef.Instance, resource, null, true, cancellationToken); }
        catch (ResourceConcurrencyException) { return await AnnounceAsync(request, cancellationToken); }
        await AuditAsync(SecurityAuditActions.AwpWorkerEnrollmentAnnounced, request.WorkerId, null, cancellationToken);
        return View(resource);
    }

    public async Task<IReadOnlyList<AwpWorkerEnrollmentView>> ListAsync(CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        return (await store.ListExactAsync<AwpWorkerIdentityResource>(ResourceScopeRef.Instance, Kind, 0, 500, cancellationToken))
            .Select(value => View(value.Value))
            .OrderBy(value => value.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AwpWorkerPairingCode> IssuePairingCodeAsync(Guid workerId, CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await GetAsync(workerId, cancellationToken);
        if (stored.Value.Definition.State == AwpWorkerEnrollmentState.Active)
            throw new AwpWorkerIdentityException("already_enrolled", "The Worker is already enrolled.", 409);
        var code = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var salt = RandomNumberGenerator.GetBytes(16);
        var now = timeProvider.GetUtcNow();
        var definition = stored.Value.Definition with
        {
            State = AwpWorkerEnrollmentState.PairingCodeIssued,
            PairingCodeSalt = Convert.ToBase64String(salt),
            PairingCodeDigest = Digest(code, salt),
            PairingCodeExpiresAt = now.Add(PairingCodeLifetime),
            PairingAttempts = 0
        };
        CryptographicOperations.ZeroMemory(salt);
        _ = await UpdateAsync(stored, definition, cancellationToken);
        await AuditAsync(SecurityAuditActions.AwpWorkerPairingCodeIssued, workerId, null, cancellationToken);
        return new(workerId, code, definition.PairingCodeExpiresAt.Value);
    }

    public async Task<AwpWorkerCredential> ClaimAsync(ClaimAwpWorkerRequest request, string instanceId, CancellationToken cancellationToken)
    {
        if (request.WorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64)
            throw new AwpWorkerIdentityException("invalid_claim", "The pairing claim is invalid.");
        using var scope = RequestScopes().PushSystem();
        var stored = await GetAsync(request.WorkerId, cancellationToken);
        var definition = stored.Value.Definition;
        if (definition.State != AwpWorkerEnrollmentState.PairingCodeIssued
            || definition.PairingCodeExpiresAt is null
            || string.IsNullOrWhiteSpace(definition.PairingCodeSalt)
            || string.IsNullOrWhiteSpace(definition.PairingCodeDigest))
            throw new AwpWorkerIdentityException("code_unavailable", "The pairing code is not active.", 409);
        if (timeProvider.GetUtcNow() >= definition.PairingCodeExpiresAt)
        {
            _ = await UpdateAsync(stored, ClearPairing(definition) with { State = AwpWorkerEnrollmentState.Pending }, cancellationToken);
            await AuditFailureAsync(request.WorkerId, "code_expired", cancellationToken);
            throw new AwpWorkerIdentityException("code_expired", "The pairing code has expired.", 410);
        }
        var salt = Convert.FromBase64String(definition.PairingCodeSalt);
        var valid = FixedEquals(definition.PairingCodeDigest, Digest(request.Code, salt));
        CryptographicOperations.ZeroMemory(salt);
        if (!valid)
        {
            var attempts = checked(definition.PairingAttempts + 1);
            var exhausted = attempts >= MaximumPairingAttempts;
            _ = await UpdateAsync(stored, exhausted
                ? ClearPairing(definition) with { State = AwpWorkerEnrollmentState.Pending, PairingAttempts = attempts }
                : definition with { PairingAttempts = attempts }, cancellationToken);
            await AuditFailureAsync(request.WorkerId, exhausted ? "attempts_exceeded" : "code_invalid", cancellationToken);
            throw new AwpWorkerIdentityException(exhausted ? "attempts_exceeded" : "code_invalid",
                "The pairing code is invalid.", exhausted ? 429 : 422);
        }

        return await ReplaceCredentialAsync(stored, instanceId, SecurityAuditActions.AwpWorkerCredentialIssued, cancellationToken);
    }

    public async Task<AwpWorkerCredential> RotateAsync(Guid workerId, string instanceId, CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await GetAsync(workerId, cancellationToken);
        if (stored.Value.Definition.State != AwpWorkerEnrollmentState.Active)
            throw new AwpWorkerIdentityException("worker_inactive", "The Worker identity is not active.", 409);
        return await ReplaceCredentialAsync(stored, instanceId, SecurityAuditActions.AwpWorkerCredentialRotated, cancellationToken);
    }

    public async Task RevokeAsync(Guid workerId, Guid credentialId, CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await GetAsync(workerId, cancellationToken);
        var definition = stored.Value.Definition;
        var credential = definition.Credentials.SingleOrDefault(value => value.CredentialId == credentialId);
        if (credential is null)
            throw new AwpWorkerIdentityException("credential_not_found", "The Worker credential was not found.", 404);
        if (credential.Revoked) return;
        var credentials = definition.Credentials
            .Select(value => value.CredentialId == credentialId ? value with { Revoked = true } : value)
            .ToArray();
        _ = await UpdateAsync(stored, definition with
        {
            State = credentials.All(value => value.Revoked) ? AwpWorkerEnrollmentState.Revoked : AwpWorkerEnrollmentState.Active,
            Credentials = credentials
        }, cancellationToken);
        await Vault().DeleteAsync(VaultContext(), credential.SecretName, cancellationToken);
        await AuditAsync(SecurityAuditActions.AwpWorkerCredentialRevoked, workerId, credentialId.ToString("D"), cancellationToken);
    }

    public async Task<AwpWorkerCredentialMaterial?> GetCredentialAsync(Guid workerId, Guid credentialId, CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await store.GetExactAsync<AwpWorkerIdentityResource>(Address(workerId), cancellationToken);
        var definition = stored?.Value.Definition;
        var credential = definition?.Credentials.SingleOrDefault(value => value.CredentialId == credentialId && !value.Revoked);
        if (definition is null || definition.State != AwpWorkerEnrollmentState.Active || credential is null)
            return null;
        var secret = await Vault().GetAsync(VaultContext(), credential.SecretName, cancellationToken);
        return secret is null ? null : new(credentialId, secret);
    }

    public async Task<bool> IsSessionActiveAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await store.GetExactAsync<AwpWorkerIdentityResource>(Address(workerId), cancellationToken);
        return stored?.Value.Definition.State == AwpWorkerEnrollmentState.Active
            && stored.Value.Definition.ActiveSessionId == sessionId;
    }

    public async Task ActivateSessionAsync(Guid workerId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (workerId == Guid.Empty || sessionId == Guid.Empty)
            throw new AwpWorkerIdentityException("invalid_session", "The Worker session is invalid.");
        using var scope = RequestScopes().PushSystem();
        var stored = await store.GetExactAsync<AwpWorkerIdentityResource>(Address(workerId), cancellationToken);
        if (stored is null)
        {
            var now = timeProvider.GetUtcNow();
            var configured = new AwpWorkerIdentityResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = Kind,
                Metadata = new ResourceMetadata { Name = workerId.ToString("N") },
                ScopeRef = ResourceScopeRef.Instance,
                Generation = 1,
                Status = Succeeded(),
                Definition = new()
                {
                    WorkerId = workerId,
                    DisplayName = $"Configured AWP Worker {workerId:D}",
                    ProtocolVersion = AwpWorkerAuthentication.ProtocolVersion,
                    State = AwpWorkerEnrollmentState.Active,
                    AnnouncedAt = now,
                    ActiveSessionId = sessionId,
                    ActiveSessionStartedAt = now
                }
            };
            try { _ = await store.PutExactAsync(ResourceScopeRef.Instance, configured, null, true, cancellationToken); }
            catch (ResourceConcurrencyException) { await ActivateSessionAsync(workerId, sessionId, cancellationToken); return; }
            await AuditAsync(SecurityAuditActions.AwpWorkerSessionStarted, workerId, $"session:{sessionId:N}", cancellationToken);
            return;
        }
        if (stored.Value.Definition.State != AwpWorkerEnrollmentState.Active)
            throw new AwpWorkerIdentityException("worker_inactive", "The Worker identity is not active.", 409);
        if (stored.Value.Definition.ActiveSessionId == sessionId) return;
        _ = await UpdateAsync(stored, stored.Value.Definition with
        {
            ActiveSessionId = sessionId,
            ActiveSessionStartedAt = timeProvider.GetUtcNow()
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.AwpWorkerSessionStarted, workerId, $"session:{sessionId:N}", cancellationToken);
    }

    private async Task<AwpWorkerCredential> ReplaceCredentialAsync(
        StoredResource<AwpWorkerIdentityResource> stored,
        string instanceId,
        string auditAction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new InvalidOperationException("The AWP instance identifier is not configured.");
        var credentialId = Guid.NewGuid();
        var secretName = $"awp-{stored.Value.Definition.WorkerId:N}-{credentialId:N}";
        var secretBytes = Encoding.UTF8.GetBytes(Base64Url(RandomNumberGenerator.GetBytes(48)));
        try
        {
            using (var secret = new SecretValue(secretBytes))
                await Vault().SetAsync(VaultContext(), secretName, secret, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var updated = ClearPairing(stored.Value.Definition) with
            {
                State = AwpWorkerEnrollmentState.Active,
                Credentials = [.. stored.Value.Definition.Credentials, new AwpWorkerStoredCredential(credentialId, secretName, now, false)]
            };
            try { _ = await UpdateAsync(stored, updated, cancellationToken); }
            catch
            {
                await Vault().DeleteAsync(VaultContext(), secretName, cancellationToken);
                throw;
            }
            await AuditAsync(auditAction, updated.WorkerId, credentialId.ToString("D"), cancellationToken);
            return new(updated.WorkerId, credentialId, instanceId, Encoding.UTF8.GetString(secretBytes));
        }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
    }

    private async Task<StoredResource<AwpWorkerIdentityResource>> GetAsync(Guid workerId, CancellationToken cancellationToken) =>
        await store.GetExactAsync<AwpWorkerIdentityResource>(Address(workerId), cancellationToken)
        ?? throw new AwpWorkerIdentityException("worker_not_found", "The Worker enrollment was not found.", 404);

    private Task<StoredResource<AwpWorkerIdentityResource>> UpdateAsync(
        StoredResource<AwpWorkerIdentityResource> stored,
        AwpWorkerIdentityProperties definition,
        CancellationToken cancellationToken) => store.PutExactAsync(ResourceScopeRef.Instance, stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            Definition = definition
        }, stored.ETag, false, cancellationToken);

    private static AwpWorkerIdentityProperties ClearPairing(AwpWorkerIdentityProperties definition) => definition with
    {
        PairingCodeSalt = null,
        PairingCodeDigest = null,
        PairingCodeExpiresAt = null
    };

    private static void ValidateAnnouncement(AnnounceAwpWorkerRequest request)
    {
        if (request.WorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128
            || !string.Equals(request.ProtocolVersion, AwpWorkerAuthentication.ProtocolVersion, StringComparison.Ordinal))
            throw new AwpWorkerIdentityException("invalid_announcement", "The Worker announcement is invalid or incompatible.");
    }

    private static string Digest(string code, byte[] salt) =>
        Convert.ToBase64String(SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(code)).ToArray()));

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ScopedResourceAddress Address(Guid workerId) =>
        ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, Kind, workerId.ToString("N"));

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static AwpWorkerEnrollmentView View(AwpWorkerIdentityResource value) => new(
        value.Definition.WorkerId,
        value.Definition.DisplayName,
        value.Definition.ProtocolVersion,
        value.Definition.State,
        value.Definition.AnnouncedAt,
        value.Definition.Credentials.Count == 0 ? null : value.Definition.Credentials.Max(credential => credential.IssuedAt),
        value.Definition.ActiveSessionId,
        value.Definition.ActiveSessionStartedAt);

    private ISecretVaultProvider Vault() =>
        vaultProviders.Single(value => string.Equals(value.ProviderType, "local", StringComparison.OrdinalIgnoreCase));
    private static SecretVaultContext VaultContext() => new(
        ResourceScopeRef.Instance,
        ResourceAddress.Create(ResourceNamespace.Default, "Vault", VaultName),
        new Dictionary<string, System.Text.Json.JsonElement>());
    private IRequestContextScopeFactory RequestScopes() => requestContext as IRequestContextScopeFactory
        ?? throw new InvalidOperationException("AWP Worker identity requires a mutable request context.");
    private Task AuditAsync(string action, Guid workerId, string? reason, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action, TargetAccountId: workerId, ReasonCode: reason), cancellationToken);
    private Task AuditFailureAsync(Guid workerId, string reason, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(SecurityAuditActions.AwpWorkerEnrollmentFailed, SecurityAuditOutcome.Failed,
            TargetAccountId: workerId, ReasonCode: reason), cancellationToken);
}

internal sealed record AwpWorkerIdentityProperties
{
    public required Guid WorkerId { get; init; }
    public required string DisplayName { get; init; }
    public required string ProtocolVersion { get; init; }
    public AwpWorkerEnrollmentState State { get; init; }
    public DateTimeOffset AnnouncedAt { get; init; }
    public string? PairingCodeSalt { get; init; }
    public string? PairingCodeDigest { get; init; }
    public DateTimeOffset? PairingCodeExpiresAt { get; init; }
    public int PairingAttempts { get; init; }
    public IReadOnlyList<AwpWorkerStoredCredential> Credentials { get; init; } = [];
    public Guid? ActiveSessionId { get; init; }
    public DateTimeOffset? ActiveSessionStartedAt { get; init; }
}

internal sealed record AwpWorkerStoredCredential(Guid CredentialId, string SecretName, DateTimeOffset IssuedAt, bool Revoked);

internal sealed record AwpWorkerIdentityResource : Resource
{
    public AwpWorkerIdentityProperties Definition { get; init; } = null!;
}
