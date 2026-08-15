using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Flow.Application;

public sealed record CreateFlowCommand(string Name, string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null, FlowGraphDefinition? Graph = null, string? DisplayName = null);
public sealed record UpdateFlowCommand(string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null, FlowGraphDefinition? Graph = null, string? DisplayName = null);

public interface IFlowDeletionGuard
{
    Task ValidateDeleteAsync(FlowId flowId, CancellationToken cancellationToken);
}

public sealed class FlowService(IFlowRepository repository, TimeProvider timeProvider, IEnumerable<IFlowDeletionGuard> deletionGuards)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public async Task<StoredFlow> CreateAsync(CreateFlowCommand command, CancellationToken cancellationToken)
        => await CreateAsync(command, ResourceNamespace.Default, cancellationToken);

    public async Task<StoredFlow> CreateAsync(CreateFlowCommand command, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = timeProvider.GetUtcNow();
        var resource = new FlowResource(new FlowId(command.Name, @namespace), command.Name, command.Description, command.Version, command.Enabled, null,
            command.Definition, Copy(command.Metadata), now, now, command.DisplayName, command.Graph);
        FlowValidator.Validate(resource);
        return await repository.CreateAsync(resource, cancellationToken);
    }

    public Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken) => repository.GetAsync(id, cancellationToken);
    public Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken) => ListAsync(ResourceNamespace.Default, skip, take, cancellationToken);
    public Task<FlowPage> ListAsync(ResourceNamespace @namespace, int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(@namespace, skip, take, cancellationToken);
    public Task<FlowPage> ListAllAsync(int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(skip, take, cancellationToken);

    public async Task<StoredFlow> UpdateAsync(FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await repository.GetAsync(id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (!string.Equals(current.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var published = await repository.GetVersionAsync(id, command.Version, cancellationToken);
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

    public async Task DeleteAsync(FlowId id, string? expectedETag, CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (stored.Value.Metadata.TryGetValue("systemManaged", out var systemManaged) && bool.TryParse(systemManaged, out var isSystemManaged) && isSystemManaged)
            throw new FlowValidationException("system_flow_managed", $"Flow '{id}' is managed by Agentstration and cannot be deleted independently.");
        foreach (var guard in deletionGuards) await guard.ValidateDeleteAsync(id, cancellationToken);
        await repository.DeleteAsync(id, expectedETag, cancellationToken);
    }

    public async Task<StoredFlowVersion> PublishVersionAsync(FlowId id, string version, bool activate, CancellationToken cancellationToken, string? releaseNotes = null)
    {
        var stored = await repository.GetAsync(id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (!string.Equals(stored.Value.Version, version, StringComparison.Ordinal))
            throw new FlowValidationException("flow_version_mismatch", "The requested version must match the current Flow definition version.");
        var published = new FlowVersion(id, version, stored.Value.Description, stored.Value.Definition, stored.Value.Metadata, timeProvider.GetUtcNow(), stored.Value.Graph,
            stored.Value.Graph is null ? null : FlowDefinitionHash.Compute(stored.Value.Graph), releaseNotes);
        FlowValidator.ValidateVersion(published);
        var created = await repository.CreateVersionAsync(published, cancellationToken);
        if (activate)
        {
            var activated = stored.Value with { ActiveVersion = version, Enabled = true, UpdatedAt = timeProvider.GetUtcNow() };
            await repository.UpdateAsync(activated, stored.ETag, cancellationToken);
        }
        return created;
    }

    public Task<StoredFlowVersion?> GetVersionAsync(FlowId id, string version, CancellationToken cancellationToken) => repository.GetVersionAsync(id, version, cancellationToken);
    public Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(FlowId id, CancellationToken cancellationToken) => repository.ListVersionsAsync(id, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(FlowReference reference, CancellationToken cancellationToken)
        => await ResolveAsync(reference, reference.FlowId.Namespace, cancellationToken);

    public async Task<FlowVersion> ResolveAsync(FlowReference reference, ResourceNamespace ownerNamespace, CancellationToken cancellationToken)
    {
        FlowValidator.ValidateReference(reference);
        var flowId = reference.Resolve(ownerNamespace);
        var version = reference.Version;
        if (version is null)
        {
            var flow = await repository.GetAsync(flowId, cancellationToken) ?? throw new FlowNotFoundException(flowId);
            version = flow.Value.ActiveVersion ?? throw new FlowValidationException("flow_active_version_missing", "The referenced Flow has no active version.");
        }
        return (await repository.GetVersionAsync(flowId, version, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Flow version '{flowId}:{version}' does not exist.");
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);
}
