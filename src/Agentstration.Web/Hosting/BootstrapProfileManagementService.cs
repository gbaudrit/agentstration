using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Hosting;

public sealed record BootstrapTargetWorkspace(
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceDisplayName);

public sealed record BootstrapTargetTenant(Guid Id, string Name, string DisplayName);

public sealed record BootstrapManagementView(
    BootstrapCatalogSnapshot Catalog,
    IReadOnlyList<BootstrapTargetTenant> Tenants,
    IReadOnlyList<BootstrapTargetWorkspace> Workspaces,
    IReadOnlyList<BootstrapApplicationResource> Applications);

public sealed class BootstrapApplicationLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class BootstrapProfileManagementService(
    BootstrapProfileCatalog catalog,
    DeclarativeBootstrapService bootstrap,
    IIdentityStore identities,
    IPlatformAuthorizationService platformAuthorization,
    IRequestContextScopeFactory scopes,
    IControlPlaneStore store,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    BootstrapApplicationLock applicationLock)
{
    public async Task<BootstrapManagementView> GetAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var tenants = await identities.ListTenantsAsync(cancellationToken);
        var workspaces = new List<BootstrapTargetWorkspace>();
        foreach (var tenant in tenants.Where(tenant => tenant.Status == TenantStatus.Active))
        {
            var tenantWorkspaces = await identities.ListWorkspacesAsync(tenant.Id, cancellationToken);
            workspaces.AddRange(tenantWorkspaces
                .Where(workspace => workspace.Status == WorkspaceStatus.Active)
                .Select(workspace => new BootstrapTargetWorkspace(
                    tenant.Id,
                    tenant.Name,
                    tenant.DisplayName,
                    workspace.Id,
                    workspace.Name,
                    workspace.DisplayName)));
        }
        var applications = await ListApplicationsWithRecoveryAsync(cancellationToken);
        return new(
            await catalog.GetSnapshotAsync(cancellationToken),
            tenants.Where(tenant => tenant.Status == TenantStatus.Active)
                .Select(tenant => new BootstrapTargetTenant(tenant.Id, tenant.Name, tenant.DisplayName))
                .ToArray(),
            workspaces,
            applications);
    }

    public async Task<BootstrapCompositionPreview> PreviewAsync(
        BootstrapProfileSelection selection,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var targetContext = await ResolveTargetContextAsync(selection.Target, actorPrincipalId, cancellationToken);
        using var operationScope = PushTargetScope(targetContext);
        return await bootstrap.PreviewAsync(selection, cancellationToken);
    }

    public async Task<BootstrapApplicationResource?> GetApplicationAsync(
        string applicationId,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(applicationId)) return null;
        await applicationLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var systemScope = scopes.PushSystem();
            var application = (await store.GetAsync<BootstrapApplicationResource>(
                new(ResourceKinds.BootstrapApplication, applicationId),
                cancellationToken))?.Value;
            if (application?.Definition.Status != BootstrapApplicationStatus.Running) return application;
            return await SaveApplicationAsync(Interrupt(application), application.ETag, cancellationToken);
        }
        finally
        {
            applicationLock.Semaphore.Release();
        }
    }

    public async Task<BootstrapApplicationResource> ApplyAsync(
        BootstrapProfileSelection selection,
        string expectedDigest,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(expectedDigest))
            throw new DeclarativeBootstrapException("The digest returned by preview is required.");
        await applicationLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            BootstrapCompositionPreview preview;
            BootstrapExecutionResult execution;
            var targetContext = await ResolveTargetContextAsync(selection.Target, actorPrincipalId, cancellationToken);
            using (PushTargetScope(targetContext))
            {
                preview = await bootstrap.PreviewAsync(selection, cancellationToken);
                if (!string.Equals(preview.Digest, expectedDigest, StringComparison.Ordinal))
                    throw new DeclarativeBootstrapException("The bootstrap catalog or target changed after preview. Preview the application again.");
                if (!preview.CanApply)
                    throw new DeclarativeBootstrapException("The bootstrap preview contains invalid resources and cannot be applied.");

                var application = await CreateApplicationAsync(preview, actorPrincipalId, cancellationToken);
                try
                {
                    execution = await bootstrap.ExecuteAsync(selection, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    var interrupted = Interrupt(application);
                    _ = await SaveApplicationAsync(interrupted, application.ETag, CancellationToken.None);
                    await audit.WriteAsync(new(
                        SecurityAuditActions.BootstrapProfileApplied,
                        SecurityAuditOutcome.Failed,
                        ActorPrincipalId: actorPrincipalId,
                        TenantId: selection.Target?.TenantId,
                        WorkspaceId: selection.Target?.WorkspaceId,
                        ReasonCode: "application_interrupted"), CancellationToken.None);
                    throw;
                }
                var completed = Complete(application, execution);
                completed = await SaveApplicationAsync(completed, application.ETag, cancellationToken);
                await audit.WriteAsync(new(
                    SecurityAuditActions.BootstrapProfileApplied,
                    execution.Error is null ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed,
                    ActorPrincipalId: actorPrincipalId,
                    TenantId: selection.Target?.TenantId,
                    WorkspaceId: selection.Target?.WorkspaceId,
                    ReasonCode: execution.Error is null ? null : "application_failed"), CancellationToken.None);
                return completed;
            }
        }
        finally
        {
            applicationLock.Semaphore.Release();
        }
    }

    private async Task<BootstrapApplicationResource> CreateApplicationAsync(
        BootstrapCompositionPreview preview,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var resource = new BootstrapApplicationResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.BootstrapApplication,
            Metadata = new ResourceMetadata { Name = id.ToString("N") },
            TenantId = preview.Target?.TenantId ?? Guid.Empty,
            WorkspaceId = preview.Target?.WorkspaceId ?? Guid.Empty,
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
            Definition = new BootstrapApplicationProperties
            {
                Source = BootstrapApplicationSource.Manual,
                ActorPrincipalId = actorPrincipalId,
                Profiles = preview.Profiles.Select(profile => profile.Name).ToArray(),
                Scope = preview.Scope,
                Target = preview.Target,
                Bindings = preview.Bindings,
                Digest = preview.Digest,
                StartedAt = timeProvider.GetUtcNow()
            }
        };
        return await SaveApplicationAsync(resource, null, cancellationToken);
    }

    private BootstrapApplicationResource Complete(
        BootstrapApplicationResource application,
        BootstrapExecutionResult execution)
    {
        var createdBeforeFailure = execution.Resources.Any(resource => resource.Disposition == BootstrapResourceDisposition.Create);
        var status = execution.Error is null
            ? BootstrapApplicationStatus.Succeeded
            : createdBeforeFailure
                ? BootstrapApplicationStatus.PartiallyApplied
                : BootstrapApplicationStatus.Failed;
        return application with
        {
            Generation = checked(application.Generation + 1),
            Status = new ResourceStatus
            {
                ProvisioningState = execution.Error is null ? ProvisioningState.Succeeded : ProvisioningState.Failed
            },
            Definition = application.Definition with
            {
                CompletedAt = timeProvider.GetUtcNow(),
                Status = status,
                Error = execution.Error,
                Resources = execution.Resources
            }
        };
    }

    private BootstrapApplicationResource Interrupt(BootstrapApplicationResource application) => application with
    {
        Generation = checked(application.Generation + 1),
        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed },
        Definition = application.Definition with
        {
            CompletedAt = timeProvider.GetUtcNow(),
            Status = BootstrapApplicationStatus.Interrupted,
            Error = "The bootstrap application was interrupted before completion."
        }
    };

    private async Task<BootstrapApplicationResource> SaveApplicationAsync(
        BootstrapApplicationResource application,
        string? etag,
        CancellationToken cancellationToken)
    {
        using var systemScope = scopes.PushSystem();
        return (await store.PutAsync(application, etag, etag is null, cancellationToken)).Value;
    }

    private async Task<IReadOnlyList<BootstrapApplicationResource>> ListApplicationsWithRecoveryAsync(
        CancellationToken cancellationToken)
    {
        await applicationLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var systemScope = scopes.PushSystem();
            var applications = (await store.ListAllAsync<BootstrapApplicationResource>(
                ResourceKinds.BootstrapApplication,
                cancellationToken)).Select(value => value.Value).ToArray();
            var recovered = new List<BootstrapApplicationResource>(applications.Length);
            foreach (var application in applications)
            {
                recovered.Add(application.Definition.Status == BootstrapApplicationStatus.Running
                    ? await SaveApplicationAsync(Interrupt(application), application.ETag, cancellationToken)
                    : application);
            }
            return recovered.OrderByDescending(value => value.Definition.StartedAt).Take(100).ToArray();
        }
        finally
        {
            applicationLock.Semaphore.Release();
        }
    }

    private async Task<RequestContext?> ResolveTargetContextAsync(
        BootstrapApplicationTarget? target,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        if (target?.WorkspaceId is null)
        {
            if (target?.TenantId is { } tenantId)
            {
                var tenant = await identities.GetTenantAsync(tenantId, cancellationToken);
                if (tenant is null || tenant.Status != TenantStatus.Active)
                    throw new DeclarativeBootstrapException($"Bootstrap target Tenant '{tenantId}' is missing or inactive.");
            }
            return null;
        }
        if (target.TenantId is not { } targetTenantId)
            throw new DeclarativeBootstrapException("A Workspace bootstrap target requires its Tenant identifier.");
        var workspace = await identities.GetWorkspaceAsync(targetTenantId, target.WorkspaceId.Value, cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            throw new DeclarativeBootstrapException($"Bootstrap target Workspace '{target.WorkspaceId}' is missing, inactive, or outside Tenant '{targetTenantId}'.");
        var tenantForWorkspace = await identities.GetTenantAsync(targetTenantId, cancellationToken);
        if (tenantForWorkspace is null || tenantForWorkspace.Status != TenantStatus.Active)
            throw new DeclarativeBootstrapException($"Bootstrap target Tenant '{targetTenantId}' is missing or inactive.");
        return new RequestContext(actorPrincipalId, targetTenantId, workspace.Id);
    }

    private IDisposable PushTargetScope(RequestContext? context) =>
        context is null ? scopes.PushSystem() : scopes.Push(context);

    private async Task EnsurePlatformAdministratorAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (!await platformAuthorization.IsPlatformAdministratorAsync(actorPrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
    }
}
