using System.Security.Cryptography;
using System.Text;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed record CreatePersonalAccessToken(
    string Name,
    Guid WorkspaceId,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset ExpiresAt);

public sealed class PersonalAccessTokenService(
    IPersonalAccessTokenStore tokens,
    IIdentityStore identities,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const string TokenPrefix = "agt_pat_";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(365);
    public static readonly IReadOnlySet<string> SupportedPermissions = new HashSet<string>(
        [
            AuthorizationPermissions.WorkspacesRead,
            AuthorizationPermissions.ResourcesRead,
            AuthorizationPermissions.ResourcesWrite,
            AuthorizationPermissions.ResourcesDelete,
            AuthorizationPermissions.RunsRead,
            AuthorizationPermissions.RunsExecute
        ],
        StringComparer.Ordinal);

    public async Task<IReadOnlyList<PersonalAccessToken>> ListCurrentAsync(CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        return await tokens.ListAsync(current.PrincipalId, cancellationToken);
    }

    public async Task<IReadOnlyList<PersonalAccessToken>> ListAsPlatformAdministratorAsync(
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        if (!await platformAuthorization.IsPlatformAdministratorAsync(current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        if (await identities.GetPrincipalAsync(principalId, cancellationToken) is null)
            throw new ArgumentException("The Principal does not exist.", nameof(principalId));
        return await tokens.ListAsync(principalId, cancellationToken);
    }

    public async Task<CreatedPersonalAccessToken> CreateAsync(
        CreatePersonalAccessToken request,
        CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        var principal = await identities.GetPrincipalAsync(current.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The current Principal does not exist.");
        if (principal.Status != PrincipalStatus.Active || principal.Kind != PrincipalKind.Human)
            throw new InvalidOperationException("Personal access tokens require an active human Principal.");

        var workspace = await identities.GetWorkspaceAsync(current.TenantId, request.WorkspaceId, cancellationToken)
            ?? throw new ArgumentException("The selected Workspace does not exist in the current Tenant.", nameof(request));
        if (workspace.Status != WorkspaceStatus.Active)
            throw new ArgumentException("The selected Workspace is disabled.", nameof(request));

        var name = request.Name.Trim();
        if (name.Length is < 2 or > 80)
            throw new ArgumentException("PAT names must contain 2-80 characters.", nameof(request));
        var permissions = request.Permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (permissions.Length == 0 || permissions.Any(permission => !SupportedPermissions.Contains(permission)))
            throw new ArgumentException("The PAT contains an unsupported permission.", nameof(request));

        var candidateContext = new RequestContext(current.PrincipalId, current.TenantId, workspace.Id);
        var effectivePermissions = await authorization.GetPermissionsAsync(candidateContext, cancellationToken);
        if (permissions.Any(permission => !effectivePermissions.Contains(permission)))
            throw new AuthorizationDeniedException("personal-access-token/permissions");

        var now = timeProvider.GetUtcNow();
        if (request.ExpiresAt <= now || request.ExpiresAt > now.Add(MaximumLifetime))
            throw new ArgumentException("PAT expiration must be in the future and no more than 365 days away.", nameof(request));

        var id = Guid.NewGuid();
        var publicPrefix = $"{TokenPrefix}{id:N}";
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var metadata = new PersonalAccessToken(
            id,
            current.PrincipalId,
            workspace.Id,
            name,
            publicPrefix,
            permissions,
            now,
            request.ExpiresAt,
            null,
            null);
        await tokens.AddAsync(new PersonalAccessTokenCredential(metadata, HashSecret(secret)), cancellationToken);
        await audit.WriteAsync(new(
            SecurityAuditActions.PersonalAccessTokenCreated,
            TargetPrincipalId: current.PrincipalId,
            TenantId: current.TenantId,
            WorkspaceId: workspace.Id), cancellationToken);
        return new CreatedPersonalAccessToken(metadata, $"{publicPrefix}_{secret}");
    }

    public async Task<bool> RevokeCurrentAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        var revoked = await tokens.RevokeAsync(tokenId, current.PrincipalId, timeProvider.GetUtcNow(), cancellationToken);
        if (revoked)
            await AuditRevocationAsync(current.PrincipalId, current.TenantId, current.WorkspaceId, cancellationToken);
        return revoked;
    }

    public async Task<int> RevokeAllCurrentAsync(CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        var count = await tokens.RevokeAllAsync(current.PrincipalId, timeProvider.GetUtcNow(), cancellationToken);
        if (count > 0)
            await audit.WriteAsync(new(SecurityAuditActions.PersonalAccessTokensRevoked,
                TargetPrincipalId: current.PrincipalId, TenantId: current.TenantId, WorkspaceId: current.WorkspaceId), cancellationToken);
        return count;
    }

    public async Task<bool> RevokeAsPlatformAdministratorAsync(
        Guid principalId,
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        if (!await platformAuthorization.IsPlatformAdministratorAsync(current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        var revoked = await tokens.RevokeAsync(tokenId, principalId, timeProvider.GetUtcNow(), cancellationToken);
        if (revoked)
            await AuditRevocationAsync(principalId, current.TenantId, current.WorkspaceId, cancellationToken);
        return revoked;
    }

    public async Task<int> RevokeAllAsPlatformAdministratorAsync(
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        if (!await platformAuthorization.IsPlatformAdministratorAsync(current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        var count = await tokens.RevokeAllAsync(principalId, timeProvider.GetUtcNow(), cancellationToken);
        if (count > 0)
            await audit.WriteAsync(new(SecurityAuditActions.PersonalAccessTokensRevoked,
                TargetPrincipalId: principalId, TenantId: current.TenantId, WorkspaceId: current.WorkspaceId), cancellationToken);
        return count;
    }

    public static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private RequestContext RequireInteractiveContext()
    {
        if (!requestContext.IsInitialized) throw new AuthorizationDeniedException("authenticated");
        if (requestContext.Current.Restriction is not null)
            throw new AuthorizationDeniedException("interactive-user");
        return requestContext.Current;
    }

    private Task AuditRevocationAsync(Guid principalId, Guid tenantId, Guid workspaceId, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(SecurityAuditActions.PersonalAccessTokenRevoked,
            TargetPrincipalId: principalId, TenantId: tenantId, WorkspaceId: workspaceId), cancellationToken);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
