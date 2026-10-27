using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Security.Contracts;

namespace Agentstration.Flows.Application;

public sealed record CreateFlowCommand(string Name, string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null, FlowGraphDefinition? Graph = null, string? DisplayName = null);
public sealed record UpdateFlowCommand(string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null, FlowGraphDefinition? Graph = null, string? DisplayName = null);

public interface IFlowDeletionGuard
{
    Task ValidateDeleteAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken);
}

public interface IFlowVersionActivationGuard
{
    Task ValidateActivationAsync(WorkspaceId workspaceId, FlowVersion version, CancellationToken cancellationToken);
}

public interface IFlowAgentReferenceResolver
{
    Task<FlowTargetReference> ResolveAsync(ResourceScopeRef ownerScopeRef, ResourceNamespace ownerNamespace,
        FlowTargetReference reference, CancellationToken cancellationToken);
}

public interface IFlowDiscoveryPolicy
{
    Task<bool> CanDiscoverAsync(FlowResource flow, ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken);
}

public sealed class FlowService(
    IFlowRepository repository,
    TimeProvider timeProvider,
    IEnumerable<IFlowDeletionGuard> deletionGuards,
    ISchemaBindingResolver schemaBindingResolver,
    IEnumerable<IFlowVersionActivationGuard>? activationGuards = null,
    IEnumerable<IFlowScopeHierarchy>? scopeHierarchies = null,
    IFlowAgentReferenceResolver? agentReferences = null,
    IFlowDiscoveryPolicy? discoveryPolicy = null,
    IResourceGovernanceContext? governanceContext = null,
    ISecurityAuditWriter? audit = null,
    IFlowResourceReferenceResolver? resourceReferenceResolver = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IResourceGovernanceContext effectiveGovernanceContext = governanceContext ?? new ResourceGovernanceContext();

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public async Task<StoredFlow> CreateAsync(WorkspaceId workspaceId, CreateFlowCommand command, CancellationToken cancellationToken)
        => await CreateAsync(workspaceId, command, ResourceNamespace.Default, cancellationToken);

    public async Task<StoredFlow> CreateAsync(WorkspaceId workspaceId, CreateFlowCommand command, ResourceNamespace @namespace, CancellationToken cancellationToken)
        => await CreateExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), command, @namespace, cancellationToken);

    public async Task<StoredFlow> CreateExactAsync(ResourceScopeRef scopeRef, CreateFlowCommand command, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = scopeRef.Value;
        var now = timeProvider.GetUtcNow();
        var resource = new FlowResource(scopeRef, new FlowId(command.Name, @namespace), command.Name, command.Description, command.Version, command.Enabled, null,
            command.Definition, Copy(command.Metadata), now, now, command.DisplayName, command.Graph)
        {
            Governance = ResourceGovernanceEnforcement.PrepareCreate(ResourceGovernance.User, effectiveGovernanceContext.Current)
        };
        FlowValidator.Validate(resource);
        var created = await repository.CreateAsync(resource, cancellationToken);
        await AuditReconciliationAsync(resource.Governance, cancellationToken);
        return created;
    }

    public Task<StoredFlow?> GetAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) => repository.GetAsync(workspaceId, id, cancellationToken);
    public Task<StoredFlow?> GetExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken) => repository.GetExactAsync(scopeRef, id, cancellationToken);
    public Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, int skip, int take, CancellationToken cancellationToken) => repository.ListExactAsync(scopeRef, skip, take, cancellationToken);
    public Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) => repository.ListExactAsync(scopeRef, @namespace, skip, take, cancellationToken);
    public Task<FlowPage> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken) => ListAsync(workspaceId, ResourceNamespace.Default, skip, take, cancellationToken);
    public Task<FlowPage> ListAsync(WorkspaceId workspaceId, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(workspaceId, @namespace, skip, take, cancellationToken);
    public Task<FlowPage> ListAllAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(workspaceId, skip, take, cancellationToken);

    public async Task<StoredFlow?> GetVisibleAsync(
        ResourceScopeRef consumerScopeRef,
        FlowId id,
        ResourceScopeRef? exactScopeRef,
        CancellationToken cancellationToken)
    {
        var visibleScopes = await ListVisibleScopesAsync(consumerScopeRef, cancellationToken);
        if (exactScopeRef is { } scopeRef)
        {
            if (!visibleScopes.Contains(scopeRef))
                throw new FlowReferenceOutsideScopeException(scopeRef, consumerScopeRef);
            var exact = await repository.GetExactAsync(scopeRef, id, cancellationToken);
            return exact is not null && await CanDiscoverAsync(exact.Value, consumerScopeRef, cancellationToken)
                ? exact
                : null;
        }

        StoredFlow? result = null;
        foreach (var visibleScope in visibleScopes)
        {
            var candidate = await repository.GetExactAsync(visibleScope, id, cancellationToken);
            if (candidate is null) continue;
            if (!await CanDiscoverAsync(candidate.Value, consumerScopeRef, cancellationToken)) continue;
            if (result is not null)
                throw new FlowReferenceAmbiguousException(new FlowReference(id));
            result = candidate;
        }
        return result;
    }

    public async Task<FlowPage> ListVisibleAsync(
        ResourceScopeRef consumerScopeRef,
        ResourceNamespace? @namespace,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        take = Math.Min(take, 200);
        var targetCount = checked(skip + take + 1);
        var visibleScopes = await ListVisibleScopesAsync(consumerScopeRef, cancellationToken);
        var scopeOrder = visibleScopes.Select((scope, index) => (scope, index)).ToDictionary(value => value.scope, value => value.index);
        var values = new List<StoredFlow>();

        foreach (var scopeRef in visibleScopes)
        {
            var offset = 0;
            while (values.Count(value => value.Value.ScopeRef == scopeRef) < targetCount)
            {
                var pageSize = Math.Min(200, targetCount - offset);
                var page = @namespace is null
                    ? await repository.ListExactAsync(scopeRef, offset, pageSize, cancellationToken)
                    : await repository.ListExactAsync(scopeRef, @namespace.Value, offset, pageSize, cancellationToken);
                foreach (var item in page.Items)
                    if (await CanDiscoverAsync(item.Value, consumerScopeRef, cancellationToken))
                        values.Add(item);
                offset += page.Items.Count;
                if (!page.HasMore || page.Items.Count == 0) break;
            }
        }

        var ordered = values
            .OrderBy(value => value.Value.Id.Namespace.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Value.Id.Value, StringComparer.Ordinal)
            .ThenBy(value => scopeOrder[value.Value.ScopeRef])
            .Skip(skip)
            .Take(take + 1)
            .ToArray();
        return new FlowPage(ordered.Take(take).ToArray(), ordered.Length > take);
    }

    private Task<bool> CanDiscoverAsync(FlowResource flow, ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken) =>
        flow.ScopeRef == consumerScopeRef
            ? Task.FromResult(true)
            : discoveryPolicy?.CanDiscoverAsync(flow, consumerScopeRef, cancellationToken)
                ?? Task.FromResult(false);

    public async Task<StoredFlow> UpdateAsync(WorkspaceId workspaceId, FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
        => await UpdateExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, command, expectedETag, cancellationToken);

    public async Task<StoredFlow> UpdateExactAsync(ResourceScopeRef scopeRef, FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        await EnsureManagerAsync(current.Value.Governance, cancellationToken);
        if (!string.Equals(current.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var published = await repository.GetVersionExactAsync(scopeRef, id, command.Version, cancellationToken);
        if (published is not null
            && (JsonSerializer.Serialize(current.Value.Definition, JsonOptions) != JsonSerializer.Serialize(command.Definition, JsonOptions)
                || JsonSerializer.Serialize(current.Value.Graph, JsonOptions) != JsonSerializer.Serialize(command.Graph, JsonOptions)))
            throw new FlowValidationException("published_version_immutable", $"Published Flow version '{command.Version}' cannot be modified.");
        var updated = current.Value with
        {
            Description = command.Description,
            Version = command.Version,
            Enabled = command.Enabled,
            Definition = command.Definition,
            Metadata = Copy(command.Metadata),
            Graph = command.Graph,
            DisplayName = command.DisplayName ?? current.Value.DisplayName,
            UpdatedAt = timeProvider.GetUtcNow(),
            Governance = ResourceGovernanceEnforcement.PrepareUpdate(
                current.Value.Governance,
                ResourceGovernance.User,
                effectiveGovernanceContext.Current)
        };
        FlowValidator.Validate(updated);
        var stored = await repository.UpdateAsync(updated, expectedETag, cancellationToken);
        await AuditReconciliationAsync(updated.Governance, cancellationToken);
        return stored;
    }

    public Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, CancellationToken cancellationToken) =>
        DeleteAsync(workspaceId, id, expectedETag, allowSystemManaged: false, cancellationToken);

    public async Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, bool allowSystemManaged, CancellationToken cancellationToken)
        => await DeleteExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, expectedETag, allowSystemManaged, cancellationToken);

    public async Task DeleteExactAsync(ResourceScopeRef scopeRef, FlowId id, string? expectedETag, bool allowSystemManaged, CancellationToken cancellationToken)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        await EnsureManagerAsync(stored.Value.Governance, cancellationToken);
        if (IsSystemManaged(stored.Value.Metadata)
            && (!allowSystemManaged || !IsDirectAgentFlow(stored.Value.Metadata)))
            throw new FlowValidationException("system_flow_managed", $"Flow '{id}' is managed by Agentstration and cannot be deleted independently.");
        if (scopeRef.Kind == ResourceScopeKind.Workspace)
        {
            var workspaceId = new WorkspaceId(scopeRef.TargetId!.Value);
            foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(workspaceId, id, cancellationToken);
        }
        await repository.DeleteExactAsync(scopeRef, id, expectedETag, cancellationToken);
        await AuditReconciliationAsync(stored.Value.Governance, cancellationToken);
    }

    public async Task<StoredFlowVersion> PublishVersionAsync(WorkspaceId workspaceId, FlowId id, string version, bool activate, CancellationToken cancellationToken, string? releaseNotes = null)
        => await PublishVersionExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, version, activate, cancellationToken, releaseNotes);

    public async Task<StoredFlowVersion> PublishVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, bool activate, CancellationToken cancellationToken, string? releaseNotes = null)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        await EnsureManagerAsync(stored.Value.Governance, cancellationToken);
        if (!string.Equals(stored.Value.Version, version, StringComparison.Ordinal))
            throw new FlowValidationException("flow_version_mismatch", "The requested version must match the current Flow definition version.");
        if (await repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken) is not null)
            throw new FlowValidationException("flow_version_already_published", $"Flow version '{version}' has already been published. Choose a new version.");
        var definition = await PinAgentsAsync(scopeRef, id.Namespace, stored.Value.Definition, cancellationToken);
        var graph = stored.Value.Graph is null ? null : await PinAgentsAsync(scopeRef, id.Namespace, stored.Value.Graph, cancellationToken);
        IReadOnlyDictionary<string, SchemaBindingSnapshot>? schemaBindings = null;
        if (graph is not null)
        {
            var resolved = await new FlowSchemaBindingSnapshotter(schemaBindingResolver)
                .ResolveAsync(scopeRef, id, graph, cancellationToken);
            graph = await new FlowOutputContractSnapshotter(resourceReferenceResolver)
                .ResolveAsync(scopeRef, id, resolved.Graph, cancellationToken);
            schemaBindings = resolved.Snapshots;
        }
        var published = new FlowVersion(stored.Value.ScopeRef, id, version, stored.Value.Description, definition, stored.Value.Metadata, timeProvider.GetUtcNow(), graph,
            graph is null ? FlowDefinitionHash.Compute(definition) : FlowDefinitionHash.Compute(graph), releaseNotes, schemaBindings)
        {
            Governance = stored.Value.Governance
        };
        FlowValidator.ValidateVersion(published);
        if (activate)
            if (scopeRef.Kind == ResourceScopeKind.Workspace)
                foreach (var guard in activationGuards ?? []) await guard.ValidateActivationAsync(new(scopeRef.TargetId!.Value), published, cancellationToken);
        StoredFlowVersion created;
        try { created = await repository.CreateVersionAsync(published, cancellationToken); }
        catch (FlowConcurrencyException)
        {
            if (await repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken) is not null)
                throw new FlowValidationException("flow_version_already_published", $"Flow version '{version}' has already been published. Choose a new version.");
            throw;
        }
        if (activate)
        {
            var activated = stored.Value with { ActiveVersion = version, Enabled = true, UpdatedAt = timeProvider.GetUtcNow() };
            await repository.UpdateAsync(activated, stored.ETag, cancellationToken);
        }
        await AuditReconciliationAsync(stored.Value.Governance, cancellationToken);
        return created;
    }

    private async Task<FlowDefinition> PinAgentsAsync(ResourceScopeRef ownerScopeRef, ResourceNamespace ownerNamespace,
        FlowDefinition definition, CancellationToken cancellationToken)
    {
        if (agentReferences is null) return definition;
        async Task<FlowTargetReference> Resolve(FlowTargetReference reference) => reference.Kind == FlowTargetKind.Agent
            ? await agentReferences.ResolveAsync(ownerScopeRef, ownerNamespace, reference, cancellationToken)
            : reference;
        return definition switch
        {
            DirectFlowDefinition direct => direct with { Target = await Resolve(direct.Target) },
            RoutingFlowDefinition routing => routing with
            {
                Destinations = await Task.WhenAll(routing.Destinations.Select(Resolve)),
                Fallback = routing.Fallback is null ? null : await Resolve(routing.Fallback)
            },
            WorkflowFlowDefinition workflow => workflow with
            {
                Nodes = await Task.WhenAll(workflow.Nodes.Select(async node => node.Target is null
                    ? node
                    : node with { Target = await Resolve(node.Target) }))
            },
            OrchestrationFlowDefinition orchestration => orchestration with
            {
                Participants = await Task.WhenAll(orchestration.Participants.Select(Resolve)),
                Pattern = orchestration.Pattern is MagenticOrchestrationPattern magentic
                    ? magentic with { Manager = await Resolve(magentic.Manager) }
                    : orchestration.Pattern
            },
            _ => definition
        };
    }

    private async Task<FlowGraphDefinition> PinAgentsAsync(ResourceScopeRef ownerScopeRef, ResourceNamespace ownerNamespace,
        FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        if (agentReferences is null) return graph;
        async Task<FlowResourceReference> Resolve(FlowResourceReference reference)
        {
            var resolved = await agentReferences.ResolveAsync(ownerScopeRef, ownerNamespace,
                new(FlowTargetKind.Agent, reference.ResourceId,
                    reference.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), reference.Namespace,
                    reference.ScopeRef), cancellationToken);
            return reference with
            {
                Namespace = resolved.Namespace,
                ScopeRef = resolved.ScopeRef,
                Version = long.Parse(resolved.Version!, System.Globalization.CultureInfo.InvariantCulture),
                ResourceUid = resolved.ResourceUid,
                RevisionUid = resolved.RevisionUid,
                DefinitionHash = resolved.DefinitionHash
            };
        }
        var steps = await Task.WhenAll(graph.Steps.Select(async step => step switch
        {
            AgentFlowStepDefinition agent when !agent.Agent.ResourceId.StartsWith("${", StringComparison.Ordinal) =>
                agent with { Agent = await Resolve(agent.Agent) },
            RouterFlowStepDefinition router => router with
            {
                Candidates = await Task.WhenAll(router.Candidates.Select(async candidate =>
                    candidate with { Agent = await Resolve(candidate.Agent) })),
                Fallback = router.Fallback is null ? null : await Resolve(router.Fallback)
            },
            _ => step
        }));
        return graph with { Steps = steps };
    }

    public Task<StoredFlowVersion?> GetVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken) => repository.GetVersionAsync(workspaceId, id, version, cancellationToken);
    public Task<StoredFlowVersion?> GetVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken) => repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken);
    public async Task<StoredFlow> ActivateVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken)
        => await ActivateVersionExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, version, cancellationToken);

    public async Task<StoredFlow> ActivateVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        await EnsureManagerAsync(stored.Value.Governance, cancellationToken);
        var published = await repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken)
            ?? throw new FlowValidationException("flow_version_not_published", "The Flow version must be published before activation.");
        if (stored.Value.ActiveVersion == version) return stored;
        if (scopeRef.Kind == ResourceScopeKind.Workspace)
            foreach (var guard in activationGuards ?? []) await guard.ValidateActivationAsync(new(scopeRef.TargetId!.Value), published.Value, cancellationToken);
        var activated = await repository.UpdateAsync(stored.Value with { ActiveVersion = version, Enabled = true, UpdatedAt = timeProvider.GetUtcNow() }, stored.ETag, cancellationToken);
        await AuditReconciliationAsync(stored.Value.Governance, cancellationToken);
        return activated;
    }
    public Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) => repository.ListVersionsAsync(workspaceId, id, cancellationToken);
    public Task<IReadOnlyList<StoredFlowVersion>> ListVersionsExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken) => repository.ListVersionsExactAsync(scopeRef, id, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(WorkspaceId workspaceId, FlowReference reference, CancellationToken cancellationToken)
        => await ResolveAsync(workspaceId, reference, reference.FlowId.Namespace, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(WorkspaceId workspaceId, FlowReference reference, ResourceNamespace ownerNamespace, CancellationToken cancellationToken)
        => await ResolveAsync(ResourceScopeRef.Workspace(workspaceId.Value), reference, ownerNamespace, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(ResourceScopeRef consumerScopeRef, FlowReference reference, ResourceNamespace ownerNamespace, CancellationToken cancellationToken)
    {
        FlowValidator.ValidateReference(reference);
        var flowId = reference.Resolve(ownerNamespace);
        var visibleScopes = await ListVisibleScopesAsync(consumerScopeRef, cancellationToken);
        StoredFlow? flow;
        if (reference.ScopeRef is { } exactScopeRef)
        {
            if (!visibleScopes.Contains(exactScopeRef))
                throw new FlowReferenceOutsideScopeException(exactScopeRef, consumerScopeRef);
            flow = await repository.GetExactAsync(exactScopeRef, flowId, cancellationToken);
        }
        else
        {
            flow = null;
            foreach (var visibleScope in visibleScopes)
            {
                var candidate = await repository.GetExactAsync(visibleScope, flowId, cancellationToken);
                if (candidate is null) continue;
                if (flow is not null) throw new FlowReferenceAmbiguousException(reference);
                flow = candidate;
            }
        }
        if (flow is null) throw new FlowNotFoundException(flowId);

        var version = reference.Version;
        if (version is null)
            version = flow.Value.ActiveVersion ?? throw new FlowValidationException("flow_active_version_missing", "The referenced Flow has no active version.");
        return (await repository.GetVersionExactAsync(flow.Value.ScopeRef, flowId, version, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Flow version '{flowId}:{version}' does not exist.");
    }

    private async Task<IReadOnlyList<ResourceScopeRef>> ListVisibleScopesAsync(
        ResourceScopeRef consumerScopeRef,
        CancellationToken cancellationToken)
    {
        var hierarchy = scopeHierarchies?.SingleOrDefault();
        var visibleScopes = hierarchy is null
            ? [consumerScopeRef]
            : await hierarchy.ListVisibleAsync(consumerScopeRef, cancellationToken);
        if (visibleScopes.Count == 0 || visibleScopes[0] != consumerScopeRef || visibleScopes.Distinct().Count() != visibleScopes.Count)
            throw new InvalidOperationException($"The visible Flow scope chain for '{consumerScopeRef}' is invalid.");
        return visibleScopes;
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);

    private async Task EnsureManagerAsync(ResourceGovernance governance, CancellationToken cancellationToken)
    {
        try { ResourceGovernanceEnforcement.EnsureManager(governance, effectiveGovernanceContext.Current); }
        catch (ResourceGovernanceViolationException exception)
        {
            if (audit is not null)
                await audit.WriteAsync(new(SecurityAuditActions.ManagedResourceMutationDenied,
                    SecurityAuditOutcome.Failed, ReasonCode: exception.Code), cancellationToken);
            throw;
        }
    }

    private Task AuditReconciliationAsync(ResourceGovernance governance, CancellationToken cancellationToken) =>
        audit is null || !governance.IsManaged
            ? Task.CompletedTask
            : audit.WriteAsync(new(SecurityAuditActions.ManagedResourceReconciled), cancellationToken);

    private static bool IsSystemManaged(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("systemManaged", out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static bool IsDirectAgentFlow(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("systemKind", out var value) && string.Equals(value, "DirectAgentFlow", StringComparison.Ordinal);
}
