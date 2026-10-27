using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Schemas.Contracts;
using Agentstration.Security.Contracts;
using Agentstration.Tools;

namespace Agentstration.Agents;

public sealed record AgentRevisionRetentionOptions
{
    public int RetainLatestPerAgent { get; init; } = 3;
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromDays(30);
}

public sealed record AgentRevisionPurgeImpact(
    ResourceNamespace Namespace,
    string AgentName,
    string RevisionName,
    long AgentGeneration,
    bool ProtectedByRetentionPolicy,
    IReadOnlyList<string> ProtectionReasons,
    string? DeploymentName,
    AgentRevisionRunUsage RunUsage);

public sealed class AgentRevisionPurgeBlockedException(AgentRevisionPurgeImpact impact)
    : Exception($"Agent revision '{impact.RevisionName}' cannot be purged while it is protected or used by active runs.")
{
    public AgentRevisionPurgeImpact Impact { get; } = impact;
}

public sealed class AgentManagementService(
    IResourceStore store,
    IAgentResourceQueries agentQueries,
    IAgentDefinitionCompiler compiler,
    IAgentDeploymentReconciler reconciler,
    IManagementEventPublisher eventBus,
    IModelProfileReferenceValidator modelProfiles,
    IResourceReferenceResolver references,
    IResourceAccessPolicyService accessPolicies,
    ToolSetService toolSets,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider,
    IEnumerable<IResourceDeletionGuard> deletionGuards,
    IAgentRevisionRunRetention revisionRunRetention,
    ISecurityAuditWriter audit,
    AgentRevisionRetentionOptions revisionRetentionOptions,
    ISchemaBindingResolver? schemaBindingResolver = null)
{
    private const string JsonSchemaFormat = "json-schema";

    public Task InitializeAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public async Task ValidateForCreateAsync(AgentResource resource, CancellationToken cancellationToken)
    {
        ValidateResource(resource, AgentResourceKinds.Agent);
        ValidateDefinition(resource.Definition);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(AgentResourceKinds.Agent);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        _ = await ResolveContractsAsync(resource.Definition, scopeRef, resource.Namespace, cancellationToken);
        await ValidateReferencesAsync(resource, scopeRef, cancellationToken);
    }

    public async Task<StoredResource<AgentResource>> PutAgentAsync(AgentResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateResource(resource, AgentResourceKinds.Agent);
        ValidateDefinition(resource.Definition);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(AgentResourceKinds.Agent);
        var address = ScopedResourceAddress.Create(scopeRef, resource.Namespace, AgentResourceKinds.Agent, resource.Name);
        var existing = await store.GetExactAsync<AgentResource>(address, cancellationToken);
        ValidatePreconditions(existing, ifMatch, ifNoneMatch);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await ValidateReferencesAsync(resource, scopeRef, cancellationToken);
        var contracts = await ResolveContractsAsync(resource.Definition, scopeRef, resource.Namespace, cancellationToken);
        resource = resource with { Definition = contracts.Definition };
        if (existing is not null && DesiredStateEquals(existing.Value, resource)) return existing;

        var desired = resource with
        {
            ScopeRef = scopeRef,
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            ETag = null,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        var stored = await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, ifMatch, ifNoneMatch, token), cancellationToken);
        if (existing is null)
            await eventBus.PublishAsync(new AgentCreated(stored.Value.Uid, stored.Value.Metadata.Name, stored.Value.Generation, timeProvider.GetUtcNow()), cancellationToken);
        else
            await eventBus.PublishAsync(new AgentUpdated(stored.Value.Uid, stored.Value.Metadata.Name, stored.Value.Generation, timeProvider.GetUtcNow()), cancellationToken);
        return stored;
    }

    public Task<StoredResource<AgentResource>?> GetAgentAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, name), cancellationToken);
    public Task<StoredResource<AgentResource>?> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentResource>(new ResourceKey(AgentResourceKinds.Agent, name, @namespace), cancellationToken);
    public Task<StoredResource<AgentResource>?> GetAgentAsync(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
        scopeRef is { } exact ? GetAgentExactAsync(exact, @namespace, name, cancellationToken) : GetAgentAsync(@namespace, name, cancellationToken);
    public Task<StoredResource<AgentResource>?> GetAgentExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<AgentResource>(ScopedResourceAddress.Create(scopeRef, @namespace, AgentResourceKinds.Agent, name), cancellationToken);

    public Task<StoredResource<AgentDeployment>?> GetDeploymentAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentDeployment>(new ResourceKey(AgentResourceKinds.AgentDeployment, name), cancellationToken);

    public Task DeleteAgentAsync(string name, string? ifMatch, CancellationToken cancellationToken) =>
        DeleteAgentAsync(ResourceNamespace.Default, name, ifMatch, cancellationToken);

    public async Task DeleteAgentAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var key = new ResourceKey(AgentResourceKinds.Agent, name, @namespace);
        var existing = await store.GetAsync<AgentResource>(key, cancellationToken) ?? throw new ResourceNotFoundException(key);
        foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(key, cancellationToken);
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");

        var deployments = await agentQueries.ListDeploymentsForAgentAsync(@namespace, name, cancellationToken);
        foreach (var deployment in deployments)
        {
            var stopped = deployment.Value.DesiredState == DesiredAgentState.Stopped
                ? deployment
                : await StopAsync(deployment, cancellationToken);
            var reconciled = await ReconcileAsync(stopped, cancellationToken);
            await store.DeleteAsync(
                new ResourceKey(AgentResourceKinds.AgentDeployment, reconciled.Value.Metadata.Name, @namespace),
                reconciled.ETag,
                cancellationToken);
        }

        await store.DeleteAsync(key, ifMatch, cancellationToken);
        await eventBus.PublishAsync(new AgentDeleted(existing.Value.Uid, name, timeProvider.GetUtcNow()), cancellationToken);
    }

    public async Task DeleteAgentExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var key = new ResourceKey(AgentResourceKinds.Agent, name, @namespace);
        var address = ScopedResourceAddress.Create(scopeRef, @namespace, AgentResourceKinds.Agent, name);
        var existing = await store.GetExactAsync<AgentResource>(address, cancellationToken) ?? throw new ResourceNotFoundException(key);
        foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(key, cancellationToken);
        if (ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");

        var deployments = await agentQueries.ListDeploymentsForAgentAsync(@namespace, name, cancellationToken);
        foreach (var deployment in deployments.Where(value => value.Value.ScopeRef == scopeRef))
        {
            var stopped = deployment.Value.DesiredState == DesiredAgentState.Stopped
                ? deployment
                : await StopAsync(deployment, cancellationToken);
            var reconciled = await ReconcileAsync(stopped, cancellationToken);
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, @namespace, AgentResourceKinds.AgentDeployment, reconciled.Value.Metadata.Name),
                reconciled.ETag, cancellationToken);
        }

        await store.DeleteExactAsync(address, ifMatch, cancellationToken);
        await eventBus.PublishAsync(new AgentDeleted(existing.Value.Uid, name, timeProvider.GetUtcNow()), cancellationToken);
    }

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAgentsAsync(int skip, int take, CancellationToken cancellationToken) =>
        ListAgentsAsync(ResourceNamespace.Default, skip, take, cancellationToken);

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAgentsAsync(ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) =>
        store.ListAsync<AgentResource>(@namespace, AgentResourceKinds.Agent, skip, take, cancellationToken);

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAllAgentsAsync(int skip, int take, CancellationToken cancellationToken) =>
        store.ListAsync<AgentResource>(AgentResourceKinds.Agent, skip, take, cancellationToken);

    public async Task<IReadOnlyList<StoredResource<AgentDeployment>>> ListDeploymentsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        return (await agentQueries.ListDeploymentsAsync(cancellationToken)).Skip(skip).Take(take).ToArray();
    }

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(string agentName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
        => await CreateRevisionAsync(ResourceNamespace.Default, agentName, spec, cancellationToken);

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(ResourceNamespace @namespace, string agentName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
        => await CreateRevisionAsync(@namespace, agentName, null, spec, cancellationToken);

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(ResourceNamespace @namespace, string agentName, ResourceScopeRef? scopeRef, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        var agent = await GetAgentAsync(@namespace, agentName, scopeRef, cancellationToken) ?? throw new ResourceNotFoundException(new(AgentResourceKinds.Agent, agentName, @namespace));
        var ownerScopeRef = agent.Value.ScopeRef
            ?? throw new AgentDefinitionValidationException("agent_scope_invalid", "The Agent has no ownership scope.");
        var contracts = await ResolveContractsAsync(agent.Value.Definition, ownerScopeRef, agent.Value.Namespace, cancellationToken);
        var resolvedAgent = agent.Value with { Definition = contracts.Definition };
        var (toolSetTools, toolSetAssignments, toolSetPins) = await ResolveToolSetAssignmentsAsync(resolvedAgent, cancellationToken);
        var directToolPins = await ResolveToolPinsAsync(resolvedAgent, ownerScopeRef, cancellationToken);
        var modelProfilePin = await modelProfiles.ResolveAsync(
            resolvedAgent.Definition.ModelProfile, resolvedAgent.Namespace, ownerScopeRef, cancellationToken)
            ?? await ResolveDependencyAsync(resolvedAgent.Definition.ModelProfile, resolvedAgent.Namespace,
                ModelResourceKinds.ModelProfile, ownerScopeRef, cancellationToken);
        var runtimeReference = new ResourceReference(spec.RuntimeProfileName,
            resolvedAgent.Definition.RuntimeProfile.ScopeRef, spec.RuntimeProfileNamespace);
        var runtimeProfilePin = await ResolveDependencyAsync(runtimeReference, resolvedAgent.Namespace,
            RuntimeProfileResourceKinds.RuntimeProfile, ownerScopeRef, cancellationToken);
        var resolved = compiler.Compile(resolvedAgent, spec, toolSetTools, toolSetAssignments,
            [.. directToolPins, .. toolSetPins],
            modelProfilePin, runtimeProfilePin) with
        { SchemaBindings = contracts.Snapshots };
        var number = agent.Value.Generation;
        var revisionName = $"{agentName}--{number:000000}";
        var revisionAddress = ScopedResourceAddress.Create(ownerScopeRef, @namespace, AgentResourceKinds.AgentRevision, revisionName);
        var existing = await GetRevisionInternalAsync(revisionAddress, cancellationToken);
        if (existing is not null)
        {
            if (Matches(existing.Value, agent.Value, resolved)) return existing;
            revisionName = $"{revisionName}--{agent.Value.Uid:N}";
            revisionAddress = ScopedResourceAddress.Create(ownerScopeRef, @namespace, AgentResourceKinds.AgentRevision, revisionName);
            existing = await GetRevisionInternalAsync(revisionAddress, cancellationToken);
            if (existing is not null)
            {
                if (Matches(existing.Value, agent.Value, resolved)) return existing;
                throw new ResourceConcurrencyException($"Agent revision '{revisionName}' already belongs to a different Agent definition.");
            }
        }

        var revision = new AgentRevision
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata
            {
                Name = revisionName,
                Namespace = @namespace,
                Tags = agent.Value.Metadata.Tags,
                Annotations = agent.Value.Metadata.Annotations
            },
            ScopeRef = ownerScopeRef,
            AgentUid = agent.Value.Uid,
            AgentNamespace = @namespace,
            AgentName = agentName,
            AgentVersion = agent.Value.Generation,
            Definition = resolved,
            DefinitionHash = resolved.DefinitionHash,
            CreatedAt = timeProvider.GetUtcNow(),
            ProvisioningState = ProvisioningState.Succeeded
        };
        try
        {
            return await scopeOperations.WriteAsync(revision, ownerScopeRef, AuthorizationPermissions.ResourcesWrite,
                token => store.CreateImmutableAsync(revision, token), cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            existing = await GetRevisionInternalAsync(revisionAddress, cancellationToken);
            if (existing is not null && Matches(existing.Value, agent.Value, resolved)) return existing;
            throw;
        }
    }

    public async Task<StoredResource<AgentRevision>> EnsureExecutionRevisionAsync(
        ResourceNamespace @namespace,
        string agentName,
        long generation,
        CancellationToken cancellationToken)
        => await EnsureExecutionRevisionAsync(@namespace, agentName, null, generation, cancellationToken);

    public async Task<StoredResource<AgentRevision>> EnsureExecutionRevisionAsync(
        ResourceNamespace @namespace,
        string agentName,
        ResourceScopeRef? scopeRef,
        long generation,
        CancellationToken cancellationToken)
    {
        var agent = await GetAgentAsync(@namespace, agentName, scopeRef, cancellationToken)
            ?? throw new ResourceNotFoundException(new(AgentResourceKinds.Agent, agentName, @namespace));
        var existing = await FindRevisionInternalAsync(agent.Value.Uid, generation, cancellationToken);
        if (existing is not null) return existing;
        if (agent.Value.Generation != generation)
            throw new ArgumentException($"Agent generation '{generation}' has no immutable revision.", nameof(generation));
        var runtimeProfile = agent.Value.Definition.RuntimeProfile.Resolve(
            agent.Value.Namespace, RuntimeProfileResourceKinds.RuntimeProfile);
        return await CreateRevisionAsync(@namespace, agentName, agent.Value.ScopeRef, new AgentDeploymentSpec
        {
            Environment = "worker",
            RuntimeProfileName = runtimeProfile.Name,
            RuntimeProfileNamespace = runtimeProfile.Namespace,
            HostingMode = AgentHostingMode.DedicatedProcess
        }, cancellationToken);
    }

    private static bool Matches(AgentRevision revision, AgentResource agent, ResolvedAgentDefinition definition) =>
        revision.AgentUid == agent.Uid
        && revision.AgentVersion == agent.Generation
        && string.Equals(revision.DefinitionHash, definition.DefinitionHash, StringComparison.Ordinal);

    private async Task<StoredResource<AgentRevision>?> FindRevisionInternalAsync(
        Guid agentUid,
        long generation,
        CancellationToken cancellationToken)
    {
        using var systemScope = requestScopes.PushSystem();
        return await agentQueries.FindRevisionAsync(agentUid, generation, cancellationToken);
    }

    private async Task<StoredResource<AgentRevision>?> GetRevisionInternalAsync(
        ScopedResourceAddress address,
        CancellationToken cancellationToken)
    {
        using var systemScope = requestScopes.PushSystem();
        return await store.GetExactAsync<AgentRevision>(address, cancellationToken);
    }

    public async Task<StoredResource<AgentRevision>> GetRevisionAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        CancellationToken cancellationToken)
        => await GetRevisionAsync(@namespace, agentName, revisionName, null, cancellationToken);

    public async Task<StoredResource<AgentRevision>> GetRevisionAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var key = new ResourceKey(AgentResourceKinds.AgentRevision, revisionName, @namespace);
        var revision = scopeRef is { } exact
            ? await store.GetExactAsync<AgentRevision>(ScopedResourceAddress.Create(exact, @namespace, AgentResourceKinds.AgentRevision, revisionName), cancellationToken)
            : await store.GetAsync<AgentRevision>(key, cancellationToken);
        revision = revision
            ?? throw new ResourceNotFoundException(key);
        if (!string.Equals(revision.Value.AgentName, agentName, StringComparison.Ordinal))
            throw new ResourceNotFoundException(key);
        return revision;
    }

    public async Task<AgentRevisionPurgeImpact> GetRevisionPurgeImpactAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        CancellationToken cancellationToken)
    {
        var key = new ResourceKey(AgentResourceKinds.AgentRevision, revisionName, @namespace);
        var revision = await store.GetAsync<AgentRevision>(key, cancellationToken)
            ?? throw new ResourceNotFoundException(key);
        if (!string.Equals(revision.Value.AgentName, agentName, StringComparison.Ordinal))
            throw new ResourceNotFoundException(key);
        var revisions = (await store.ListAsync<AgentRevision>(@namespace, AgentResourceKinds.AgentRevision, 0, 1000, cancellationToken))
            .Where(value => value.Value.AgentUid == revision.Value.AgentUid)
            .OrderByDescending(value => value.Value.AgentVersion)
            .ToArray();
        var reasons = new List<string>();
        if (revisions.Take(Math.Max(0, revisionRetentionOptions.RetainLatestPerAgent)).Any(value => value.Value.Metadata.Name == revisionName))
            reasons.Add("retain_latest");
        if (timeProvider.GetUtcNow() - revision.Value.CreatedAt < revisionRetentionOptions.MinimumAge)
            reasons.Add("minimum_age");
        var deployment = await agentQueries.FindDeploymentByRevisionAsync(@namespace, revisionName, cancellationToken);
        var usage = await revisionRunRetention.GetUsageAsync(revisionName, cancellationToken);
        return new(@namespace, agentName, revisionName, revision.Value.AgentVersion, reasons.Count > 0, reasons,
            deployment?.Value.Metadata.Name, usage);
    }

    public async Task<AgentRevisionPurgeImpact> PurgeRevisionAsync(
        ResourceNamespace @namespace,
        string agentName,
        string revisionName,
        string? ifMatch,
        bool force,
        CancellationToken cancellationToken)
    {
        var impact = await GetRevisionPurgeImpactAsync(@namespace, agentName, revisionName, cancellationToken);
        if (!force && (impact.ProtectedByRetentionPolicy || impact.RunUsage.ActiveRunCount > 0))
            throw new AgentRevisionPurgeBlockedException(impact);

        try
        {
            if (force && impact.RunUsage.ActiveRunCount > 0)
            {
                await revisionRunRetention.ForceTerminateAsync(revisionName, cancellationToken);
                var remaining = await revisionRunRetention.GetUsageAsync(revisionName, cancellationToken);
                if (remaining.ActiveRunCount > 0)
                    throw new ResourceConcurrencyException("Some Flow Runs still use the revision after forced termination.");
            }

            var deployment = await agentQueries.FindDeploymentByRevisionAsync(@namespace, revisionName, cancellationToken);
            if (deployment is not null)
            {
                if (deployment.Value.DesiredState != DesiredAgentState.Stopped)
                    deployment = await StopAsync(deployment, cancellationToken);
                deployment = await ReconcileAsync(deployment, cancellationToken);
                await store.DeleteAsync(new(AgentResourceKinds.AgentDeployment, deployment.Value.Metadata.Name, @namespace), deployment.ETag, cancellationToken);
            }

            var revision = await store.GetAsync<AgentRevision>(new(AgentResourceKinds.AgentRevision, revisionName, @namespace), cancellationToken)
                ?? throw new ResourceNotFoundException(new(AgentResourceKinds.AgentRevision, revisionName, @namespace));
            await store.DeleteAsync(new(AgentResourceKinds.AgentRevision, revisionName, @namespace), ifMatch ?? revision.ETag, cancellationToken);
            await audit.WriteAsync(new(
                force ? SecurityAuditActions.AgentRevisionForcePurged : SecurityAuditActions.AgentRevisionPurged,
                ReasonCode: $"active-runs:{impact.RunUsage.ActiveRunCount}"), cancellationToken);
            return impact;
        }
        catch when (force)
        {
            await audit.WriteAsync(new(SecurityAuditActions.AgentRevisionForcePurged, SecurityAuditOutcome.Failed,
                ReasonCode: "force-purge-failed"), CancellationToken.None);
            throw;
        }
    }

    public async Task<StoredResource<AgentDeployment>> CreateDeploymentAsync(string name, string revisionName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
        => await CreateDeploymentAsync(ResourceNamespace.Default, name, revisionName, spec, cancellationToken);

    public async Task<StoredResource<AgentDeployment>> CreateDeploymentAsync(ResourceNamespace @namespace, string name, string revisionName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        await ValidateRuntimeProfileAsync(spec.RuntimeProfileNamespace, spec.RuntimeProfileName, cancellationToken);
        var revision = await store.GetAsync<AgentRevision>(new ResourceKey(AgentResourceKinds.AgentRevision, revisionName, @namespace), cancellationToken)
            ?? throw new ResourceNotFoundException(new(AgentResourceKinds.AgentRevision, revisionName, @namespace));
        return await store.PutAsync(new AgentDeployment
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
            AgentNamespace = @namespace,
            RevisionName = revisionName,
            AgentName = revision.Value.AgentName,
            ModelProfileName = revision.Value.Definition.ModelProfileName,
            ModelProfileNamespace = revision.Value.Definition.ModelProfileNamespace,
            Environment = spec.Environment,
            RuntimeProfileName = spec.RuntimeProfileName,
            RuntimeProfileNamespace = spec.RuntimeProfileNamespace,
            HostingMode = spec.HostingMode,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Accepted,
            OperationalState = OperationalState.Starting,
            UpdatedAt = timeProvider.GetUtcNow()
        }, null, true, cancellationToken);
    }

    public Task<StoredResource<AgentDeployment>> StartAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken) =>
        SetDesiredStateAsync(deployment, DesiredAgentState.Running, cancellationToken);

    public Task<StoredResource<AgentDeployment>> StopAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken) =>
        SetDesiredStateAsync(deployment, DesiredAgentState.Stopped, cancellationToken);

    public async Task<StoredResource<AgentDeployment>> ReconcileAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken)
    {
        var result = await reconciler.ReconcileAsync(deployment.Value, cancellationToken);
        return result.Changed
            ? await store.PutAsync(result.Deployment with { UpdatedAt = timeProvider.GetUtcNow() }, deployment.ETag, false, cancellationToken)
            : deployment;
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (var deployment in await agentQueries.ListDeploymentsAsync(cancellationToken))
        {
            try { await ReconcileAsync(deployment, cancellationToken); }
            catch (ResourceConcurrencyException) { }
        }
    }

    private async Task ValidateRuntimeProfileAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        _ = await store.GetAsync<RuntimeProfileResource>(new ResourceKey(RuntimeProfileResourceKinds.RuntimeProfile, name, @namespace), cancellationToken)
            ?? throw new ResourceNotFoundException(new(RuntimeProfileResourceKinds.RuntimeProfile, name, @namespace));

    private async Task ValidateReferencesAsync(AgentResource resource, ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        using var tenantResolutionScope = requestContext.AccessMode == ControlPlaneAccessMode.TenantAdministration
            ? requestScopes.PushTenant(requestContext.Current.PrincipalId, requestContext.Current.TenantId)
            : null;
        await modelProfiles.ValidateAsync(resource.Definition.ModelProfile, resource.Namespace, scopeRef, cancellationToken);
        _ = await ResolveDependencyAsync(resource.Definition.RuntimeProfile, resource.Namespace,
            RuntimeProfileResourceKinds.RuntimeProfile, scopeRef, cancellationToken);
        _ = await ResolveToolPinsAsync(resource, scopeRef, cancellationToken);
        _ = await ResolveToolSetAssignmentsAsync(resource with { ScopeRef = scopeRef }, cancellationToken);
    }

    private async Task<ResolvedResourceDependency> ResolveDependencyAsync(ResourceReference reference,
        ResourceNamespace ownerNamespace, string kind, ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        using var tenantResolutionScope = requestContext.AccessMode == ControlPlaneAccessMode.TenantAdministration
            ? requestScopes.PushTenant(requestContext.Current.PrincipalId, requestContext.Current.TenantId)
            : null;
        Resource? stored;
        try
        {
            stored = kind switch
            {
                ModelResourceKinds.ModelProfile => (await references.ResolveAsync<ModelProfileResource>(reference, ownerNamespace, kind, consumerScopeRef, cancellationToken))?.Value,
                RuntimeProfileResourceKinds.RuntimeProfile => (await references.ResolveAsync<RuntimeProfileResource>(reference, ownerNamespace, kind, consumerScopeRef, cancellationToken))?.Value,
                ToolResourceKinds.Tool => (await references.ResolveAsync<ToolResource>(reference, ownerNamespace, kind, consumerScopeRef, cancellationToken))?.Value,
                _ => throw new AgentDefinitionValidationException("dependency_kind_unsupported", $"Agent dependency kind '{kind}' is not supported.")
            };
        }
        catch (ResourceReferenceAmbiguousException exception)
        {
            throw new AgentDefinitionValidationException("dependency_reference_ambiguous", exception.Message);
        }
        catch (ResourceReferenceOutsideScopeException exception)
        {
            throw new AgentDefinitionValidationException("dependency_scope_invalid", exception.Message);
        }
        if (stored is null)
        {
            var address = reference.Resolve(ownerNamespace, kind);
            throw new ResourceNotFoundException(new(kind, address.Name, address.Namespace));
        }
        var storedScopeRef = stored.ScopeRef
            ?? throw new AgentDefinitionValidationException("dependency_scope_invalid",
                $"Referenced {kind} '{stored.Address}' has no ownership scope.");
        if (storedScopeRef != consumerScopeRef)
        {
            var access = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(
                stored.Uid, storedScopeRef, stored.Namespace, kind, stored.Generation),
                consumerScopeRef, ResourceAccessAction.Use, cancellationToken);
            if (!access.Allowed)
                throw new AgentDefinitionValidationException("resource_access_denied",
                    $"Referenced {kind} '{stored.Address}' does not authorize use from '{consumerScopeRef}' ({access.ReasonCode}).");
        }
        return new(stored.Uid, storedScopeRef, stored.Namespace, kind,
            stored.Name, stored.Generation);
    }

    private async Task<IReadOnlyCollection<ResolvedResourceDependency>> ResolveToolPinsAsync(
        AgentResource resource, ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken)
    {
        var pins = new List<ResolvedResourceDependency>(resource.Definition.Tools.Count);
        foreach (var tool in resource.Definition.Tools)
            pins.Add(await ResolveDependencyAsync(tool, resource.Namespace, ToolResourceKinds.Tool, ownerScopeRef, cancellationToken));
        return pins;
    }

    private async Task<(IReadOnlyCollection<ResourceReference> Tools, IReadOnlyCollection<ResolvedAgentToolSetAssignment> Assignments, IReadOnlyCollection<ResolvedResourceDependency> ToolPins)>
        ResolveToolSetAssignmentsAsync(AgentResource resource, CancellationToken cancellationToken)
    {
        var tools = new List<ResourceReference>();
        var assignments = new List<ResolvedAgentToolSetAssignment>();
        var toolPins = new List<ResolvedResourceDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in resource.Definition.ToolSets)
        {
            var toolSetNamespace = selection.ToolSet.Namespace ?? resource.Namespace;
            var identity = $"{toolSetNamespace.Value}/{selection.ToolSet.Name}:{selection.Version}";
            if (!seen.Add(identity))
                throw new AgentDefinitionValidationException("duplicate_tool_set_reference", $"ToolSet reference '{identity}' is duplicated.");
            if (string.IsNullOrWhiteSpace(selection.Version))
                throw new AgentDefinitionValidationException("tool_set_version_required", "An Agent ToolSet assignment requires an exact published version.");
            var scopeRef = resource.ScopeRef
                ?? throw new AgentDefinitionValidationException("agent_scope_invalid", "An Agent ToolSet assignment requires an Agent ownership scope.");
            var source = await references.ResolveAsync<ToolSetResource>(selection.ToolSet, resource.Namespace,
                ToolResourceKinds.ToolSet, scopeRef, cancellationToken)
                ?? throw new AgentDefinitionValidationException("tool_set_version_not_found", $"ToolSet '{identity}' was not found.");
            var sourceScopeRef = source.Value.ScopeRef
                ?? throw new AgentDefinitionValidationException("dependency_scope_invalid", $"ToolSet '{identity}' has no ownership scope.");
            if (sourceScopeRef != scopeRef)
            {
                var access = await accessPolicies.EvaluateAsync(new ResourceAccessIdentity(
                    source.Value.Uid, sourceScopeRef, source.Value.Namespace, ToolResourceKinds.ToolSet,
                    source.Value.Generation), scopeRef, ResourceAccessAction.Use, cancellationToken);
                if (!access.Allowed)
                    throw new AgentDefinitionValidationException("resource_access_denied",
                        $"Referenced ToolSet '{source.Value.Address}' does not authorize use from '{scopeRef}' ({access.ReasonCode}).");
            }
            var published = await toolSets.GetVersionExactAsync(sourceScopeRef, toolSetNamespace, selection.ToolSet.Name, selection.Version, cancellationToken)
                ?? throw new AgentDefinitionValidationException("tool_set_version_not_found", $"Published ToolSet '{identity}' was not found.");

            var requested = selection.Members.ToHashSet(StringComparer.Ordinal);
            if (requested.Count != selection.Members.Count)
                throw new AgentDefinitionValidationException("tool_set_member_selection_duplicate", $"ToolSet assignment '{identity}' contains duplicate members.");
            var available = published.Value.Members
                .ToDictionary(member => ToolResourceIdentity.CatalogId(member.ToolNamespace, member.ToolName), StringComparer.Ordinal);
            if (requested.Count > 0)
                foreach (var member in requested.Where(member => !available.ContainsKey(member)))
                    throw new AgentDefinitionValidationException("tool_set_member_not_found", $"ToolSet member '{member}' does not exist in '{identity}'.");
            IEnumerable<string> selectedNames = requested.Count == 0 ? available.Keys : requested;
            var selected = selectedNames
                .Order(StringComparer.Ordinal)
                .ToArray();
            tools.AddRange(selected.Select(member =>
            {
                var separator = member.IndexOf('/', StringComparison.Ordinal);
                return separator < 0
                    ? new ResourceReference(member)
                    : new ResourceReference(member[(separator + 1)..], @namespace: ResourceNamespace.Parse(member[..separator]));
            }));
            foreach (var member in published.Value.Members.Where(member => selected.Contains(
                         ToolResourceIdentity.CatalogId(member.ToolNamespace, member.ToolName), StringComparer.Ordinal)))
                toolPins.Add(new(member.ToolUid, member.ToolScopeRef, member.ToolNamespace,
                    ToolResourceKinds.Tool, member.ToolName, member.ToolGeneration));
            assignments.Add(new ResolvedAgentToolSetAssignment
            {
                ToolSetUid = published.Value.Uid,
                ToolSetScopeRef = sourceScopeRef,
                ToolSetName = selection.ToolSet.Name,
                ToolSetNamespace = toolSetNamespace,
                ToolSetGeneration = published.Value.Generation,
                Version = selection.Version,
                DefinitionHash = published.Value.DefinitionHash,
                Members = selected
            });
        }
        return (tools, assignments, toolPins);
    }

    private async Task<StoredResource<AgentDeployment>> SetDesiredStateAsync(StoredResource<AgentDeployment> stored, DesiredAgentState state, CancellationToken cancellationToken) =>
        await store.PutAsync(stored.Value with
        {
            DesiredState = state,
            ProvisioningState = ProvisioningState.Accepted,
            OperationalState = state == DesiredAgentState.Running ? OperationalState.Starting : stored.Value.OperationalState,
            UpdatedAt = timeProvider.GetUtcNow()
        }, stored.ETag, false, cancellationToken);

    private static void ValidateResource(Resource resource, string expectedKind)
    {
        if (resource.Kind != expectedKind) throw new AgentDefinitionValidationException("resource_kind_mismatch", $"Expected resource kind '{expectedKind}'.");
        if (resource.ApiVersion != ResourceApiVersions.CoreV1) throw new AgentDefinitionValidationException("api_version_not_supported", $"Supported API version is '{ResourceApiVersions.CoreV1}'.");
        ValidateName(resource.Metadata.Name, "metadata.name");
    }

    private static void ValidateDefinition(AgentProperties definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Instructions);
        ValidateReference(definition.ModelProfile, "model_profile_reference_invalid");
        ValidateReference(definition.RuntimeProfile, "runtime_profile_reference_invalid");
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in definition.Tools)
        {
            ValidateReference(tool, "tool_reference_invalid");
            if (!tools.Add(tool.Name)) throw new AgentDefinitionValidationException("duplicate_tool_reference", $"Tool reference '{tool.Name}' is duplicated.");
        }
        foreach (var selection in definition.ToolSets)
        {
            ValidateReference(selection.ToolSet, "tool_set_reference_invalid");
            if (string.IsNullOrWhiteSpace(selection.Version))
                throw new AgentDefinitionValidationException("tool_set_version_required", "An exact ToolSet version is required.");
            foreach (var member in selection.Members)
                ValidateName(member.Contains('/', StringComparison.Ordinal) ? member[(member.IndexOf('/', StringComparison.Ordinal) + 1)..] : member, "tool_set.member");
        }
        ValidateSchema(definition.InputSchema, definition.InputSchemaBinding, "input");
        ValidateSchema(definition.OutputSchema, definition.OutputSchemaBinding, "output");
    }

    private static void ValidateSchema(JsonElement? schema, SchemaBinding? binding, string direction)
    {
        if (schema is { ValueKind: not JsonValueKind.Object })
            throw new AgentDefinitionValidationException($"agent_{direction}_schema_invalid", $"The Agent {direction} schema must be a JSON object.");
        if (binding is { Inline: null, Reference: null } or { Inline: not null, Reference: not null })
            throw new AgentDefinitionValidationException("schema_binding_invalid", $"The Agent {direction} contract must select exactly one Schema binding form.");
    }

    private async ValueTask<(AgentProperties Definition, IReadOnlyDictionary<string, SchemaBindingSnapshot> Snapshots)> ResolveContractsAsync(
        AgentProperties definition,
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, SchemaBindingSnapshot>(StringComparer.Ordinal);
        var input = await ResolveContractAsync("$.inputSchema", definition.InputSchema, definition.InputSchemaBinding,
            scopeRef, ownerNamespace, snapshots, cancellationToken);
        var output = await ResolveContractAsync("$.outputSchema", definition.OutputSchema, definition.OutputSchemaBinding,
            scopeRef, ownerNamespace, snapshots, cancellationToken);
        return (definition with { InputSchema = input, OutputSchema = output }, snapshots);
    }

    private async ValueTask<JsonElement?> ResolveContractAsync(
        string path,
        JsonElement? legacy,
        SchemaBinding? binding,
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        Dictionary<string, SchemaBindingSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (binding is null)
            return legacy?.Clone();
        if (schemaBindingResolver is null)
            throw new AgentDefinitionValidationException("schema_binding_resolver_unavailable", "Schema bindings are not available in this composition.");
        try
        {
            var snapshot = await schemaBindingResolver.ResolveAsync(path, binding,
                new SchemaBindingResolutionContext(scopeRef, ownerNamespace, JsonSchemaFormat), cancellationToken);
            snapshots[path] = snapshot;
            return snapshot.Content.Clone();
        }
        catch (SchemaBindingResolutionException exception)
        {
            throw new AgentDefinitionValidationException(exception.Code, exception.Message);
        }
    }

    private static void ValidateReference(ResourceReference reference, string errorCode)
    {
        ValidateName(reference.Name, "reference.name");
        if (string.IsNullOrWhiteSpace(reference.Name)) throw new AgentDefinitionValidationException(errorCode, "A resource reference name is required.");
    }

    private static void ValidateName(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new AgentDefinitionValidationException($"invalid_{field.Replace('.', '_')}", $"{field} must contain only letters, digits, '.', '_' or '-' and be at most 128 characters.");
    }

    private static void ValidatePreconditions(StoredResource<AgentResource>? existing, string? ifMatch, bool ifNoneMatch)
    {
        if (existing is null && ifMatch is not null) throw new ResourceConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ResourceConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && existing.ETag != ifMatch) throw new ResourceConcurrencyException("The supplied ETag does not match the current resource version.");
    }

    private static bool DesiredStateEquals(AgentResource left, AgentResource right) =>
        left.ApiVersion == right.ApiVersion && left.Kind == right.Kind && left.Metadata == right.Metadata && left.Definition == right.Definition;
}
