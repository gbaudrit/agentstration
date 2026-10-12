using System.Text.Json;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;

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

public sealed class FlowService(
    IFlowRepository repository,
    TimeProvider timeProvider,
    IEnumerable<IFlowDeletionGuard> deletionGuards,
    IEnumerable<IFlowVersionActivationGuard>? activationGuards = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            command.Definition, Copy(command.Metadata), now, now, command.DisplayName, command.Graph);
        FlowValidator.Validate(resource);
        return await repository.CreateAsync(resource, cancellationToken);
    }

    public Task<StoredFlow?> GetAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) => repository.GetAsync(workspaceId, id, cancellationToken);
    public Task<StoredFlow?> GetExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken) => repository.GetExactAsync(scopeRef, id, cancellationToken);
    public Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, int skip, int take, CancellationToken cancellationToken) => repository.ListExactAsync(scopeRef, skip, take, cancellationToken);
    public Task<FlowPage> ListExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) => repository.ListExactAsync(scopeRef, @namespace, skip, take, cancellationToken);
    public Task<FlowPage> ListAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken) => ListAsync(workspaceId, ResourceNamespace.Default, skip, take, cancellationToken);
    public Task<FlowPage> ListAsync(WorkspaceId workspaceId, ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(workspaceId, @namespace, skip, take, cancellationToken);
    public Task<FlowPage> ListAllAsync(WorkspaceId workspaceId, int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(workspaceId, skip, take, cancellationToken);

    public async Task<StoredFlow> UpdateAsync(WorkspaceId workspaceId, FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
        => await UpdateExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, command, expectedETag, cancellationToken);

    public async Task<StoredFlow> UpdateExactAsync(ResourceScopeRef scopeRef, FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (!string.Equals(current.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var published = await repository.GetVersionExactAsync(scopeRef, id, command.Version, cancellationToken);
        if (published is not null && JsonSerializer.Serialize(published.Value.Definition, JsonOptions) != JsonSerializer.Serialize(command.Definition, JsonOptions))
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
            UpdatedAt = timeProvider.GetUtcNow()
        };
        FlowValidator.Validate(updated);
        return await repository.UpdateAsync(updated, expectedETag, cancellationToken);
    }

    public Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, CancellationToken cancellationToken) =>
        DeleteAsync(workspaceId, id, expectedETag, allowSystemManaged: false, cancellationToken);

    public async Task DeleteAsync(WorkspaceId workspaceId, FlowId id, string? expectedETag, bool allowSystemManaged, CancellationToken cancellationToken)
        => await DeleteExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, expectedETag, allowSystemManaged, cancellationToken);

    public async Task DeleteExactAsync(ResourceScopeRef scopeRef, FlowId id, string? expectedETag, bool allowSystemManaged, CancellationToken cancellationToken)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (IsSystemManaged(stored.Value.Metadata)
            && (!allowSystemManaged || !IsDirectAgentFlow(stored.Value.Metadata)))
            throw new FlowValidationException("system_flow_managed", $"Flow '{id}' is managed by Agentstration and cannot be deleted independently.");
        if (scopeRef.Kind == ResourceScopeKind.Workspace)
        {
            var workspaceId = new WorkspaceId(scopeRef.TargetId!.Value);
            foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(workspaceId, id, cancellationToken);
        }
        await repository.DeleteExactAsync(scopeRef, id, expectedETag, cancellationToken);
    }

    public async Task<StoredFlowVersion> PublishVersionAsync(WorkspaceId workspaceId, FlowId id, string version, bool activate, CancellationToken cancellationToken, string? releaseNotes = null)
        => await PublishVersionExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, version, activate, cancellationToken, releaseNotes);

    public async Task<StoredFlowVersion> PublishVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, bool activate, CancellationToken cancellationToken, string? releaseNotes = null)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (!string.Equals(stored.Value.Version, version, StringComparison.Ordinal))
            throw new FlowValidationException("flow_version_mismatch", "The requested version must match the current Flow definition version.");
        if (await repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken) is not null)
            throw new FlowValidationException("flow_version_already_published", $"Flow version '{version}' has already been published. Choose a new version.");
        var published = new FlowVersion(stored.Value.ScopeRef, id, version, stored.Value.Description, stored.Value.Definition, stored.Value.Metadata, timeProvider.GetUtcNow(), stored.Value.Graph,
            stored.Value.Graph is null ? null : FlowDefinitionHash.Compute(stored.Value.Graph), releaseNotes);
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
        return created;
    }

    public Task<StoredFlowVersion?> GetVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken) => repository.GetVersionAsync(workspaceId, id, version, cancellationToken);
    public Task<StoredFlowVersion?> GetVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken) => repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken);
    public async Task<StoredFlow> ActivateVersionAsync(WorkspaceId workspaceId, FlowId id, string version, CancellationToken cancellationToken)
        => await ActivateVersionExactAsync(ResourceScopeRef.Workspace(workspaceId.Value), id, version, cancellationToken);

    public async Task<StoredFlow> ActivateVersionExactAsync(ResourceScopeRef scopeRef, FlowId id, string version, CancellationToken cancellationToken)
    {
        var stored = await repository.GetExactAsync(scopeRef, id, cancellationToken) ?? throw new FlowNotFoundException(id);
        var published = await repository.GetVersionExactAsync(scopeRef, id, version, cancellationToken)
            ?? throw new FlowValidationException("flow_version_not_published", "The Flow version must be published before activation.");
        if (stored.Value.ActiveVersion == version) return stored;
        if (scopeRef.Kind == ResourceScopeKind.Workspace)
            foreach (var guard in activationGuards ?? []) await guard.ValidateActivationAsync(new(scopeRef.TargetId!.Value), published.Value, cancellationToken);
        return await repository.UpdateAsync(stored.Value with { ActiveVersion = version, Enabled = true, UpdatedAt = timeProvider.GetUtcNow() }, stored.ETag, cancellationToken);
    }
    public Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(WorkspaceId workspaceId, FlowId id, CancellationToken cancellationToken) => repository.ListVersionsAsync(workspaceId, id, cancellationToken);
    public Task<IReadOnlyList<StoredFlowVersion>> ListVersionsExactAsync(ResourceScopeRef scopeRef, FlowId id, CancellationToken cancellationToken) => repository.ListVersionsExactAsync(scopeRef, id, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(WorkspaceId workspaceId, FlowReference reference, CancellationToken cancellationToken)
        => await ResolveAsync(workspaceId, reference, reference.FlowId.Namespace, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(WorkspaceId workspaceId, FlowReference reference, ResourceNamespace ownerNamespace, CancellationToken cancellationToken)
    {
        FlowValidator.ValidateReference(reference);
        var flowId = reference.Resolve(ownerNamespace);
        var scopeRef = reference.ScopeRef ?? ResourceScopeRef.Workspace(workspaceId.Value);
        var version = reference.Version;
        if (version is null)
        {
            var flow = await repository.GetExactAsync(scopeRef, flowId, cancellationToken) ?? throw new FlowNotFoundException(flowId);
            version = flow.Value.ActiveVersion ?? throw new FlowValidationException("flow_active_version_missing", "The referenced Flow has no active version.");
        }
        return (await repository.GetVersionExactAsync(scopeRef, flowId, version, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Flow version '{flowId}:{version}' does not exist.");
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);

    private static bool IsSystemManaged(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("systemManaged", out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static bool IsDirectAgentFlow(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("systemKind", out var value) && string.Equals(value, "DirectAgentFlow", StringComparison.Ordinal);
}
