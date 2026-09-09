using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Management.Core;

public sealed class AepEnrollmentException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record AepPairingCodeResult(Guid RequestId, string Code, DateTimeOffset ExpiresAt);

public sealed class AepEnrollmentService(
    IControlPlaneStore store,
    IIdentityStore identities,
    IAuthorizationService authorization,
    IEnumerable<ISecretVaultProvider> vaultProviders,
    IHttpClientFactory httpClients,
    AepTransportSecurityOptions transportOptions,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumAttempts = 5;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    private const string VaultName = "aep-enrollment-vault";

    public async Task<AepEnrollmentAnnouncementResponse> AnnounceAsync(AepEnrollmentAnnouncement announcement, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().PushSystem();
        ValidateAnnouncement(announcement);
        var workspace = await identities.GetWorkspaceAsync(announcement.TenantId, announcement.WorkspaceId, cancellationToken);
        if (workspace?.Status != WorkspaceStatus.Active)
            throw new AepEnrollmentException("workspace_unavailable", "The enrollment workspace is unavailable.", 404);
        var scope = ResourceScopeRef.Workspace(announcement.WorkspaceId);
        var name = announcement.InstanceId.ToString("N");
        var existing = await store.GetExactAsync<AepEnrollmentRequestResource>(Address(scope, name), cancellationToken);
        if (existing is not null)
        {
            var definition = existing.Value.Definition;
            if (definition.TenantId != announcement.TenantId
                || !string.Equals(definition.ExtensionId, announcement.Extension.Id, StringComparison.Ordinal)
                || definition.Endpoint != Normalize(announcement.Endpoint)
                || definition.PairingUri != Normalize(announcement.PairingUri))
                throw new AepEnrollmentException("instance_mismatch", "The extension instance is already bound to another enrollment request.", 409);
            return new(announcement.InstanceId, State(definition.State));
        }

        var now = timeProvider.GetUtcNow();
        var resource = new AepEnrollmentRequestResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AepEnrollmentRequest,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            Definition = new AepEnrollmentRequestProperties
            {
                InstanceId = announcement.InstanceId,
                TenantId = announcement.TenantId,
                ExtensionId = announcement.Extension.Id.Trim(),
                ExtensionName = announcement.Extension.Name.Trim(),
                ExtensionVersion = announcement.Extension.Version.Trim(),
                Endpoint = Normalize(announcement.Endpoint),
                PairingUri = Normalize(announcement.PairingUri),
                State = AepEnrollmentState.Pending,
                AnnouncedAt = now
            }
        };
        try { _ = await store.PutExactAsync(scope, resource, null, true, cancellationToken); }
        catch (ControlPlaneConcurrencyException)
        {
            // A simultaneous restart announced the same deterministic instance.
            return await AnnounceAsync(announcement, cancellationToken);
        }
        await AuditAsync(SecurityAuditActions.AepEnrollmentAnnounced, resource, null, cancellationToken);
        return new(announcement.InstanceId, "pending");
    }

    public async Task<IReadOnlyList<AepEnrollmentRequestResource>> ListAsync(RequestContext context, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await AuthorizeAsync(context, cancellationToken);
        return (await store.ListExactAsync<AepEnrollmentRequestResource>(
            ResourceScopeRef.Workspace(context.WorkspaceId), ResourceKinds.AepEnrollmentRequest, 0, 200, cancellationToken))
            .Select(value => Sanitize(value.Value))
            .OrderByDescending(value => value.Definition.AnnouncedAt)
            .ToArray();
    }

    public async Task<AepPairingCodeResult> RotateAsync(RequestContext context, Guid requestId, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await AuthorizeAsync(context, cancellationToken);
        var stored = await GetAsync(context.WorkspaceId, requestId, cancellationToken);
        if (stored.Value.Definition.State is AepEnrollmentState.Available or AepEnrollmentState.CredentialIssued or AepEnrollmentState.Verifying)
            throw new AepEnrollmentException("already_consumed", "The enrollment request has already consumed a code.", 409);
        if (stored.Value.Definition.State is AepEnrollmentState.Rejected or AepEnrollmentState.Cancelled)
            throw new AepEnrollmentException("request_closed", "The enrollment request is closed.", 409);
        var code = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var salt = RandomNumberGenerator.GetBytes(16);
        var expires = timeProvider.GetUtcNow().Add(CodeLifetime);
        var definition = stored.Value.Definition with
        {
            State = AepEnrollmentState.CodeIssued,
            CodeIssuedAt = timeProvider.GetUtcNow(),
            CodeExpiresAt = expires,
            AttemptCount = 0,
            CodeSalt = Convert.ToBase64String(salt),
            CodeDigest = Digest(code, salt),
            Outcome = null
        };
        CryptographicOperations.ZeroMemory(salt);
        _ = await UpdateAsync(stored, definition, cancellationToken);
        await AuditAsync(SecurityAuditActions.AepPairingCodeIssued, stored.Value, null, cancellationToken);
        return new(requestId, code, expires);
    }

    public async Task CloseAsync(RequestContext context, Guid requestId, AepEnrollmentState state, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await AuthorizeAsync(context, cancellationToken);
        if (state is not (AepEnrollmentState.Rejected or AepEnrollmentState.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(state));
        var stored = await GetAsync(context.WorkspaceId, requestId, cancellationToken);
        if (stored.Value.Definition.State is AepEnrollmentState.Available or AepEnrollmentState.CredentialIssued or AepEnrollmentState.Verifying)
            throw new AepEnrollmentException("already_consumed", "The enrollment request has already consumed a code.", 409);
        _ = await UpdateAsync(stored, stored.Value.Definition with
        {
            State = state,
            CodeSalt = null,
            CodeDigest = null,
            Outcome = State(state)
        }, cancellationToken);
        await AuditAsync(
            state == AepEnrollmentState.Rejected ? SecurityAuditActions.AepEnrollmentRejected : SecurityAuditActions.AepEnrollmentCancelled,
            stored.Value,
            null,
            cancellationToken);
    }

    public async Task<AepEnrollmentCredential> ClaimAsync(AepEnrollmentClaim claim, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().PushSystem();
        if (claim.RequestId == Guid.Empty || claim.InstanceId != claim.RequestId || claim.WorkspaceId == Guid.Empty
            || string.IsNullOrWhiteSpace(claim.Code) || claim.Code.Length > 64)
            throw new AepEnrollmentException("invalid_claim", "The enrollment claim is invalid.");
        var stored = await GetAsync(claim.WorkspaceId, claim.RequestId, cancellationToken);
        var definition = stored.Value.Definition;
        if (definition.InstanceId != claim.InstanceId)
            throw new AepEnrollmentException("request_mismatch", "The code is not bound to this extension instance.", 409);
        if (definition.State != AepEnrollmentState.CodeIssued || definition.CodeExpiresAt is null
            || string.IsNullOrWhiteSpace(definition.CodeSalt) || string.IsNullOrWhiteSpace(definition.CodeDigest))
            throw new AepEnrollmentException(ClosedCode(definition.State), "The pairing code is not active.", 409);
        if (timeProvider.GetUtcNow() >= definition.CodeExpiresAt)
        {
            _ = await UpdateAsync(stored, definition with { State = AepEnrollmentState.Expired, CodeSalt = null, CodeDigest = null, Outcome = "expired" }, cancellationToken);
            throw new AepEnrollmentException("code_expired", "The pairing code has expired.", 410);
        }
        var salt = Convert.FromBase64String(definition.CodeSalt);
        var matches = FixedEquals(definition.CodeDigest, Digest(claim.Code, salt));
        CryptographicOperations.ZeroMemory(salt);
        if (!matches)
        {
            var attempts = checked(definition.AttemptCount + 1);
            var exhausted = attempts >= MaximumAttempts;
            _ = await UpdateAsync(stored, definition with
            {
                AttemptCount = attempts,
                State = exhausted ? AepEnrollmentState.AttemptsExceeded : definition.State,
                CodeSalt = exhausted ? null : definition.CodeSalt,
                CodeDigest = exhausted ? null : definition.CodeDigest,
                Outcome = exhausted ? "attempts_exceeded" : null
            }, cancellationToken);
            throw new AepEnrollmentException(exhausted ? "attempts_exceeded" : "code_invalid", "The pairing code is invalid.", exhausted ? 429 : 422);
        }

        var clientId = $"agentstration:{claim.WorkspaceId:D}:{claim.InstanceId:D}";
        var accessToken = GenerateToken();
        var completion = GenerateToken();
        var completionDigest = Sha256(completion);
        var consumed = definition with
        {
            State = AepEnrollmentState.CredentialIssued,
            CodeSalt = null,
            CodeDigest = null,
            CompletionDigest = completionDigest,
            Outcome = null
        };
        try { stored = await UpdateAsync(stored, consumed, cancellationToken); }
        catch (ControlPlaneConcurrencyException) { throw new AepEnrollmentException("code_consumed", "The pairing code was already consumed.", 409); }
        try
        {
            var names = await PersistCredentialAndRegistrationAsync(stored.Value, accessToken, cancellationToken);
            _ = await UpdateAsync(stored, consumed with { CredentialSecretName = names.Secret, RegistrationName = names.Registration }, cancellationToken);
            await AuditAsync(SecurityAuditActions.AepCredentialIssued, stored.Value, null, cancellationToken);
            return new(clientId, accessToken, completion);
        }
        catch
        {
            try { _ = await UpdateAsync(stored, consumed with { State = AepEnrollmentState.VerificationFailed, Outcome = "credential_persistence_failed" }, cancellationToken); }
            catch (ControlPlaneConcurrencyException) { }
            throw;
        }
    }

    public async Task<AepEnrollmentReadyResponse> ReadyAsync(AepEnrollmentReady ready, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().PushSystem();
        var stored = await GetAsync(ready.WorkspaceId, ready.RequestId, cancellationToken);
        var definition = stored.Value.Definition;
        if (definition.InstanceId != ready.InstanceId || definition.State != AepEnrollmentState.CredentialIssued
            || string.IsNullOrWhiteSpace(definition.CompletionDigest) || !FixedEquals(definition.CompletionDigest, Sha256(ready.CompletionToken)))
            throw new AepEnrollmentException("completion_invalid", "The enrollment completion proof is invalid.", 409);
        stored = await UpdateAsync(stored, definition with { State = AepEnrollmentState.Verifying }, cancellationToken);
        try
        {
            var token = await ReadTokenAsync(stored.Value, cancellationToken);
            using var http = httpClients.CreateClient("agentstration-aep");
            http.BaseAddress = definition.Endpoint;
            _ = await new AepClient(http, new StaticAepAccessTokenProvider(token), transportOptions, definition.ExtensionId)
                .GetManifestAsync(cancellationToken);
            _ = await UpdateAsync(stored, definition with
            {
                State = AepEnrollmentState.Available,
                CompletionDigest = null,
                Outcome = "available"
            }, cancellationToken);
            await AuditAsync(SecurityAuditActions.AepEnrollmentAvailable, stored.Value, null, cancellationToken);
            return new("available");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            try { _ = await UpdateAsync(stored, definition with { State = AepEnrollmentState.VerificationFailed, CompletionDigest = null, Outcome = "verification_failed" }, cancellationToken); }
            catch (ControlPlaneConcurrencyException) { }
            await AuditAsync(SecurityAuditActions.AepEnrollmentFailed, stored.Value, "verification_failed", cancellationToken, SecurityAuditOutcome.Failed);
            throw new AepEnrollmentException("verification_failed", "The authenticated extension manifest could not be verified.", 502);
        }
    }

    public async Task<AepCredentialLifecycleResponse> RotateCredentialAsync(RequestContext context, Guid requestId, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await AuthorizeAsync(context, cancellationToken);
        var stored = await GetAsync(context.WorkspaceId, requestId, cancellationToken);
        if (stored.Value.Definition.State != AepEnrollmentState.Available)
            throw new AepEnrollmentException("credential_not_active", "The enrollment credential is not active.", 409);
        stored = await UpdateAsync(stored, stored.Value.Definition with { State = AepEnrollmentState.Verifying, Outcome = "rotation_pending" }, cancellationToken);
        var oldToken = await ReadTokenAsync(stored.Value, cancellationToken);
        var newToken = GenerateToken();
        try
        {
            await SendLifecycleAsync(stored.Value, AepEnrollmentProtocol.CredentialRotationPath, oldToken,
                new AepCredentialRotation(stored.Value.Definition.InstanceId,
                    $"agentstration:{context.WorkspaceId:D}:{stored.Value.Definition.InstanceId:D}", newToken), cancellationToken);
            await WriteTokenAsync(stored.Value, newToken, cancellationToken);
            await VerifyAsync(stored.Value, newToken, cancellationToken);
            await SendLifecycleAsync(stored.Value, AepEnrollmentProtocol.PreviousCredentialRevocationPath, newToken, null, cancellationToken);
            _ = await UpdateAsync(stored, stored.Value.Definition with { State = AepEnrollmentState.Available, Outcome = "credential_rotated" }, cancellationToken);
            await AuditAsync(SecurityAuditActions.AepCredentialRotated, stored.Value, null, cancellationToken);
            return new("rotated");
        }
        catch (AepEnrollmentException exception)
        {
            try { _ = await UpdateAsync(stored, stored.Value.Definition with { State = AepEnrollmentState.VerificationFailed, Outcome = exception.Code }, cancellationToken); }
            catch (ControlPlaneConcurrencyException) { }
            await AuditAsync(SecurityAuditActions.AepEnrollmentFailed, stored.Value, exception.Code, cancellationToken, SecurityAuditOutcome.Failed);
            throw;
        }
    }

    public async Task RevokeCredentialAsync(RequestContext context, Guid requestId, CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await AuthorizeAsync(context, cancellationToken);
        var stored = await GetAsync(context.WorkspaceId, requestId, cancellationToken);
        if (stored.Value.Definition.State == AepEnrollmentState.Revoked) return;
        if (stored.Value.Definition.State is not (AepEnrollmentState.Available or AepEnrollmentState.Disabled or AepEnrollmentState.VerificationFailed))
            throw new AepEnrollmentException("credential_not_active", "The enrollment credential is not active.", 409);
        var token = await ReadTokenAsync(stored.Value, cancellationToken);
        await SendLifecycleAsync(stored.Value, AepEnrollmentProtocol.CredentialRevocationPath, token, null, cancellationToken);
        await DeleteTokenAndDisableRegistrationAsync(stored.Value, cancellationToken);
        _ = await UpdateAsync(stored, stored.Value.Definition with { State = AepEnrollmentState.Revoked, Outcome = "credential_revoked" }, cancellationToken);
        await AuditAsync(SecurityAuditActions.AepCredentialRevoked, stored.Value, null, cancellationToken);
    }

    private async Task<(string Secret, string Registration)> PersistCredentialAndRegistrationAsync(AepEnrollmentRequestResource request, string token, CancellationToken cancellationToken)
    {
        var scope = request.ScopeRef ?? throw new InvalidOperationException("The enrollment request is unscoped.");
        var provider = vaultProviders.Single(value => string.Equals(value.ProviderType, "local", StringComparison.OrdinalIgnoreCase));
        var vaultAddress = ResourceAddress.Create(ResourceNamespace.Default, ResourceKinds.Vault, VaultName);
        var vaultContext = new SecretVaultContext(scope, vaultAddress, new Dictionary<string, System.Text.Json.JsonElement>());
        if (provider is ISecretVaultInitializer initializer) _ = await initializer.InitializeAsync(vaultContext, cancellationToken);
        var existingVault = await store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ResourceKinds.Vault, VaultName), cancellationToken);
        if (existingVault is null)
        {
            _ = await store.PutExactAsync(scope, new VaultResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Vault,
                Metadata = new ResourceMetadata { Name = VaultName },
                ScopeRef = scope,
                Generation = 1,
                Status = Succeeded(),
                Definition = new VaultProperties { DisplayName = "AEP enrollment credentials", ProviderType = "local" }
            }, null, true, cancellationToken);
        }
        var secretName = $"aep-{request.Definition.InstanceId:N}";
        var secretAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ResourceKinds.Secret, secretName);
        if (await store.GetExactAsync<SecretResource>(secretAddress, cancellationToken) is null)
        {
            _ = await store.PutExactAsync(scope, new SecretResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Secret,
                Metadata = new ResourceMetadata { Name = secretName },
                ScopeRef = scope,
                Generation = 1,
                Status = Succeeded(),
                Definition = new SecretProperties
                {
                    DisplayName = $"{request.Definition.ExtensionName} AEP credential",
                    Vault = new ResourceReference(VaultName, scope, ResourceNamespace.Default),
                    Key = secretName
                }
            }, null, true, cancellationToken);
        }
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            using var value = new SecretValue(tokenBytes);
            await provider.SetAsync(vaultContext, secretName, value, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
        var registrationName = $"paired-{request.Definition.InstanceId:N}";
        var registrationAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ResourceKinds.ExtensionRegistration, registrationName);
        if (await store.GetExactAsync<ExtensionRegistrationResource>(registrationAddress, cancellationToken) is null)
        {
            _ = await store.PutExactAsync(scope, new ExtensionRegistrationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = registrationName },
                ScopeRef = scope,
                Generation = 1,
                Status = Succeeded(),
                Definition = new ExtensionRegistrationProperties
                {
                    DisplayName = request.Definition.ExtensionName,
                    Endpoint = request.Definition.Endpoint,
                    ExpectedExtensionId = request.Definition.ExtensionId,
                    Source = ExtensionRegistrationSource.Manual,
                    AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                    EnrollmentMode = AepEnrollmentMode.PairingCode,
                    Credential = new ResourceReference(secretName, scope, ResourceNamespace.Default)
                }
            }, null, true, cancellationToken);
        }
        return (secretName, registrationName);
    }

    private async Task<string> ReadTokenAsync(AepEnrollmentRequestResource request, CancellationToken cancellationToken)
    {
        var scope = request.ScopeRef ?? throw new InvalidOperationException("The enrollment request is unscoped.");
        var provider = vaultProviders.Single(value => string.Equals(value.ProviderType, "local", StringComparison.OrdinalIgnoreCase));
        var secretName = request.Definition.CredentialSecretName
            ?? throw new InvalidOperationException("The enrollment credential reference is unavailable.");
        using var value = await provider.GetAsync(new SecretVaultContext(
            scope,
            ResourceAddress.Create(ResourceNamespace.Default, ResourceKinds.Vault, VaultName),
            new Dictionary<string, System.Text.Json.JsonElement>()), secretName, cancellationToken)
            ?? throw new InvalidOperationException("The enrollment credential is unavailable.");
        return Encoding.UTF8.GetString(value.AccessValue().Span);
    }

    private async Task WriteTokenAsync(AepEnrollmentRequestResource request, string token, CancellationToken cancellationToken)
    {
        var provider = EnrollmentVaultProvider();
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            using var value = new SecretValue(tokenBytes);
            await provider.SetAsync(EnrollmentVaultContext(request), CredentialName(request), value, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
    }

    private async Task DeleteTokenAndDisableRegistrationAsync(AepEnrollmentRequestResource request, CancellationToken cancellationToken)
    {
        await EnrollmentVaultProvider().DeleteAsync(EnrollmentVaultContext(request), CredentialName(request), cancellationToken);
        var scope = request.ScopeRef ?? throw new InvalidOperationException("The enrollment request is unscoped.");
        var registrationName = request.Definition.RegistrationName
            ?? throw new InvalidOperationException("The enrollment registration reference is unavailable.");
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ResourceKinds.ExtensionRegistration, registrationName);
        var registration = await store.GetExactAsync<ExtensionRegistrationResource>(address, cancellationToken)
            ?? throw new InvalidOperationException("The enrollment registration is unavailable.");
        if (registration.Value.Definition.Enabled)
            _ = await store.PutExactAsync(scope, registration.Value with
            {
                Generation = checked(registration.Value.Generation + 1),
                Definition = registration.Value.Definition with { Enabled = false }
            }, registration.ETag, false, cancellationToken);
    }

    private async Task VerifyAsync(AepEnrollmentRequestResource request, string token, CancellationToken cancellationToken)
    {
        using var http = httpClients.CreateClient("agentstration-aep");
        http.BaseAddress = request.Definition.Endpoint;
        try
        {
            _ = await new AepClient(http, new StaticAepAccessTokenProvider(token), transportOptions, request.Definition.ExtensionId)
                .GetManifestAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new AepEnrollmentException("verification_failed", "The rotated credential could not verify the extension identity.", 502);
        }
    }

    private async Task SendLifecycleAsync(
        AepEnrollmentRequestResource request,
        string path,
        string token,
        object? body,
        CancellationToken cancellationToken)
    {
        using var http = httpClients.CreateClient("agentstration-aep");
        http.BaseAddress = request.Definition.Endpoint;
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) message.Content = JsonContent.Create(body, body.GetType(), options: AepProtocol.JsonOptions);
        HttpResponseMessage response;
        try { response = await http.SendAsync(message, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new AepEnrollmentException("extension_unreachable", "The extension enrollment endpoint is unreachable.", 502);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            var code = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "authentication_failed",
                System.Net.HttpStatusCode.Forbidden => "authorization_denied",
                System.Net.HttpStatusCode.NotFound => "protocol_incompatible",
                _ => "extension_unreachable"
            };
            throw new AepEnrollmentException(code, "The extension rejected the credential lifecycle operation.",
                response.StatusCode == System.Net.HttpStatusCode.NotFound ? 409 : 502);
        }
    }

    private ISecretVaultProvider EnrollmentVaultProvider() =>
        vaultProviders.Single(value => string.Equals(value.ProviderType, "local", StringComparison.OrdinalIgnoreCase));

    private static string CredentialName(AepEnrollmentRequestResource request) => request.Definition.CredentialSecretName
        ?? throw new InvalidOperationException("The enrollment credential reference is unavailable.");

    private static SecretVaultContext EnrollmentVaultContext(AepEnrollmentRequestResource request) => new(
        request.ScopeRef ?? throw new InvalidOperationException("The enrollment request is unscoped."),
        ResourceAddress.Create(ResourceNamespace.Default, ResourceKinds.Vault, VaultName),
        new Dictionary<string, System.Text.Json.JsonElement>());

    private Task AuditAsync(
        string action,
        AepEnrollmentRequestResource request,
        string? reasonCode,
        CancellationToken cancellationToken,
        SecurityAuditOutcome outcome = SecurityAuditOutcome.Succeeded) => audit.WriteAsync(new(
            action,
            outcome,
            TargetAccountId: request.Definition.InstanceId,
            TenantId: request.Definition.TenantId,
            WorkspaceId: request.ScopeRef?.Kind == ResourceScopeKind.Workspace ? request.ScopeRef.Value.TargetId : null,
            ReasonCode: reasonCode), cancellationToken);

    private async Task AuthorizeAsync(RequestContext context, CancellationToken cancellationToken) =>
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesWrite, cancellationToken);

    private IRequestContextScopeFactory RequestScopes() => requestContext as IRequestContextScopeFactory
        ?? throw new InvalidOperationException("AEP enrollment requires a mutable Control Plane request context.");

    private async Task<StoredResource<AepEnrollmentRequestResource>> GetAsync(Guid workspaceId, Guid requestId, CancellationToken cancellationToken) =>
        await store.GetExactAsync<AepEnrollmentRequestResource>(Address(ResourceScopeRef.Workspace(workspaceId), requestId.ToString("N")), cancellationToken)
        ?? throw new AepEnrollmentException("request_not_found", "The enrollment request was not found.", 404);

    private Task<StoredResource<AepEnrollmentRequestResource>> UpdateAsync(
        StoredResource<AepEnrollmentRequestResource> stored,
        AepEnrollmentRequestProperties definition,
        CancellationToken cancellationToken) => store.PutExactAsync(
            stored.Value.ScopeRef!.Value,
            stored.Value with { Definition = definition, Generation = checked(stored.Value.Generation + 1), Status = Succeeded() },
            stored.ETag,
            false,
            cancellationToken);

    private void ValidateAnnouncement(AepEnrollmentAnnouncement announcement)
    {
        if (announcement.InstanceId == Guid.Empty || announcement.TenantId == Guid.Empty || announcement.WorkspaceId == Guid.Empty
            || string.IsNullOrWhiteSpace(announcement.Extension.Id) || string.IsNullOrWhiteSpace(announcement.Extension.Name)
            || string.IsNullOrWhiteSpace(announcement.Extension.Version))
            throw new AepEnrollmentException("announcement_invalid", "The enrollment announcement is incomplete.");
        try { AepTransportSecurity.ValidateEndpoint(announcement.Endpoint, transportOptions); }
        catch (AepTransportSecurityException exception) { throw new AepEnrollmentException("endpoint_untrusted", exception.Message); }
        if (!announcement.PairingUri.IsAbsoluteUri || announcement.PairingUri.UserInfo.Length != 0
            || announcement.PairingUri.Query.Length != 0 || announcement.PairingUri.Fragment.Length != 0
            || !SameOrigin(announcement.Endpoint, announcement.PairingUri))
            throw new AepEnrollmentException("pairing_origin_mismatch", "The pairing URI must use the exact announced AEP origin and contain no query or fragment.");
    }

    private static AepEnrollmentRequestResource Sanitize(AepEnrollmentRequestResource resource) => resource with
    {
        Definition = resource.Definition with { CodeSalt = null, CodeDigest = null, CompletionDigest = null }
    };
    private static ScopedResourceAddress Address(ResourceScopeRef scope, string name) =>
        ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ResourceKinds.AepEnrollmentRequest, name);
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static Uri Normalize(Uri value) => new UriBuilder(value) { Host = value.IdnHost.ToLowerInvariant() }.Uri;
    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
    private static string Digest(string code, byte[] salt) => Sha256($"{Convert.ToBase64String(salt)}:{code}");
    private static string Sha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var digest = SHA256.HashData(bytes);
        try { return Convert.ToBase64String(digest); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(digest); }
    }
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool FixedEquals(string left, string right)
    {
        var a = Convert.FromBase64String(left);
        var b = Convert.FromBase64String(right);
        try { return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }
    private static string ClosedCode(AepEnrollmentState state) => state switch
    {
        AepEnrollmentState.Expired => "code_expired",
        AepEnrollmentState.AttemptsExceeded => "attempts_exceeded",
        AepEnrollmentState.Cancelled => "request_cancelled",
        AepEnrollmentState.Rejected => "request_rejected",
        _ => "code_consumed"
    };
    private static string State(AepEnrollmentState state) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}
