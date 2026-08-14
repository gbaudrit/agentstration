using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class AgentManagementService(
    IControlPlaneStore store,
    IAgentResourceQueries agentQueries,
    IAgentDefinitionCompiler compiler,
    IAgentDeploymentReconciler reconciler,
    IManagementEventPublisher eventBus,
    IModelProfileReferenceValidator modelProfiles,
    TimeProvider timeProvider,
    IEnumerable<IManagementResourceDeletionGuard> deletionGuards)
{
    public Task InitializeAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public async Task<StoredResource<AgentResource>> PutAgentAsync(AgentResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateResource(resource, ResourceKinds.Agent);
        ValidateDefinition(resource.Definition);
        await modelProfiles.ValidateAsync(resource.Definition.ModelProfile, cancellationToken);
        var key = ResourceKey.Create(ResourceKinds.Agent, resource.Metadata.Name, resource.Namespace);
        var existing = await store.GetAsync<AgentResource>(key, cancellationToken);
        ValidatePreconditions(existing, ifMatch, ifNoneMatch);
        if (existing is not null && DesiredStateEquals(existing.Value, resource)) return existing;

        var desired = resource with
        {
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            ETag = null,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted }
        };
        var stored = await store.PutAsync(desired, ifMatch, ifNoneMatch, cancellationToken);
        if (existing is null)
            await eventBus.PublishAsync(new AgentCreated(stored.Value.Uid, stored.Value.Metadata.Name, stored.Value.Generation, timeProvider.GetUtcNow()), cancellationToken);
        else
            await eventBus.PublishAsync(new AgentUpdated(stored.Value.Uid, stored.Value.Metadata.Name, stored.Value.Generation, timeProvider.GetUtcNow()), cancellationToken);
        return stored;
    }

    public Task<StoredResource<AgentResource>?> GetAgentAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, name), cancellationToken);
    public Task<StoredResource<AgentResource>?> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentResource>(new ResourceKey(ResourceKinds.Agent, name, @namespace), cancellationToken);

    public Task<StoredResource<AgentDeployment>?> GetDeploymentAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<AgentDeployment>(new ResourceKey(ResourceKinds.AgentDeployment, name), cancellationToken);

    public async Task DeleteAgentAsync(string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var key = new ResourceKey(ResourceKinds.Agent, name);
        var existing = await store.GetAsync<AgentResource>(key, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(key);
        foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(key, cancellationToken);
        await store.DeleteAsync(key, ifMatch, cancellationToken);
        await eventBus.PublishAsync(new AgentDeleted(existing.Value.Uid, name, timeProvider.GetUtcNow()), cancellationToken);
    }

    public async Task DeleteAgentAsync(ResourceNamespace @namespace, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        var key = new ResourceKey(ResourceKinds.Agent, name, @namespace);
        var existing = await store.GetAsync<AgentResource>(key, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(key);
        foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(key, cancellationToken);
        await store.DeleteAsync(key, ifMatch, cancellationToken);
        await eventBus.PublishAsync(new AgentDeleted(existing.Value.Uid, name, timeProvider.GetUtcNow()), cancellationToken);
    }

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAgentsAsync(int skip, int take, CancellationToken cancellationToken) =>
        ListAgentsAsync(ResourceNamespace.Default, skip, take, cancellationToken);

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAgentsAsync(ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) =>
        store.ListAsync<AgentResource>(@namespace, ResourceKinds.Agent, skip, take, cancellationToken);

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(string agentName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
        => await CreateRevisionAsync(ResourceNamespace.Default, agentName, spec, cancellationToken);

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(ResourceNamespace @namespace, string agentName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        await ValidateRuntimeProfileAsync(spec.RuntimeProfileName, cancellationToken);
        var agent = await GetAgentAsync(@namespace, agentName, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, agentName, @namespace));
        var resolved = compiler.Compile(agent.Value, spec);
        var number = agent.Value.Generation;
        var revisionName = $"{agentName}--{number:000000}";
        return await store.CreateImmutableAsync(new AgentRevision
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata
            {
                Name = revisionName,
                Namespace = @namespace,
                Tags = agent.Value.Metadata.Tags,
                Annotations = agent.Value.Metadata.Annotations
            },
            AgentUid = agent.Value.Uid,
            AgentNamespace = @namespace,
            AgentName = agentName,
            AgentVersion = agent.Value.Generation,
            Definition = resolved,
            DefinitionHash = resolved.DefinitionHash,
            CreatedAt = timeProvider.GetUtcNow(),
            ProvisioningState = ProvisioningState.Succeeded
        }, cancellationToken);
    }

    public async Task<StoredResource<AgentDeployment>> CreateDeploymentAsync(string name, string revisionName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
        => await CreateDeploymentAsync(ResourceNamespace.Default, name, revisionName, spec, cancellationToken);

    public async Task<StoredResource<AgentDeployment>> CreateDeploymentAsync(ResourceNamespace @namespace, string name, string revisionName, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        await ValidateRuntimeProfileAsync(spec.RuntimeProfileName, cancellationToken);
        var revision = await store.GetAsync<AgentRevision>(new ResourceKey(ResourceKinds.AgentRevision, revisionName, @namespace), cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.AgentRevision, revisionName, @namespace));
        return await store.PutAsync(new AgentDeployment
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
            AgentNamespace = @namespace,
            RevisionName = revisionName,
            AgentName = revision.Value.AgentName,
            ModelProfileName = revision.Value.Definition.ModelProfileName,
            Environment = spec.Environment,
            RuntimeProfileName = spec.RuntimeProfileName,
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

    public async Task<StoredResource<AgentDeployment>> PrepareLocalRuntimeAsync(string agentName, long generation, CancellationToken cancellationToken)
        => await PrepareLocalRuntimeAsync(ResourceNamespace.Default, agentName, generation, cancellationToken);

    public async Task<StoredResource<AgentDeployment>> PrepareLocalRuntimeAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken)
    {
        var agent = await GetAgentAsync(@namespace, agentName, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, agentName, @namespace));
        if (agent.Value.Generation != generation) throw new ArgumentException($"Only current agent generation '{agent.Value.Generation}' can be prepared.", nameof(generation));
        var spec = new AgentDeploymentSpec { Environment = "local", RuntimeProfileName = "maf-default", HostingMode = AgentHostingMode.InProcess };
        var revision = await agentQueries.FindRevisionAsync(agent.Value.Uid, generation, cancellationToken)
            ?? await CreateRevisionAsync(@namespace, agentName, spec, cancellationToken);
        var deployment = await agentQueries.FindDeploymentByRevisionAsync(@namespace, revision.Value.Metadata.Name, cancellationToken)
            ?? await CreateDeploymentAsync(@namespace, $"{agentName}--g{generation:000000}", revision.Value.Metadata.Name, spec, cancellationToken);
        if (deployment.Value.DesiredState != DesiredAgentState.Running) deployment = await StartAsync(deployment, cancellationToken);
        var activated = await ReconcileAsync(deployment, cancellationToken);
        if (activated.Value.OperationalState != OperationalState.Ready) return activated;

        var current = await GetAgentAsync(@namespace, agentName, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Agent, agentName, @namespace));
        if (current.Value.Generation != generation) throw new ControlPlaneConcurrencyException($"Agent '{agentName}' changed while generation '{generation}' was being activated.");
        var deployments = await agentQueries.ListDeploymentsForAgentAsync(@namespace, agentName, cancellationToken);
        foreach (var previous in deployments.Where(item => item.Value.Uid != activated.Value.Uid && item.Value.AgentName == agentName && item.Value.DesiredState != DesiredAgentState.Stopped))
            _ = await ReconcileAsync(await StopAsync(previous, cancellationToken), cancellationToken);
        return activated;
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (var deployment in await agentQueries.ListDeploymentsAsync(cancellationToken))
        {
            try { await ReconcileAsync(deployment, cancellationToken); }
            catch (ControlPlaneConcurrencyException) { }
        }
    }

    private async Task ValidateRuntimeProfileAsync(string name, CancellationToken cancellationToken) =>
        _ = await store.GetAsync<RuntimeProfileResource>(new ResourceKey(ResourceKinds.RuntimeProfile, name), cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.RuntimeProfile, name));

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
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw new AgentDefinitionValidationException("api_version_not_supported", $"Supported API version is '{ManagementApiVersions.CoreV1}'.");
        ValidateName(resource.Metadata.Name, "metadata.name");
    }

    private static void ValidateDefinition(AgentProperties definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Instructions);
        ValidateReference(definition.ModelProfile, "model_profile_reference_invalid");
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in definition.Tools)
        {
            ValidateReference(tool, "tool_reference_invalid");
            if (!tools.Add(tool.Name)) throw new AgentDefinitionValidationException("duplicate_tool_reference", $"Tool reference '{tool.Name}' is duplicated.");
        }
    }

    private static void ValidateReference(ResourceReference reference, string errorCode)
    {
        ValidateName(reference.Name, "reference.name");
        if (reference.WorkspaceRef is not null)
            throw new AgentDefinitionValidationException("cross_workspace_reference_not_supported", "Cross-workspace resource references are not enabled in this installation.");
        if (string.IsNullOrWhiteSpace(reference.Name)) throw new AgentDefinitionValidationException(errorCode, "A resource reference name is required.");
    }

    private static void ValidateName(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new AgentDefinitionValidationException($"invalid_{field.Replace('.', '_')}", $"{field} must contain only letters, digits, '.', '_' or '-' and be at most 128 characters.");
    }

    private static void ValidatePreconditions(StoredResource<AgentResource>? existing, string? ifMatch, bool ifNoneMatch)
    {
        if (existing is null && ifMatch is not null) throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch) throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && existing.ETag != ifMatch) throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");
    }

    private static bool DesiredStateEquals(AgentResource left, AgentResource right) =>
        left.ApiVersion == right.ApiVersion && left.Kind == right.Kind && left.Metadata == right.Metadata && left.Definition == right.Definition;
}
