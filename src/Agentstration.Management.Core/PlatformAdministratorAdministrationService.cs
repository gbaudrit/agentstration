using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed record PlatformAdministratorView(Principal Principal, PlatformAdministrator Grant);

public sealed class PlatformAdministratorAdministrationService(
    IIdentityStore store,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService authorization,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    PlatformAdministratorLifecycleLock lifecycleLock) : IPlatformAdministratorPolicy
{
    public async Task<IReadOnlyList<PlatformAdministratorView>> ListAsync(CancellationToken cancellationToken)
    {
        await RequireActorAsync(cancellationToken);
        var result = new List<PlatformAdministratorView>();
        foreach (var grant in await store.ListPlatformAdministratorsAsync(cancellationToken))
        {
            var principal = await store.GetPrincipalAsync(grant.PrincipalId, cancellationToken)
                ?? throw new InvalidOperationException($"Platform administrator '{grant.PrincipalId:D}' references a missing Principal.");
            result.Add(new(principal, grant));
        }
        return result.OrderBy(value => value.Principal.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PlatformAdministratorView> GrantAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await lifecycleLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await RequireActorAsync(cancellationToken);
            var principal = await store.GetPrincipalAsync(principalId, cancellationToken)
                ?? throw new ArgumentException("The Principal does not exist.", nameof(principalId));
            if (principal.Status != PrincipalStatus.Active)
                throw new InvalidOperationException("A disabled Principal cannot become a Platform administrator.");
            var existing = await store.GetPlatformAdministratorAsync(principalId, cancellationToken);
            if (existing is not null) return new(principal, existing);

            var grant = new PlatformAdministrator(principal.Id, timeProvider.GetUtcNow());
            await store.AddPlatformAdministratorAsync(grant, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.PlatformAdministratorGranted,
                TargetPrincipalId: principal.Id), cancellationToken);
            return new(principal, grant);
        }
        finally
        {
            lifecycleLock.Semaphore.Release();
        }
    }

    public async Task RevokeAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await lifecycleLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var actorId = await RequireActorAsync(cancellationToken);
            if (actorId == principalId)
                throw new InvalidOperationException("You cannot revoke your own Platform administrator grant.");
            var existing = await store.GetPlatformAdministratorAsync(principalId, cancellationToken);
            if (existing is null) return;
            var principal = await store.GetPrincipalAsync(principalId, cancellationToken)
                ?? throw new InvalidOperationException("The Platform administrator references a missing Principal.");
            if (principal.Status == PrincipalStatus.Active && await CountActiveAsync(cancellationToken) <= 1)
                throw new InvalidOperationException("The last active Platform administrator cannot be revoked.");

            await store.RemovePlatformAdministratorAsync(principalId, cancellationToken);
            await audit.WriteAsync(new(SecurityAuditActions.PlatformAdministratorRevoked,
                TargetPrincipalId: principalId), cancellationToken);
        }
        finally
        {
            lifecycleLock.Semaphore.Release();
        }
    }

    public async Task<IAsyncDisposable> AcquireDisableLeaseAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await lifecycleLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var actorId = await RequireActorAsync(cancellationToken);
            var isPlatformAdministrator = await store.GetPlatformAdministratorAsync(principalId, cancellationToken) is not null;
            if (isPlatformAdministrator && actorId == principalId)
                throw new InvalidOperationException("You cannot disable your own Platform administrator account.");
            if (isPlatformAdministrator && await CountActiveAsync(cancellationToken, principalId) == 0)
                throw new InvalidOperationException("The last active Platform administrator account cannot be disabled.");
            return new LifecycleLease(lifecycleLock.Semaphore);
        }
        catch
        {
            lifecycleLock.Semaphore.Release();
            throw;
        }
    }

    private async Task<Guid> RequireActorAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized
            || !await authorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
        var actorId = requestContext.Current.PrincipalId;
        if ((await store.GetPrincipalAsync(actorId, cancellationToken))?.Status != PrincipalStatus.Active)
            throw new AuthorizationDeniedException("platform/admin");
        return actorId;
    }

    private async Task<int> CountActiveAsync(CancellationToken cancellationToken, Guid? excluding = null)
    {
        var count = 0;
        foreach (var grant in await store.ListPlatformAdministratorsAsync(cancellationToken))
        {
            if (grant.PrincipalId == excluding) continue;
            if ((await store.GetPrincipalAsync(grant.PrincipalId, cancellationToken))?.Status == PrincipalStatus.Active) count++;
        }
        return count;
    }

    private sealed class LifecycleLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PlatformAdministratorLifecycleLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}
