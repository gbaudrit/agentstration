using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed record LinkExternalIdentityRequest(string Issuer, string Subject);

public sealed class ExternalIdentityAdministrationService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService authorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    ExternalIdentityLifecycleLock lifecycleLock)
{
    public async Task<IReadOnlyList<ExternalIdentity>> ListAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await RequireActorAsync(cancellationToken);
        _ = await RequirePrincipalAsync(principalId, cancellationToken);
        return await store.ListExternalIdentitiesAsync(principalId, cancellationToken);
    }

    public async Task<ExternalIdentity> LinkAsync(
        Guid principalId,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        await lifecycleLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await RequireActorAsync(cancellationToken);
            Validate(issuer, subject);
            var principal = await RequirePrincipalAsync(principalId, cancellationToken);
            if (principal.Status != PrincipalStatus.Active)
                throw new InvalidOperationException("An external identity cannot be linked to a disabled Principal.");
            if (principal.Kind != PrincipalKind.Human)
                throw new InvalidOperationException("Interactive external identities can only be linked to human Principals.");

            var existing = await store.FindExternalIdentityAsync(issuer, subject, cancellationToken);
            if (existing?.PrincipalId == principalId) return existing;
            if (existing is not null)
                throw new ControlPlaneConcurrencyException("The external identity is already linked to another Principal.");

            var identity = new ExternalIdentity(Guid.NewGuid(), issuer, subject, principalId, timeProvider.GetUtcNow());
            await store.AddExternalIdentityAsync(identity, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.ExternalIdentityLinked, TargetPrincipalId: principalId), cancellationToken);
            return identity;
        }
        finally
        {
            lifecycleLock.Semaphore.Release();
        }
    }

    public async Task UnlinkAsync(Guid principalId, Guid externalIdentityId, CancellationToken cancellationToken)
    {
        await lifecycleLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await RequireActorAsync(cancellationToken);
            _ = await RequirePrincipalAsync(principalId, cancellationToken);
            var identities = await store.ListExternalIdentitiesAsync(principalId, cancellationToken);
            if (identities.All(value => value.Id != externalIdentityId))
                throw new KeyNotFoundException("The external identity does not exist for this Principal.");
            if (identities.Count == 1
                && await store.FindLocalIdentityByPrincipalAsync(principalId, cancellationToken) is null)
                throw new InvalidOperationException("The last authentication identity of a Principal cannot be removed.");

            await store.RemoveExternalIdentityAsync(principalId, externalIdentityId, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.ExternalIdentityUnlinked, TargetPrincipalId: principalId), cancellationToken);
        }
        finally
        {
            lifecycleLock.Semaphore.Release();
        }
    }

    private async Task RequireActorAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized
            || !await authorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        if ((await store.GetPrincipalAsync(requestContext.Current.PrincipalId, cancellationToken))?.Status != PrincipalStatus.Active)
            throw new AuthorizationDeniedException("platform/admin");
    }

    private async Task<Principal> RequirePrincipalAsync(Guid principalId, CancellationToken cancellationToken) =>
        await store.GetPrincipalAsync(principalId, cancellationToken)
        ?? throw new ArgumentException("The Principal does not exist.", nameof(principalId));

    private static void Validate(string issuer, string subject)
    {
        if (string.IsNullOrWhiteSpace(issuer)) throw new ArgumentException("The issuer is required.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("The subject is required.", nameof(subject));
        if (!string.Equals(issuer, issuer.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The issuer cannot begin or end with whitespace.", nameof(issuer));
        if (!string.Equals(subject, subject.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The subject cannot begin or end with whitespace.", nameof(subject));
        if (issuer.Length is < 1 or > 2048 || issuer.Any(char.IsControl))
            throw new ArgumentException("The issuer must contain 1-2048 printable characters.", nameof(issuer));
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || (issuerUri.Scheme != Uri.UriSchemeHttps && issuerUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(issuerUri.UserInfo)
            || !string.IsNullOrEmpty(issuerUri.Query)
            || !string.IsNullOrEmpty(issuerUri.Fragment))
            throw new ArgumentException("The issuer must be an absolute HTTP(S) URI without user information, query, or fragment.", nameof(issuer));
        if (subject.Length is < 1 or > 512 || subject.Any(char.IsControl))
            throw new ArgumentException("The subject must contain 1-512 printable characters.", nameof(subject));
    }
}

public sealed class ExternalIdentityLifecycleLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}
