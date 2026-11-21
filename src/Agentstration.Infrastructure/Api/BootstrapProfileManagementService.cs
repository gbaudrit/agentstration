using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets;
using Agentstration.Security.Contracts;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Web.Hosting;

public sealed class BootstrapApplicationLock
{
    internal SemaphoreSlim Execution { get; } = new(1, 1);
    internal SemaphoreSlim Admission { get; } = new(1, 1);
    internal SemaphoreSlim State { get; } = new(1, 1);
    internal string Owner { get; } = Guid.NewGuid().ToString("N");
}

public sealed class BootstrapProfileManagementService(
    BootstrapProfileCatalog catalog,
    SourceBootstrapProfileLoader sourceProfiles,
    DeclarativeBootstrapService bootstrap,
    IIdentityStore identities,
    IPlatformAuthorizationService platformAuthorization,
    IRequestContextScopeFactory scopes,
    IResourceStore store,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    BootstrapApplicationLock applicationLock) : IBootstrapProfileApiService
{
    public async Task<BootstrapManagementView> GetAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var tenants = await identities.ListTenantsAsync(cancellationToken);
        var workspaces = new List<BootstrapTargetWorkspace>();
        foreach (var tenant in tenants.Where(tenant => tenant.Status == TenantStatus.Active
                     && !OperationalTopologyIdentities.IsSystemTenant(tenant)))
        {
            var tenantWorkspaces = await identities.ListWorkspacesAsync(tenant.Id, cancellationToken);
            workspaces.AddRange(tenantWorkspaces
                .Where(workspace => workspace.Status == WorkspaceStatus.Active
                    && !OperationalTopologyIdentities.IsOperationalWorkspace(workspace))
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
            tenants.Where(tenant => tenant.Status == TenantStatus.Active
                    && !OperationalTopologyIdentities.IsSystemTenant(tenant))
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

    public async Task<BootstrapProfileSummary> GetSourceProfileAsync(
        BootstrapSourceProfileSelection selection,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        await using var loaded = await sourceProfiles.LoadAsync(selection, cancellationToken);
        return loaded.Profiles.Single().Summary;
    }

    public async Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(
        BootstrapApplicationTarget? target,
        BootstrapBindingTargetKind targetKind,
        IReadOnlyList<string> profiles,
        Guid actorPrincipalId,
        CancellationToken cancellationToken,
        BootstrapSourceProfileSelection? source = null)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (target?.TenantId is null || target.WorkspaceId is null)
            throw new DeclarativeBootstrapException("Bootstrap binding targets require a Tenant and Workspace target.");
        var targetContext = await ResolveTargetContextAsync(target, actorPrincipalId, cancellationToken);
        using var operationScope = PushTargetScope(targetContext);
        var existing = targetKind switch
        {
            BootstrapBindingTargetKind.ModelProfile => Options(
                await store.ListAsync<ModelProfileResource>(ModelResourceKinds.ModelProfile, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.ModelProvider => Options(
                await store.ListAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.RuntimeProfile => Options(
                await store.ListAsync<RuntimeProfileResource>(RuntimeProfileResourceKinds.RuntimeProfile, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.ExtensionRegistration => Options(
                await store.ListAsync<ExtensionRegistrationResource>(ExtensionKinds.ExtensionRegistration, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.Tool => Options(
                await store.ListAsync<ToolResource>(ToolResourceKinds.Tool, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.ConnectedServiceType => Options(
                await store.ListAsync<ConnectedServiceTypeResource>(ConnectedServiceResourceKinds.ConnectedServiceType, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.StorageType => Options(
                await store.ListAsync<StorageTypeResource>(StorageResourceKinds.StorageType, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.DataSourceProfile => Options(
                await store.ListAsync<DataSourceProfileResource>(DataSourceResourceKinds.DataSourceProfile, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.Parameter => Options(
                await store.ListAsync<ParameterResource>(ParameterResourceKinds.Parameter, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            BootstrapBindingTargetKind.Secret => Options(
                await store.ListAsync<SecretResource>(SecretResourceKinds.Secret, 0, 1000, cancellationToken),
                resource => resource.Definition.DisplayName),
            _ => throw new DeclarativeBootstrapException($"Unsupported bootstrap binding target kind '{targetKind}'.")
        };
        var resourceKind = targetKind switch
        {
            BootstrapBindingTargetKind.ModelProfile => ModelResourceKinds.ModelProfile,
            BootstrapBindingTargetKind.ModelProvider => ModelResourceKinds.ModelProvider,
            BootstrapBindingTargetKind.RuntimeProfile => RuntimeProfileResourceKinds.RuntimeProfile,
            BootstrapBindingTargetKind.ExtensionRegistration => ExtensionKinds.ExtensionRegistration,
            BootstrapBindingTargetKind.Tool => ToolResourceKinds.Tool,
            BootstrapBindingTargetKind.ConnectedServiceType => ConnectedServiceResourceKinds.ConnectedServiceType,
            BootstrapBindingTargetKind.StorageType => StorageResourceKinds.StorageType,
            BootstrapBindingTargetKind.DataSourceProfile => DataSourceResourceKinds.DataSourceProfile,
            BootstrapBindingTargetKind.Parameter => ParameterResourceKinds.Parameter,
            BootstrapBindingTargetKind.Secret => SecretResourceKinds.Secret,
            _ => throw new DeclarativeBootstrapException($"Unsupported bootstrap binding target kind '{targetKind}'.")
        };
        await using var loadedSelection = source is null
            ? new LoadedBootstrapSelection(await catalog.LoadAsync(profiles, cancellationToken))
            : await sourceProfiles.LoadAsync(source, cancellationToken);
        var planned = loadedSelection.Profiles
            .SelectMany(profile => profile.Resources)
            .Where(source => string.Equals(source.Resource.Kind, resourceKind, StringComparison.Ordinal))
            .Select(source => new BootstrapBindingTargetOption(
                source.Resource.Metadata.Name,
                source.Resource.Metadata.Namespace.Value,
                source.Resource.Metadata.Name,
                Planned: true,
                ScopeRef: targetKind == BootstrapBindingTargetKind.Parameter
                    ? ResourceScopeRef.Tenant(target.TenantId.Value)
                    : null));
        return existing.Concat(planned)
            .GroupBy(option => (option.Namespace, option.Name))
            .Select(group => group.OrderBy(option => option.Planned).First())
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Namespace, StringComparer.Ordinal)
            .ThenBy(option => option.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<BootstrapApplicationResource?> GetApplicationAsync(
        string applicationId,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(applicationId)) return null;
        using var systemScope = scopes.PushSystem();
        var application = (await store.GetAsync<BootstrapApplicationResource>(
            new(BootstrapKinds.BootstrapApplication, applicationId), cancellationToken))?.Value;
        return await RecoverAsync(application, cancellationToken);
    }

    public async Task<BootstrapApplicationResource?> CancelAsync(string applicationId, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        await applicationLock.State.WaitAsync(cancellationToken);
        try
        {
            using var systemScope = scopes.PushSystem();
            var application = (await store.GetAsync<BootstrapApplicationResource>(new(BootstrapKinds.BootstrapApplication, applicationId), cancellationToken))?.Value;
            if (application?.Definition.Status != BootstrapApplicationStatus.Running || application.Definition.CancellationRequested) return application;
            return await SaveApplicationAsync(application with
            {
                Generation = checked(application.Generation + 1),
                Definition = application.Definition with { CancellationRequested = true }
            }, application.ETag, cancellationToken);
        }
        finally { applicationLock.State.Release(); }
    }

    public async Task<BootstrapApplicationResource> StartAsync(
        ApplyBootstrapProfilesRequest request,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ExpectedDigest) || request.RequestId == Guid.Empty)
            throw new DeclarativeBootstrapException("bootstrap_request_identity_required");
        if ((request.Profiles.Count == 0) == (request.Source is null) || request.Profiles.Count > catalog.MaximumSelectedProfiles)
            throw new DeclarativeBootstrapException("bootstrap_selection_invalid");
        _ = await ResolveTargetContextAsync(request.Target, actorPrincipalId, cancellationToken);
        var selection = new BootstrapProfileSelection(request.Profiles, request.Target, request.Bindings, request.Source);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Selection = selection, request.ExpectedDigest, Actor = actorPrincipalId })));
        await applicationLock.Admission.WaitAsync(cancellationToken);
        try
        {
            using var systemScope = scopes.PushSystem();
            var applications = await ListApplicationsWithRecoveryAsync(cancellationToken, limitHistory: false);
            var id = (request.RequestId ?? Guid.NewGuid()).ToString("N");
            // Look up the identity directly: retained history is intentionally bounded.
            var existing = (await store.GetAsync<BootstrapApplicationResource>(new(BootstrapKinds.BootstrapApplication, id), cancellationToken))?.Value;
            existing ??= applications.FirstOrDefault(value => request.RequestId is { } requestId && value.Definition.RequestIds.Contains(requestId));
            if (existing is not null)
            {
                if (existing.Definition.RequestFingerprint != fingerprint)
                    throw new DeclarativeBootstrapException("bootstrap_request_identity_conflict");
                return existing;
            }
            var running = applications.Where(value => value.Definition.Status == BootstrapApplicationStatus.Running).ToArray();
            var duplicate = running.FirstOrDefault(value => value.Definition.RequestFingerprint == fingerprint);
            if (duplicate is not null)
            {
                if (request.RequestId is not { } duplicateRequestId) return duplicate;
                await applicationLock.State.WaitAsync(cancellationToken);
                try
                {
                    duplicate = (await store.GetAsync<BootstrapApplicationResource>(new(BootstrapKinds.BootstrapApplication, duplicate.Metadata.Name), cancellationToken))!.Value;
                    if (duplicate.Definition.RequestIds.Count >= 128)
                        throw new DeclarativeBootstrapException("bootstrap_request_identity_limit");
                    return await SaveApplicationAsync(duplicate with
                    {
                        Generation = checked(duplicate.Generation + 1),
                        Definition = duplicate.Definition with { RequestIds = [.. duplicate.Definition.RequestIds, duplicateRequestId] }
                    }, duplicate.ETag, cancellationToken);
                }
                finally { applicationLock.State.Release(); }
            }
            if (running.Length >= 16)
                throw new DeclarativeBootstrapException("bootstrap_application_queue_full");
            var scope = request.Target?.WorkspaceId is not null ? BootstrapProfileScope.Workspace
                : request.Target?.TenantId is not null ? BootstrapProfileScope.Tenant : BootstrapProfileScope.Instance;
            return await SaveApplicationAsync(new BootstrapApplicationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = BootstrapKinds.BootstrapApplication,
                Metadata = new ResourceMetadata { Name = id },
                ScopeRef = scope switch
                {
                    BootstrapProfileScope.Workspace => ResourceScopeRef.Workspace(request.Target!.WorkspaceId!.Value),
                    BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(request.Target!.TenantId!.Value),
                    _ => ResourceScopeRef.Instance
                },
                Generation = 1,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
                Definition = new BootstrapApplicationProperties
                {
                    Source = BootstrapApplicationSource.Manual,
                    ActorPrincipalId = actorPrincipalId,
                    Profiles = request.Profiles,
                    Scope = scope,
                    Target = request.Target,
                    Bindings = request.Bindings ?? [],
                    Digest = request.ExpectedDigest,
                    StartedAt = timeProvider.GetUtcNow(),
                    Selection = selection,
                    ExecutionOwner = applicationLock.Owner,
                    RequestFingerprint = fingerprint,
                    RequestIds = [Guid.ParseExact(id, "N")]
                }
            }, null, cancellationToken);
        }
        finally { applicationLock.Admission.Release(); }
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
        await applicationLock.Execution.WaitAsync(cancellationToken);
        try
        {
            var context = await ResolveTargetContextAsync(selection.Target, actorPrincipalId, cancellationToken);
            using var operationScope = PushTargetScope(context);
            var preview = await bootstrap.PreviewAsync(selection, cancellationToken);
            if (!string.Equals(preview.Digest, expectedDigest, StringComparison.Ordinal))
                throw new DeclarativeBootstrapException("The bootstrap catalog or target changed after preview. Preview the application again.");
            if (!preview.CanApply)
                throw new DeclarativeBootstrapException("The bootstrap preview contains invalid resources and cannot be applied.");
            var application = await CreateApplicationAsync(preview, actorPrincipalId, cancellationToken);
            return await ExecuteApplicationAsync(application, selection, cancellationToken);
        }
        finally { applicationLock.Execution.Release(); }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await applicationLock.Execution.WaitAsync(cancellationToken);
        try
        {
            var applications = await ListApplicationsWithRecoveryAsync(cancellationToken);
            var application = applications.LastOrDefault(value => value.Definition.Status == BootstrapApplicationStatus.Running
                && value.Definition.Selection is not null);
            if (application is null) return false;
            _ = await ExecuteApplicationAsync(application, application.Definition.Selection!, cancellationToken, background: true);
            return true;
        }
        finally { applicationLock.Execution.Release(); }
    }

    private async Task<BootstrapApplicationResource> ExecuteApplicationAsync(
        BootstrapApplicationResource application,
        BootstrapProfileSelection selection,
        CancellationToken cancellationToken,
        bool background = false)
    {
        try
        {
            if (application.Definition.CancellationRequested) throw new OperationCanceledException();
            await EnsurePlatformAdministratorAsync(application.Definition.ActorPrincipalId!.Value, cancellationToken);
            var context = await ResolveTargetContextAsync(selection.Target, application.Definition.ActorPrincipalId.Value, cancellationToken);
            using var operationScope = PushTargetScope(context);
            var execution = await bootstrap.ExecuteAsync(selection, cancellationToken, application.Definition.Digest,
                async (progress, token) =>
                {
                    await applicationLock.State.WaitAsync(token);
                    try
                    {
                        using var systemScope = scopes.PushSystem();
                        application = (await store.GetAsync<BootstrapApplicationResource>(
                            new(BootstrapKinds.BootstrapApplication, application.Metadata.Name), token))!.Value;
                        application = await SaveApplicationAsync(application with
                        {
                            Generation = checked(application.Generation + 1),
                            Definition = application.Definition with
                            {
                                TotalResources = progress.Preview.Resources.Count,
                                Profiles = progress.Preview.Profiles.Select(profile => profile.Name).ToArray(),
                                Bindings = progress.Preview.Bindings,
                                SourceProvenance = progress.Preview.SourceProvenance,
                                Resources = progress.Resources,
                                CurrentResource = progress.CurrentResource
                            }
                        }, application.ETag, token);
                        if (application.Definition.CancellationRequested) throw new OperationCanceledException();
                    }
                    finally { applicationLock.State.Release(); }
                });
            await applicationLock.State.WaitAsync(CancellationToken.None);
            try
            {
                using var systemScope = scopes.PushSystem();
                application = (await store.GetAsync<BootstrapApplicationResource>(
                    new(BootstrapKinds.BootstrapApplication, application.Metadata.Name), CancellationToken.None))!.Value;
                if (application.Definition.CancellationRequested) throw new OperationCanceledException();
                application = await SaveApplicationAsync(Complete(application, execution), application.ETag, CancellationToken.None);
            }
            finally { applicationLock.State.Release(); }
        }
        catch (OperationCanceledException)
        {
            application = await SaveTerminalAsync(application.Metadata.Name, current =>
            {
                var interrupted = Interrupt(current);
                return current.Definition.CancellationRequested || (!background && cancellationToken.IsCancellationRequested)
                    ? interrupted with { Definition = interrupted.Definition with { Status = BootstrapApplicationStatus.Cancelled, Error = "bootstrap_application_cancelled" } }
                    : interrupted;
            });
            await WriteAuditAsync(application);
            if (!background) throw;
            return application;
        }
        catch (Exception)
        {
            // Persist only a safe diagnostic. Provider exceptions may contain resource contents.
            application = await SaveTerminalAsync(application.Metadata.Name, current => current with
            {
                Generation = checked(current.Generation + 1),
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Failed },
                Definition = current.Definition with
                {
                    Status = current.Definition.Resources.Any(value => value.Disposition == BootstrapResourceDisposition.Create)
                        ? BootstrapApplicationStatus.PartiallyApplied : BootstrapApplicationStatus.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    CurrentResource = null,
                    Error = "bootstrap_application_failed"
                }
            });
        }
        await WriteAuditAsync(application);
        return application;
    }

    private async Task<BootstrapApplicationResource> SaveTerminalAsync(string id, Func<BootstrapApplicationResource, BootstrapApplicationResource> transition)
    {
        await applicationLock.State.WaitAsync(CancellationToken.None);
        try
        {
            using var systemScope = scopes.PushSystem();
            var current = (await store.GetAsync<BootstrapApplicationResource>(new(BootstrapKinds.BootstrapApplication, id), CancellationToken.None))!.Value;
            return await SaveApplicationAsync(transition(current), current.ETag, CancellationToken.None);
        }
        finally { applicationLock.State.Release(); }
    }

    private Task WriteAuditAsync(BootstrapApplicationResource application) => audit.WriteAsync(new(
        SecurityAuditActions.BootstrapProfileApplied,
        application.Definition.Status == BootstrapApplicationStatus.Succeeded ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed,
        ActorPrincipalId: application.Definition.ActorPrincipalId,
        TenantId: application.Definition.Target?.TenantId,
        WorkspaceId: application.Definition.Target?.WorkspaceId,
        ReasonCode: application.Definition.Status == BootstrapApplicationStatus.Succeeded ? null : "application_incomplete"), CancellationToken.None);

    private async Task<BootstrapApplicationResource> CreateApplicationAsync(
        BootstrapCompositionPreview preview,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var resource = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = BootstrapKinds.BootstrapApplication,
            Metadata = new ResourceMetadata { Name = id.ToString("N") },
            ScopeRef = ApplicationScopeRef(preview),
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
                SourceProvenance = preview.SourceProvenance,
                Digest = preview.Digest,
                StartedAt = timeProvider.GetUtcNow(),
                TotalResources = preview.Resources.Count,
                ExecutionOwner = applicationLock.Owner
            }
        };
        return await SaveApplicationAsync(resource, null, cancellationToken);
    }

    private static ResourceScopeRef ApplicationScopeRef(BootstrapCompositionPreview preview) => preview.Scope switch
    {
        BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
        BootstrapProfileScope.Tenant when preview.Target?.TenantId is Guid tenantId => ResourceScopeRef.Tenant(tenantId),
        BootstrapProfileScope.Workspace when preview.Target?.WorkspaceId is Guid workspaceId => ResourceScopeRef.Workspace(workspaceId),
        _ => throw new DeclarativeBootstrapException($"Bootstrap scope '{preview.Scope}' requires an explicit target.")
    };

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
                Error = execution.Error is null ? null
                    : execution.Error == "bootstrap_resource_failed" ? "bootstrap_resource_failed" : "bootstrap_application_invalid",
                Profiles = execution.Preview.Profiles.Select(profile => profile.Name).ToArray(),
                Bindings = execution.Preview.Bindings,
                Resources = execution.Resources,
                TotalResources = execution.Preview.Resources.Count,
                CurrentResource = null,
                SourceProvenance = execution.Preview.SourceProvenance
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
            CurrentResource = null,
            Error = "bootstrap_application_interrupted"
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
        CancellationToken cancellationToken, bool limitHistory = true)
    {
        using var systemScope = scopes.PushSystem();
        var applications = (await store.ListAllAsync<BootstrapApplicationResource>(
            BootstrapKinds.BootstrapApplication, cancellationToken)).Select(value => value.Value).ToArray();
        var recovered = new List<BootstrapApplicationResource>(applications.Length);
        foreach (var application in applications)
            recovered.Add((await RecoverAsync(application, cancellationToken))!);
        var ordered = recovered.OrderByDescending(value => value.Definition.Status == BootstrapApplicationStatus.Running)
            .ThenByDescending(value => value.Definition.StartedAt);
        return (limitHistory ? ordered.Take(100) : ordered).ToArray();
    }

    private async Task<BootstrapApplicationResource?> RecoverAsync(BootstrapApplicationResource? application, CancellationToken cancellationToken)
    {
        if (application?.Definition.Status != BootstrapApplicationStatus.Running
            || application.Definition.ExecutionOwner == applicationLock.Owner) return application;
        await applicationLock.State.WaitAsync(cancellationToken);
        try
        {
            using var systemScope = scopes.PushSystem();
            application = (await store.GetAsync<BootstrapApplicationResource>(new(BootstrapKinds.BootstrapApplication, application.Metadata.Name), cancellationToken))!.Value;
            return application.Definition.Status == BootstrapApplicationStatus.Running
                ? await SaveApplicationAsync(Interrupt(application), application.ETag, cancellationToken) : application;
        }
        finally { applicationLock.State.Release(); }
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
                if (tenant is null || tenant.Status != TenantStatus.Active
                    || OperationalTopologyIdentities.IsSystemTenant(tenant))
                    throw new DeclarativeBootstrapException($"Bootstrap target Tenant '{tenantId}' is missing or inactive.");
            }
            return null;
        }
        if (target.TenantId is not { } targetTenantId)
            throw new DeclarativeBootstrapException("A Workspace bootstrap target requires its Tenant identifier.");
        var workspace = await identities.GetWorkspaceAsync(targetTenantId, target.WorkspaceId.Value, cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active
            || OperationalTopologyIdentities.IsOperationalWorkspace(workspace))
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

    private static IReadOnlyList<BootstrapBindingTargetOption> Options<T>(
        IReadOnlyList<StoredResource<T>> resources,
        Func<T, string> displayName) where T : Resource =>
        resources.Select(resource => new BootstrapBindingTargetOption(
            resource.Value.Name,
            resource.Value.Namespace.Value,
            displayName(resource.Value),
            ScopeRef: resource.Value.ScopeRef)).ToArray();
}
