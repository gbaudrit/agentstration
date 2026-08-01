using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using RuntimeExecutionRequest = Agentstration.Runtime.Abstractions.AgentExecutionRequest;
using RuntimeExecutionResult = Agentstration.Runtime.Abstractions.AgentExecutionResult;

namespace Agentstration.Management.Core;

public sealed record SelectedAgentRoute(AgentRouteResult Route, string DeploymentId);

public sealed class AgentManagementService(
    IControlPlaneStore store,
    IAgentDefinitionCompiler compiler,
    IAgentDeploymentReconciler reconciler,
    IAgentRouter router,
    IRuntimeRegistry runtimes,
    IManagementEventPublisher eventBus,
    TimeProvider timeProvider)
{
    public Task InitializeAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public async Task<StoredResource<AgentTypeResource>> PutAgentTypeAsync(AgentTypeResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateResource(resource, AgentstrationResourceTypes.AgentTypes);
        if (!string.Equals(resource.Name, resource.Properties.Key, StringComparison.Ordinal)) throw new AgentDefinitionValidationException("name_key_mismatch", "Agent type name and key must match.");
        if (resource.Properties.Version < 1) throw new AgentDefinitionValidationException("invalid_version", "Agent type version must be positive.");
        return await store.PutAsync(resource, ifMatch, ifNoneMatch, cancellationToken);
    }

    public async Task<StoredResource<AgentResource>> PutAgentAsync(AgentResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateResource(resource, AgentstrationResourceTypes.Agents);
        ValidateAgentProperties(resource.Properties);
        var expectedId = ResourceIdentifier.Create(resource.ResourceGroup!, AgentstrationProviderNamespaces.Agents, "agents", resource.Name).Value;
        if (!string.Equals(resource.Id, expectedId, StringComparison.Ordinal))
            throw new AgentDefinitionValidationException("resource_id_mismatch", $"Agent id must be '{expectedId}'.");

        var type = await store.GetAsync<AgentTypeResource>(resource.Properties.AgentType.ResourceId, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(resource.Properties.AgentType.ResourceId);
        if (resource.Properties.AgentType.Version is not null && type.Value.Properties.Version != resource.Properties.AgentType.Version)
            throw new AgentDefinitionValidationException("type_version_mismatch", "The referenced agent type version does not exist.");

        var existing = await store.GetAsync<AgentResource>(resource.Id, cancellationToken);
        ValidatePreconditions(existing, ifMatch, ifNoneMatch);
        if (existing is not null && DesiredStateEquals(existing.Value, resource)) return existing;

        var generation = existing is null ? 1 : checked(existing.Value.Generation + 1);
        var desired = resource with
        {
            Generation = generation,
            ETag = null,
            Status = new ResourceStatus
            {
                ProvisioningState = ProvisioningState.Accepted,
                Conditions =
                [
                    new ResourceCondition
                    {
                        Type = "ReferenceExistenceValidated",
                        Status = "Unknown",
                        Reason = "ProviderUnavailable",
                        Message = "AgentType existence was validated; model and tool provider existence validation is deferred.",
                        LastTransitionTime = timeProvider.GetUtcNow()
                    }
                ]
            }
        };
        var stored = await store.PutAsync(desired, ifMatch, ifNoneMatch, cancellationToken);
        if (existing is null)
            await eventBus.PublishAsync(new AgentCreated(stored.Value.Id, generation, timeProvider.GetUtcNow()), cancellationToken);
        else
            await eventBus.PublishAsync(new AgentUpdated(stored.Value.Id, generation, timeProvider.GetUtcNow()), cancellationToken);
        return stored;
    }

    public Task<StoredResource<AgentTypeResource>?> GetAgentTypeAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<AgentTypeResource>(resourceId, cancellationToken);
    public Task<StoredResource<AgentResource>?> GetAgentAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<AgentResource>(resourceId, cancellationToken);
    public Task<StoredResource<AgentDeployment>?> GetDeploymentAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<AgentDeployment>(resourceId, cancellationToken);
    public async Task DeleteAgentAsync(string resourceId, string? ifMatch, CancellationToken cancellationToken)
    {
        _ = await store.GetAsync<AgentResource>(resourceId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(resourceId);
        await store.DeleteAsync(resourceId, ifMatch, cancellationToken);
        await eventBus.PublishAsync(new AgentDeleted(resourceId, timeProvider.GetUtcNow()), cancellationToken);
    }

    public Task<IReadOnlyList<StoredResource<AgentResource>>> ListAgentsAsync(string resourceGroup, int skip, int take, CancellationToken cancellationToken) =>
        store.ListAsync<AgentResource>(AgentstrationResourceTypes.Agents, resourceGroup, skip, take, cancellationToken);

    public Task<IReadOnlyList<StoredResource<AgentTypeResource>>> ListAgentTypesAsync(string resourceGroup, int skip, int take, CancellationToken cancellationToken) =>
        store.ListAsync<AgentTypeResource>(AgentstrationResourceTypes.AgentTypes, resourceGroup, skip, take, cancellationToken);

    public async Task<StoredResource<AgentRevision>> CreateRevisionAsync(string agentResourceId, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        var agent = await store.GetAsync<AgentResource>(agentResourceId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(agentResourceId);
        var type = await store.GetAsync<AgentTypeResource>(agent.Value.Properties.AgentType.ResourceId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(agent.Value.Properties.AgentType.ResourceId);
        var resolved = compiler.Compile(type.Value.Properties, agent.Value, spec);
        var revisions = await store.ListAsync<AgentRevision>(AgentstrationResourceTypes.AgentRevisions, agent.Value.ResourceGroup, 0, 1000, cancellationToken);
        var number = revisions.Count(revision => string.Equals(revision.Value.AgentResourceId, agentResourceId, StringComparison.Ordinal)) + 1;
        var revisionName = $"{agent.Value.Name}--{number:000000}";
        var id = ResourceIdentifier.Create(agent.Value.ResourceGroup!, AgentstrationProviderNamespaces.Agents, "agentRevisions", revisionName).Value;
        var revision = new AgentRevision
        {
            Id = id,
            Name = revisionName,
            Type = AgentstrationResourceTypes.AgentRevisions,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = agent.Value.ResourceGroup,
            Location = agent.Value.Location,
            Tags = agent.Value.Tags,
            AgentResourceId = agentResourceId,
            AgentVersion = agent.Value.Generation,
            AgentTypeVersion = type.Value.Properties.Version,
            Definition = resolved,
            DefinitionHash = resolved.DefinitionHash,
            CreatedAt = timeProvider.GetUtcNow(),
            ProvisioningState = ProvisioningState.Succeeded
        };
        return await store.CreateImmutableAsync(revision, cancellationToken);
    }

    public async Task<StoredResource<AgentDeployment>> CreateDeploymentAsync(string resourceGroup, string name, string location, string revisionId, AgentDeploymentSpec spec, CancellationToken cancellationToken)
    {
        _ = await store.GetAsync<AgentRevision>(revisionId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(revisionId);
        var id = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "deployments", name).Value;
        var deployment = new AgentDeployment
        {
            Id = id,
            Name = name,
            Type = AgentstrationResourceTypes.Deployments,
            ApiVersion = ManagementApiVersions.V20260801,
            ResourceGroup = resourceGroup,
            Location = location,
            RevisionId = revisionId,
            Environment = spec.Environment,
            RuntimeProfileId = spec.RuntimeProfileId,
            HostingMode = spec.HostingMode,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Accepted,
            OperationalState = OperationalState.Starting,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await store.PutAsync(deployment, null, true, cancellationToken);
    }

    public Task<StoredResource<AgentDeployment>> StartAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken) =>
        SetDesiredStateAsync(deployment, DesiredAgentState.Running, cancellationToken);

    public Task<StoredResource<AgentDeployment>> StopAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken) =>
        SetDesiredStateAsync(deployment, DesiredAgentState.Stopped, cancellationToken);

    public async Task<StoredResource<AgentDeployment>> ReconcileAsync(StoredResource<AgentDeployment> deployment, CancellationToken cancellationToken)
    {
        var result = await reconciler.ReconcileAsync(deployment.Value, cancellationToken);
        if (!result.Changed) return deployment;
        return await store.PutAsync(result.Deployment with { UpdatedAt = timeProvider.GetUtcNow() }, deployment.ETag, false, cancellationToken);
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        var deployments = await store.ListAsync<AgentDeployment>(AgentstrationResourceTypes.Deployments, null, 0, 1000, cancellationToken);
        foreach (var deployment in deployments)
        {
            try { await ReconcileAsync(deployment, cancellationToken); }
            catch (ControlPlaneConcurrencyException) { }
        }
    }

    public async Task<(AgentRouteResult Route, RuntimeExecutionResult Execution)> RouteAndExecuteAsync(string input, CancellationToken cancellationToken)
    {
        var selected = await SelectAgentAsync(input, null, cancellationToken);
        var execution = await ExecuteSelectedAsync(selected, input, cancellationToken);
        return (selected.Route, execution);
    }

    public async Task<SelectedAgentRoute> SelectAgentAsync(string input, string? requestedAgentId, CancellationToken cancellationToken)
    {
        var deployments = await store.ListAsync<AgentDeployment>(AgentstrationResourceTypes.Deployments, null, 0, 1000, cancellationToken);
        var ready = deployments.Where(item => item.Value.DesiredState == DesiredAgentState.Running && item.Value.OperationalState == OperationalState.Ready).ToArray();
        var candidates = new List<RoutableAgent>();
        var deploymentByAgent = new Dictionary<string, AgentDeployment>(StringComparer.Ordinal);
        foreach (var item in ready)
        {
            var revision = await store.GetAsync<AgentRevision>(item.Value.RevisionId, cancellationToken);
            if (revision is null) continue;
            var definition = revision.Value.Definition;
            candidates.Add(new RoutableAgent(definition.AgentKey, definition.Description, definition.Capabilities));
            deploymentByAgent[definition.AgentKey] = item.Value;
        }
        if (candidates.Count == 0) throw new InvalidOperationException("No ready and routable agent deployment is available.");
        var route = string.IsNullOrWhiteSpace(requestedAgentId)
            ? await router.SelectAsync(new AgentRouteRequest(input), candidates, cancellationToken)
            : candidates.Any(candidate => string.Equals(candidate.AgentId, requestedAgentId, StringComparison.Ordinal))
                ? new AgentRouteResult(requestedAgentId, 1, "The caller explicitly requested this agent.")
                : throw new InvalidOperationException($"Requested agent '{requestedAgentId}' is not ready or does not exist.");
        if (!deploymentByAgent.TryGetValue(route.AgentId, out var selected)) throw new InvalidOperationException("The router selected an unknown agent.");
        return new SelectedAgentRoute(route, selected.Id);
    }

    public Task<RuntimeExecutionResult> ExecuteSelectedAsync(SelectedAgentRoute selected, string input, CancellationToken cancellationToken) =>
        runtimes.ExecuteAsync(selected.DeploymentId, new RuntimeExecutionRequest(input), cancellationToken);

    private async Task<StoredResource<AgentDeployment>> SetDesiredStateAsync(StoredResource<AgentDeployment> stored, DesiredAgentState state, CancellationToken cancellationToken)
    {
        var updated = stored.Value with
        {
            DesiredState = state,
            ProvisioningState = ProvisioningState.Accepted,
            OperationalState = state == DesiredAgentState.Running ? OperationalState.Starting : stored.Value.OperationalState,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await store.PutAsync(updated, stored.ETag, false, cancellationToken);
    }

    private static void ValidateResource(Resource resource, string expectedType)
    {
        if (!string.Equals(resource.Type, expectedType, StringComparison.Ordinal)) throw new AgentDefinitionValidationException("resource_type_mismatch", $"Expected resource type '{expectedType}'.");
        if (!string.Equals(resource.ApiVersion, ManagementApiVersions.V20260801, StringComparison.Ordinal)) throw new AgentDefinitionValidationException("api_version_not_supported", $"Supported API version is '{ManagementApiVersions.V20260801}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ResourceGroup);
        ValidateName(resource.Name, "name");
        ValidateName(resource.ResourceGroup, "resourceGroup");
        ValidateName(resource.Location, "location");
    }

    private static void ValidateAgentProperties(AgentProperties properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(properties.DisplayName);
        ValidateReference(properties.AgentType.ResourceId, AgentstrationProviderNamespaces.Agents, "agentTypes", "agent_type_reference_invalid");
        if (properties.AgentType.Version is <= 0)
            throw new AgentDefinitionValidationException("invalid_version", "Agent type version must be positive when specified.");
        ValidateReference(properties.ModelProfile.ResourceId, AgentstrationProviderNamespaces.Models, "modelProfiles", "model_profile_reference_invalid");

        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in properties.Tools)
        {
            ValidateReference(tool.ResourceId, AgentstrationProviderNamespaces.Tools, "tools", "tool_reference_invalid");
            if (!tools.Add(tool.ResourceId))
                throw new AgentDefinitionValidationException("duplicate_tool_reference", $"Tool reference '{tool.ResourceId}' is duplicated.");
        }
    }

    private static void ValidateReference(string value, string providerNamespace, string resourceType, string errorCode)
    {
        if (!ResourceIdentifier.TryParse(value, out var identifier)
            || !string.Equals(identifier.ProviderNamespace, providerNamespace, StringComparison.Ordinal)
            || !string.Equals(identifier.ResourceType, resourceType, StringComparison.Ordinal))
            throw new AgentDefinitionValidationException(errorCode, $"Resource reference must target '{providerNamespace}/{resourceType}'.");
    }

    private static void ValidateName(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new AgentDefinitionValidationException($"invalid_{field.ToLowerInvariant()}", $"{field} must contain only letters, digits, '.', '_' or '-' and be at most 128 characters.");
    }

    private static void ValidatePreconditions(StoredResource<AgentResource>? existing, string? ifMatch, bool ifNoneMatch)
    {
        if (existing is null && ifMatch is not null)
            throw new ControlPlaneConcurrencyException("If-Match cannot update a resource that does not exist.");
        if (existing is not null && ifNoneMatch)
            throw new ControlPlaneConcurrencyException("If-None-Match prevented replacement of an existing resource.");
        if (existing is not null && ifMatch is not null && !string.Equals(existing.ETag, ifMatch, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException("The supplied ETag does not match the current resource version.");
    }

    private static bool DesiredStateEquals(AgentResource left, AgentResource right) =>
        string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.ApiVersion, right.ApiVersion, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.ResourceGroup, right.ResourceGroup, StringComparison.Ordinal)
        && string.Equals(left.Location, right.Location, StringComparison.Ordinal)
        && DictionaryEquals(left.Tags, right.Tags)
        && string.Equals(left.Properties.DisplayName, right.Properties.DisplayName, StringComparison.Ordinal)
        && string.Equals(left.Properties.Description, right.Properties.Description, StringComparison.Ordinal)
        && left.Properties.AgentType == right.Properties.AgentType
        && string.Equals(left.Properties.AdditionalInstructions, right.Properties.AdditionalInstructions, StringComparison.Ordinal)
        && left.Properties.ModelProfile == right.Properties.ModelProfile
        && left.Properties.Tools.Select(tool => tool.ResourceId).SequenceEqual(right.Properties.Tools.Select(tool => tool.ResourceId), StringComparer.Ordinal)
        && JsonDictionaryEquals(left.Properties.Settings, right.Properties.Settings);

    private static bool DictionaryEquals<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right) where TKey : notnull =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && EqualityComparer<TValue>.Default.Equals(pair.Value, value));

    private static bool JsonDictionaryEquals(IReadOnlyDictionary<string, System.Text.Json.JsonElement> left, IReadOnlyDictionary<string, System.Text.Json.JsonElement> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && System.Text.Json.JsonElement.DeepEquals(pair.Value, value));
}
