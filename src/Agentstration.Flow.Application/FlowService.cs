using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;

namespace Agentstration.Flow.Application;

public sealed record CreateFlowCommand(string Name, string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record UpdateFlowCommand(string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null, FlowGraphDefinition? Graph = null, string? DisplayName = null, string ResourceGroup = "default", string Location = "local");

public interface IFlowDeletionGuard
{
    Task ValidateDeleteAsync(FlowId flowId, CancellationToken cancellationToken);
}

public sealed class FlowService(IFlowRepository repository, TimeProvider timeProvider, IEnumerable<IFlowDeletionGuard> deletionGuards)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public async Task<StoredFlow> CreateAsync(CreateFlowCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = timeProvider.GetUtcNow();
        var definition = new FlowDefinition(new FlowId(command.Name), command.Name, command.Description, command.Kind, command.Version, command.Enabled, null,
            command.Spec, Copy(command.Metadata), now, now);
        FlowValidator.Validate(definition);
        return await repository.CreateAsync(definition, cancellationToken);
    }

    public Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken) => repository.GetAsync(id, cancellationToken);
    public Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken) => repository.ListAsync(skip, take, cancellationToken);

    public async Task<StoredFlow> UpdateAsync(FlowId id, UpdateFlowCommand command, string expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await repository.GetAsync(id, cancellationToken) ?? throw new FlowNotFoundException(id);
        if (!string.Equals(current.ETag, expectedETag, StringComparison.Ordinal)) throw new FlowConcurrencyException("The supplied ETag does not match the current Flow version.");
        var published = await repository.GetVersionAsync(id, command.Version, cancellationToken);
        if (published is not null && (published.Value.Kind != command.Kind || JsonSerializer.Serialize(published.Value.Spec, JsonOptions) != JsonSerializer.Serialize(command.Spec, JsonOptions)))
            throw new FlowValidationException("published_version_immutable", $"Published Flow version '{command.Version}' cannot be modified.");
        var updated = current.Value with
        {
            Description = command.Description,
            Kind = command.Kind,
            Version = command.Version,
            Enabled = command.Enabled,
            Spec = command.Spec,
            Metadata = Copy(command.Metadata),
            Graph = command.Graph,
            DisplayName = command.DisplayName ?? current.Value.DisplayName,
            ResourceGroup = command.ResourceGroup,
            Location = command.Location,
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
        var published = new FlowVersion(id, version, stored.Value.Description, stored.Value.Kind, stored.Value.Spec, stored.Value.Metadata, timeProvider.GetUtcNow(), stored.Value.Graph,
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
    {
        FlowValidator.ValidateReference(reference);
        var version = reference.Version;
        if (version is null)
        {
            var flow = await repository.GetAsync(reference.FlowId, cancellationToken) ?? throw new FlowNotFoundException(reference.FlowId);
            version = flow.Value.ActiveVersion ?? throw new FlowValidationException("flow_active_version_missing", "The referenced Flow has no active version.");
        }
        return (await repository.GetVersionAsync(reference.FlowId, version, cancellationToken))?.Value
            ?? throw new FlowValidationException("flow_version_not_found", $"Flow version '{reference.FlowId}:{version}' does not exist.");
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);
}
